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
    private const string ResetFailedKey = "Trading.PaperResetFailedFormat";

    #endregion Resx Keys

    #region Private Fields

    private readonly BalancesAggregationService _balances;
    private readonly IPaperResetService _paperReset;
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

    private string? _outcomeMessage;

    /// <summary>
    /// The finished paper reset's summary line (T-192): the reload's fresh
    /// figures sit beside it. Rides until the next reset flow or the panel
    /// close — an ordinary refresh neither confirms nor clears it.
    /// </summary>
    public string? OutcomeMessage
    {
        get => _outcomeMessage;
        private set
        {
            if (OutcomeMessage == value)
            {
                return;
            }

            _outcomeMessage = value;
            this.RaisePropertyChanged(nameof(OutcomeMessage));
            this.RaisePropertyChanged(nameof(HasOutcomeMessage));
        }
    }

    /// <summary>True while <see cref="OutcomeMessage"/> holds text; drives the outcome line visibility.</summary>
    public bool HasOutcomeMessage => _outcomeMessage is not null;

    /// <summary>Builds and applies one aggregation snapshot (panel opening and manual refresh share it).</summary>
    public ReactiveCommand<RxVoid, RxVoid> LoadCommand { get; }

    /// <summary>Runs the paper reset flow (T-192): prefill, dialog, service, outcome.</summary>
    public ReactiveCommand<RxVoid, RxVoid> ResetPaperCommand { get; }

    /// <summary>
    /// The dialog handoff of the reset flow: the input is the persisted
    /// starting balance (the prefill), the output the parsed choice — null
    /// when the dialog closed without one (a cancel is a silent untouched
    /// account, never a reported fault).
    /// </summary>
    public Interaction<decimal, decimal?> ResetRequested { get; }

    #endregion Properties

    #region Constructor

    /// <summary>
    /// Create a balances panel view model. The view model is transient:
    /// a fresh instance re-queries every adapter on each panel open.
    /// </summary>
    public BalancesPanelViewModel(
        BalancesAggregationService balances,
        IPaperResetService paperReset,
        ILocalizationManager localization,
        ILogger<BalancesPanelViewModel> logger)
    {
        _balances = balances ?? throw new ArgumentNullException(nameof(balances));
        _paperReset = paperReset ?? throw new ArgumentNullException(nameof(paperReset));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        LoadCommand = ReactiveCommand.CreateFromTask(HandleLoadAsync);
        ResetPaperCommand = ReactiveCommand.CreateFromTask(HandleResetPaperAsync);
        ResetRequested = new Interaction<decimal, decimal?>();
    }

    #endregion Constructor

    #region Private Methods

    private async Task HandleResetPaperAsync(CancellationToken ct)
    {
        // A fresh flow replaces whatever a previous one reported.
        ErrorMessage = null;
        OutcomeMessage = null;

        try
        {
            var prefill = await _paperReset.GetStartingBalanceAsync(ct);

            // The dialog handoff: a closed-without-choice dialog (cancel,
            // title bar X) declines the flow silently — the account stays
            // exactly as it was, nothing is reported.
            var chosen = await ResetRequested.Handle(prefill);
            if (chosen is null)
            {
                _logger.LogInformation(
                    "Balances panel: the reset dialog closed without a choice; nothing touched");
                return;
            }

            var outcome = await _paperReset.ResetAsync(chosen.Value, ct);
            if (!outcome.Done)
            {
                // A refused confirmation stays silent (the reset changed
                // nothing).
                _logger.LogInformation(
                    "Balances panel: the reset confirmation was refused; nothing touched");
                return;
            }

            OutcomeMessage = outcome.Summary;

            // The fresh figures: the shared load pipeline re-queries every
            // source (the reset already reported its own faults).
            await HandleLoadAsync(ct);
        }
        catch (Exception ex)
        {
            if (ex is OperationCanceledException)
            {
                _logger.LogInformation(ex, "Balances panel: the paper reset was cancelled before completing");
            }
            else
            {
                _logger.LogError(ex, "Balances panel: the paper reset failed");
            }

            ErrorMessage = _localization.GetString(ResetFailedKey, ex.Message);
        }
    }

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
