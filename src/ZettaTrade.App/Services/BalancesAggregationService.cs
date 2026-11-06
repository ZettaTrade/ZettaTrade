namespace ZettaTrade.App.Services;

using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// The balances aggregation service: builds one snapshot that covers every
/// active exchange adapter (the facade's registered ones plus the trading
/// face) and sums the served amounts per asset. The single responsibility
/// is that snapshot — the sources are taken as they come, a source without
/// a trading face reports the explicit not-provided state, a faulting
/// balance query reports the fault, and zero is never substituted for
/// absence or failure.
/// </summary>
public sealed class BalancesAggregationService
{
    #region Private Fields

    private readonly IReadOnlyList<IMarketDataAdapter> _registeredAdapters;
    private readonly ITradingAdapter _trading;
    private readonly ILogger<BalancesAggregationService> _logger;

    #endregion Private Fields

    #region Constructor

    /// <summary>
    /// Create the service over the active-exchange composition: the
    /// registered adapters (display order preserved) and the trading face
    /// the whole app trades through.
    /// </summary>
    public BalancesAggregationService(
        IReadOnlyList<IMarketDataAdapter> registeredAdapters,
        ITradingAdapter trading,
        ILogger<BalancesAggregationService> logger)
    {
        _registeredAdapters = registeredAdapters ?? throw new ArgumentNullException(nameof(registeredAdapters));
        _trading = trading ?? throw new ArgumentNullException(nameof(trading));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    #endregion Constructor

    #region Public Methods

    /// <summary>
    /// Load one balances snapshot: query every source that carries a
    /// trading face, mark the rest not-provided, and sum the served amounts
    /// per asset. Only the provided sources enter the aggregate, so an
    /// adapter without balance data can never contribute a zero.
    /// </summary>
    public async Task<BalancesSnapshot> LoadSnapshotAsync(CancellationToken ct = default)
    {
        var sources = new List<BalanceSource>(_registeredAdapters.Count + 1);

        // A trading adapter also registered on the facade must join once:
        // a duplicate id would query the same balances again and double
        // every asset it serves in the aggregate.
        var joinedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var adapter in _registeredAdapters)
        {
            sources.Add(await LoadSourceAsync(adapter, ct));
            joinedIds.Add(adapter.Id);
        }

        if (joinedIds.Add(_trading.Id))
        {
            sources.Add(await LoadSourceAsync(_trading, ct));
        }

        var aggregated = sources
            .Where(source => source.Availability == BalanceAvailability.Provided)
            .SelectMany(source => source.Balances)
            .GroupBy(served => served.Key, StringComparer.Ordinal)
            .OrderBy(assetGroup => assetGroup.Key, StringComparer.Ordinal)
            .Select(assetGroup => new AggregatedBalance
            {
                Asset = assetGroup.Key,
                Amount = assetGroup.Sum(served => served.Value.Amount),
            })
            .ToArray();

        return new BalancesSnapshot
        {
            Sources = sources,
            Aggregated = aggregated,
        };
    }

    #endregion Public Methods

    #region Private Methods

    /// <summary>
    /// Load one source: a trading adapter's served balances become the
    /// provided state; a market-data-only adapter cannot query balances at
    /// all and reports the explicit not-provided state; a failing query
    /// reports the fault. Neither path substitutes an empty (zero) result
    /// for what the adapter could not or did not answer.
    /// </summary>
    private async Task<BalanceSource> LoadSourceAsync(IMarketDataAdapter adapter, CancellationToken ct)
    {
        if (adapter is not ITradingAdapter trading)
        {
            _logger.LogInformation(
                "Balances: '{Name}' carries no trading face; balances are not provided",
                adapter.Name);
            return BalanceSource.NotProvided(adapter.Id, adapter.Name);
        }

        try
        {
            var balances = await trading.GetBalancesAsync(ct);
            _logger.LogInformation(
                "Balances: '{Name}' served {Count} asset entries",
                adapter.Name,
                balances.Count);
            return BalanceSource.Provided(adapter.Id, adapter.Name, balances);
        }

        // Source-scoped faults stay source-scoped: one broken query must
        // not abort the other exchanges' amounts, and cancellation still
        // propagates so the caller's cancellation semantics stay honest.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(
                ex,
                "Balances: '{Name}' failed the balance query; the fault is reported instead of zeros",
                adapter.Name);
            return BalanceSource.FetchFailed(adapter.Id, adapter.Name, ex.Message);
        }
    }

    #endregion Private Methods
}
