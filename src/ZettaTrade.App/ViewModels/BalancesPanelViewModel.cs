namespace ZettaTrade.App.ViewModels;

using System.Globalization;
using System.Linq;
using Livery.Localization;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ReactiveUI.Primitives;
using ZettaTrade.App.Services;

/// <summary>
/// Balances panel view model (modal window). One aggregation snapshot per
/// load — manual refresh and panel opening share <see cref="LoadCommand"/>
/// — feeds both the per-exchange sections and the aggregated view. Every
/// source displays what its adapter actually serves: provided rows, the
/// explicit not-provided state, or the localized fault of a failed query;
/// a zero is never substituted for absence or failure. Window plumbing
/// stays in the View.
/// </summary>
public sealed class BalancesPanelViewModel : ReactiveObject
{
    #region Resx Keys

    private const string LoadFailedKey = "Trading.BalancesLoadFailedFormat";
    private const string FetchFailedKey = "Trading.BalancesFetchFailedFormat";

    #endregion Resx Keys

    #region Private Fields

    private readonly BalancesAggregationService _balances;
    private readonly ILocalizationManager _localization;
    private readonly ILogger<BalancesPanelViewModel> _logger;

    #endregion Private Fields

    #region Properties

    private bool _isLoading;
    private bool _hasLoaded;
    private string? _errorMessage;
    private IReadOnlyList<BalancesSourceViewModel> _sources = [];
    private IReadOnlyList<BalanceRowViewModel> _aggregated = [];

    /// <summary>True while a snapshot load is in flight (the loading state).</summary>
    public bool IsLoading
    {
        get => _isLoading;
        private set => this.RaiseAndSetIfChanged(ref _isLoading, value);
    }

    /// <summary>True once the first snapshot has landed; refreshes never fall back to the empty initial state.</summary>
    public bool HasLoaded
    {
        get => _hasLoaded;
        private set => this.RaiseAndSetIfChanged(ref _hasLoaded, value);
    }

    /// <summary>Per-exchange balance outcomes of the last loaded snapshot.</summary>
    public IReadOnlyList<BalancesSourceViewModel> Sources
    {
        get => _sources;
        private set => this.RaiseAndSetIfChanged(ref _sources, value);
    }

    /// <summary>Per-asset sums across the sources that serve balances, of the last loaded snapshot.</summary>
    public IReadOnlyList<BalanceRowViewModel> Aggregated
    {
        get => _aggregated;
        private set => this.RaiseAndSetIfChanged(ref _aggregated, value);
    }

    /// <summary>True while <see cref="Aggregated"/> is non-empty; drives the aggregate table visibility.</summary>
    public bool HasAggregated => _aggregated.Count > 0;

    /// <summary>The whole-load failure line (a per-source fault is scoped to its source instead).</summary>
    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (ErrorMessage == value)
            {
                return;
            }

            _errorMessage = value;
            this.RaisePropertyChanged(nameof(ErrorMessage));
            this.RaisePropertyChanged(nameof(HasErrorMessage));
        }
    }

    /// <summary>True while <see cref="ErrorMessage"/> holds text; drives the error line visibility.</summary>
    public bool HasErrorMessage => _errorMessage is not null;

    /// <summary>Builds and applies one aggregation snapshot (panel opening and manual refresh share it).</summary>
    public ReactiveCommand<RxVoid, RxVoid> LoadCommand { get; }

    #endregion Properties

    #region Constructor

    /// <summary>
    /// Create a balances panel view model. The view model is transient:
    /// a fresh instance re-queries every adapter on each panel open.
    /// </summary>
    public BalancesPanelViewModel(
        BalancesAggregationService balances,
        ILocalizationManager localization,
        ILogger<BalancesPanelViewModel> logger)
    {
        _balances = balances ?? throw new ArgumentNullException(nameof(balances));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        LoadCommand = ReactiveCommand.CreateFromTask(HandleLoadAsync);
    }

    #endregion Constructor

    #region Private Methods

    private async Task HandleLoadAsync(CancellationToken ct)
    {
        // A fresh load replaces whatever a previous flow reported.
        ErrorMessage = null;
        IsLoading = true;

        try
        {
            var snapshot = await _balances.LoadSnapshotAsync(ct);

            Sources = snapshot.Sources.Select(CreateSourceViewModel).ToArray();
            Aggregated = snapshot.Aggregated.Select(CreateAggregateRow).ToArray();
            HasLoaded = true;

            _logger.LogInformation(
                "Balances panel: loaded {SourceCount} exchange sources, {AggregateCount} aggregated assets",
                Sources.Count,
                Aggregated.Count);
        }
        catch (Exception ex)
        {
            // Broader than the aggregation's own source-scoped faults: the
            // snapshot call itself rides the adapters as easily as a
            // network request, so the panel must report it instead of
            // silently keeping the stale sections. (Per-source faults stay
            // source-scoped inside the service — they never throw here.)
            _logger.LogError(ex, "Balances panel: load failed");
            ErrorMessage = _localization.GetString(LoadFailedKey, ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Build the section record for one source, with its served rows precomputed.</summary>
    private BalancesSourceViewModel CreateSourceViewModel(BalanceSource source)
    {
        var rows = source.Availability == BalanceAvailability.Provided
            ? source.Balances
                .Select(served => new BalanceRowViewModel
                {
                    Asset = served.Key,
                    Amount = served.Value.Amount.ToString(CultureInfo.InvariantCulture),
                })
                .OrderBy(row => row.Asset, StringComparer.Ordinal)
                .ToArray()
            : [];

        return new BalancesSourceViewModel
        {
            AdapterName = source.AdapterName,
            IsProvided = source.Availability == BalanceAvailability.Provided,
            IsNotProvided = source.Availability == BalanceAvailability.NotProvided,
            IsFailed = source.Availability == BalanceAvailability.FetchFailed,
            FailureMessage = source.Availability == BalanceAvailability.FetchFailed
                ? _localization.GetString(FetchFailedKey, source.Failure)
                : null,
            Rows = rows,
        };
    }

    /// <summary>Build the display row for one aggregated asset.</summary>
    private BalanceRowViewModel CreateAggregateRow(AggregatedBalance balance) => new()
    {
        Asset = balance.Asset,
        Amount = balance.Amount.ToString(CultureInfo.InvariantCulture),
    };

    #endregion Private Methods
}
