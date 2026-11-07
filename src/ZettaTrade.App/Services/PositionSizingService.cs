namespace ZettaTrade.App.Services;

using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// Snapshot capture for the order ticket's position sizing helper: the ONE
/// component that may ask the trading face for sizing facts, and it only
/// does so at capture moments — the ticket opening, a symbol commit and the
/// row's deliberate refresh. A sizing click performs zero network calls:
/// every proposal is computed from the captured
/// <see cref="SizingBalanceSnapshot"/> / reference close plus the ticket's
/// own inputs. A failed capture surfaces as an explicit absence (null) —
/// the row displays the missing reference instead of substituting zeros.
/// </summary>
public sealed class PositionSizingService
{
    #region Private Fields

    private readonly IInstrumentMetadataProvider _metadata;
    private readonly ITradingAdapter _trading;
    private readonly ILogger<PositionSizingService> _logger;

    #endregion Private Fields

    #region Constructor

    /// <summary>
    /// Create the service over the round-size metadata seam and the trading
    /// face (paper today): captures ride the adapter the whole app trades
    /// through, so the helper sees exactly the balances it would propose
    /// against.
    /// </summary>
    public PositionSizingService(
        IInstrumentMetadataProvider metadata,
        ITradingAdapter trading,
        ILogger<PositionSizingService> logger)
    {
        _metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        _trading = trading ?? throw new ArgumentNullException(nameof(trading));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    #endregion Constructor

    #region Public Methods

    /// <summary>
    /// The round-size metadata the app holds for the instrument, or null
    /// when none exists (the row stays hidden for exactly those
    /// instruments). A registry read — no I/O, no network.
    /// </summary>
    public InstrumentSizeMetadata? FindMetadata(Symbol symbol) =>
        _metadata.Find(symbol);

    /// <summary>
    /// Capture one balance snapshot for the instrument: query the trading
    /// face once and keep what it serves for the instrument's base and
    /// quote assets. An unserved asset stays an explicit null; a failed
    /// capture surfaces as an absent snapshot (null) with the fault
    /// logged, never a fabricated zero set. Cancellation propagates to the
    /// caller (a cancelled open is not a service failure).
    /// </summary>
    public async Task<SizingBalanceSnapshot?> CaptureBalanceSnapshotAsync(
        Symbol symbol,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(symbol);

        IReadOnlyDictionary<string, Money> balances;
        try
        {
            balances = await _trading.GetBalancesAsync(ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Position sizer: the balance snapshot for {Symbol} failed; the ticket shows an absent reference",
                symbol.Normalized);
            return null;
        }

        if (balances is null)
        {
            _logger.LogWarning(
                "Position sizer: the trading face served no balance dictionary for {Symbol}; the ticket shows an absent reference",
                symbol.Normalized);
            return null;
        }

        var capturedAt = DateTimeOffset.UtcNow;
        var quote = symbol.QuoteAsset;
        if (quote is null)
        {
            // A stock symbol (no quote asset) can not name the quote-side
            // balance; the row would be hidden anyway (metadata-gated), so
            // an honest absence is the answer.
            return null;
        }

        return new SizingBalanceSnapshot(
            symbol,
            symbol.BaseAsset,
            quote,
            FindAmount(balances, quote),
            FindAmount(balances, symbol.BaseAsset),
            capturedAt);
    }

    /// <summary>
    /// Capture the market reference close for the instrument (the newest
    /// H1 bar's close, the same selection contract every order-building
    /// flow rides): the entry price estimate a sizing proposal falls back
    /// to when the order type carries no explicit execution price (market
    /// orders). A failed capture surfaces as an explicit absence (null)
    /// with the fault logged, never a stale figure re-served.
    /// </summary>
    public async Task<Price?> CaptureReferencePriceAsync(
        Symbol symbol,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(symbol);

        try
        {
            return await MarketReference.GetLatestCloseAsync(_trading, symbol, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Position sizer: the reference close for {Symbol} failed; the ticket shows an absent entry estimate",
                symbol.Normalized);
            return null;
        }
    }

    #endregion Public Methods

    #region Private Methods

    /// <summary>The served amount of one asset code, or null when the source did not carry it.</summary>
    private static decimal? FindAmount(IReadOnlyDictionary<string, Money> balances, string asset) =>
        balances.TryGetValue(asset, out var served) ? served.Amount : null;

    #endregion Private Methods
}
