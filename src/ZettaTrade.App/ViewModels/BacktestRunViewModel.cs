namespace ZettaTrade.App.ViewModels;

using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using Livery.Extensions;
using Livery.Localization;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ZettaTrade.App.Services;
using ZettaTrade.Backtest;
using ZettaTrade.Charting;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.Strategies;
/// <summary>The ReactiveUI status byte the commands exchange (the app standard alias).</summary>
using RxVoid = ReactiveUI.Primitives.RxVoid;

/// <summary>
/// The backtest run/progress view model (T-113): composes one offline
/// deterministic run request — strategy from the T-111 catalog, the active
/// venue's cached series identity, seed, bars and execution policy — starts
/// it through the harness, renders the mid-run progress and the terminal
/// report, and cancels the in-flight replay cooperatively. A cancelled run
/// surfaces no report: the stop is the outcome, nothing half-run is ever
/// shown.
/// </summary>
/// <remarks>
/// View lifecycle note: the model owns only dynamic state (phase, progress
/// text, the report) and the localized renderings re-resolve live on every
/// language change — never a static localized string bound once. The stream
/// subscriptions are transient per window: the window activation calls
/// <see cref="AttachRunEvents"/> and disposes the returned handle on close
/// (the notification-history attach pattern), so the singleton harness never
/// retains a dead window's subscription.
/// </remarks>
public sealed class BacktestRunViewModel : ReactiveObject
{
    #region Resx Keys

    private const string StatusIdleKey = "Backtest.Status.Idle";
    private const string StatusRunningKey = "Backtest.Status.Running";
    private const string StatusCompletedKey = "Backtest.Status.Completed";
    private const string StatusCancelledKey = "Backtest.Status.Cancelled";
    private const string StatusFailedKey = "Backtest.Status.Failed";
    private const string ProgressFormatKey = "Backtest.Progress.Format";
    private const string InvalidSymbolKey = "Backtest.Error.InvalidSymbol";
    private const string InvalidBarsKey = "Backtest.Error.InvalidBars";
    private const string InvalidSeedKey = "Backtest.Error.InvalidSeed";
    private const string InvalidSlippageKey = "Backtest.Error.InvalidSlippage";
    private const string InvalidFractionKey = "Backtest.Error.InvalidFraction";
    private const string StartFailedKey = "Backtest.Error.StartFailed";
    private const string NoStrategyKey = "Backtest.Error.NoStrategy";
    private const string SummaryFormatKey = "Backtest.Report.Summary.Format";
    private const string SweepFractionParamKey = "Backtest.Sweep.Param.Fraction";
    private const string SweepSlippageParamKey = "Backtest.Sweep.Param.Slippage";
    private const string SweepErrorValuesKey = "Backtest.Sweep.Error.Values";
    private const string SweepErrorSameParameterKey = "Backtest.Sweep.Error.SameParameter";
    private const string SweepErrorParameterKey = "Backtest.Sweep.Error.Parameter";
    private const string SweepErrorCountKey = "Backtest.Sweep.Error.Count";
    private const string SweepErrorRangeKey = "Backtest.Sweep.Error.Range";
    private const string SweepProgressFormatKey = "Backtest.Sweep.Progress.Format";
    private const string SweepDoneFormatKey = "Backtest.Sweep.Done.Format";
    private const string WalkForwardDoneFormatKey = "Backtest.WalkForward.Done.Format";
    private const string WalkForwardErrorFoldsKey = "Backtest.WalkForward.Error.Folds";
    private const string WalkForwardErrorFoldRangeKey = "Backtest.WalkForward.Error.FoldRange";
    private const string WalkForwardProgressFormatKey = "Backtest.WalkForward.Progress.Format";
    private const string MonteCarloErrorPathsKey = "Backtest.MonteCarlo.Error.Paths";
    private const string MonteCarloErrorSeedKey = "Backtest.MonteCarlo.Error.Seed";
    private const string MonteCarloErrorNoReportKey = "Backtest.MonteCarlo.Error.NoReport";
    private const string MonteCarloHeadlineFormatKey = "Backtest.MonteCarlo.Headline.Format";
    private const string MonteCarloBelowStartFormatKey = "Backtest.MonteCarlo.BelowStart.Format";

    #endregion Resx Keys

    #region Constants

    /// <summary>The symbol spelling the request composes with until the user pins one.</summary>
    private const string DefaultSymbolText = "BTC/USDT";

    /// <summary>The slippage spelling that maps to zero (the paper reference fills).</summary>
    private const decimal DefaultFraction = BacktestRunRequest.DefaultLongNotionalFraction;

    /// <summary>The first axis' values spelling the sweep form opens with (the sizing grid).</summary>
    private const string SweepFirstValuesDefault = "0.25, 0.5, 0.75, 1";

    /// <summary>The second axis' values spelling the sweep form opens with (the cost grid).</summary>
    private const string SweepSecondValuesDefault = "0, 0.5, 1";

    /// <summary>The folds spelling the walk-forward form opens with (the honest even carve's count —
    /// the engine's own <see cref="BacktestWalkForwardRequest.DefaultFolds"/>).</summary>
    private const string WalkForwardFoldsDefaultText = "4";

    /// <summary>The paths spelling the Monte Carlo form opens with (the acceptance's own shape —
    /// the engine's own <see cref="BacktestMonteCarloRequest.DefaultPaths"/>).</summary>
    private const string MonteCarloPathsDefaultText = "10000";

    /// <summary>The sampling seed spelling the Monte Carlo form opens with (the engine's own
    /// <see cref="BacktestMonteCarloRequest.DefaultSeed"/>).</summary>
    private const string MonteCarloSeedDefaultText = "0";

    #endregion Constants

    #region Private Fields

    private readonly IStrategyCatalog _strategies;
    private readonly IBacktestService _backtest;
    private readonly ActiveExchangeService _exchange;
    private readonly ILocalizationManager _localization;
    private readonly ILogger<BacktestRunViewModel> _logger;

    /// <summary>The cancellation source of the in-flight run; null while none runs.</summary>
    private CancellationTokenSource? _runCancellation;

    private IReadOnlyList<string> _strategyIds = [];
    private string _strategyId = string.Empty;
    private string _symbolText = DefaultSymbolText;
    private TimeFrame _selectedTimeFrame = TimeFrame.H1;
    private string _barsText = BacktestRunRequest.DefaultBars.ToString(System.Globalization.CultureInfo.InvariantCulture);
    private string _seedText = BacktestRunRequest.DefaultSeed.ToString(System.Globalization.CultureInfo.InvariantCulture);
    private string _slippageText = BacktestRunRequest.DefaultSlippageValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
    private string _fractionText = DefaultFraction.ToString(System.Globalization.CultureInfo.InvariantCulture);
    private BacktestRunPhase _phase = BacktestRunPhase.Idle;
    private string _statusText = string.Empty;
    private string _progressText = string.Empty;
    private double _progressFraction;
    private BacktestReport? _lastReport;
    private string _reportText = string.Empty;
    private string _summaryText = string.Empty;
    private IReadOnlyList<EquityCurvePoint> _equityPoints = [];
    private string? _errorMessage;
    private IReadOnlyList<string> _sweepParameters = [];
    private int _sweepFirstIndex;
    private int _sweepSecondIndex = 1;
    private string _sweepFirstValuesText = SweepFirstValuesDefault;
    private string _sweepSecondValuesText = SweepSecondValuesDefault;
    private IReadOnlyList<BacktestSweepRowViewModel> _sweepRows = [];
    private BacktestSweepRowViewModel? _selectedSweepRow;
    private int _sweepTotal;
    private int _sweepCompletedRows;
    private int? _sweepDoneRows;
    private int? _sweepDoneTotal;

    /// <summary>The folds spelling of the walk-forward form (parsed and range-checked at start).</summary>
    private string _foldsText = WalkForwardFoldsDefaultText;

    /// <summary>The paths spelling of the Monte Carlo form (parsed and range-checked at start).</summary>
    private string _monteCarloPathsText = MonteCarloPathsDefaultText;

    /// <summary>The sampling seed spelling of the Monte Carlo form (the sampling's own knob).</summary>
    private string _monteCarloSeedText = MonteCarloSeedDefaultText;

    private IReadOnlyList<BacktestWalkForwardRowViewModel> _walkForwardRows = [];
    private int _walkForwardTotal;
    private int _walkForwardCompletedFolds;
    private int? _walkForwardDoneRows;
    private int? _walkForwardDoneTotal;

    /// <summary>The completed sampling's folded distribution (T-254): the tiles'
    /// source; null while no sampling completed (a cancelled sampling
    /// answers nothing, exactly a cancelled run).</summary>
    private BacktestMonteCarloDistribution? _monteCarloDistribution;

    private string _monteCarloHeadline = string.Empty;
    private string _monteCarloBelowStart = string.Empty;
    private string _monteCarloFinalEquityP5 = string.Empty;
    private string _monteCarloFinalEquityP25 = string.Empty;
    private string _monteCarloFinalEquityMedian = string.Empty;
    private string _monteCarloFinalEquityP75 = string.Empty;
    private string _monteCarloFinalEquityP95 = string.Empty;
    private string _monteCarloDrawdownP5 = string.Empty;
    private string _monteCarloDrawdownP25 = string.Empty;
    private string _monteCarloDrawdownMedian = string.Empty;
    private string _monteCarloDrawdownP75 = string.Empty;
    private string _monteCarloDrawdownP95 = string.Empty;

    /// <summary>The completed sweep's own best report (T-263): rank 1's full
    /// replay, folded on the sweep's own loaded window — the auto-selected
    /// surface renders it directly, with no second run that could only read
    /// the cache's shifted current state. Cleared on the next start.</summary>
    private BacktestReport? _sweepBestReport;

    /// <summary>The click-through's generation (T-263): the newest open
    /// request owns the report surface — a superseded replay's continuation
    /// never renders over the newer surface.</summary>
    private int _reportGeneration;

    #endregion Private Fields

    #region Constructors

    /// <summary>Create a run/progress view model over the backtest harness.</summary>
    /// <param name="strategies">The T-111 strategy catalog (the ids the picker offers).</param>
    /// <param name="backtest">The offline deterministic harness the run starts on.</param>
    /// <param name="exchange">The active-exchange facade (the venue the replayed series is cached under).</param>
    /// <param name="localization">The localized strings resolver (live language switching).</param>
    /// <param name="logger">The view model's logger.</param>
    public BacktestRunViewModel(
        IStrategyCatalog strategies,
        IBacktestService backtest,
        ActiveExchangeService exchange,
        ILocalizationManager localization,
        ILogger<BacktestRunViewModel> logger)
    {
        _strategies = strategies ?? throw new ArgumentNullException(nameof(strategies));
        _backtest = backtest ?? throw new ArgumentNullException(nameof(backtest));
        _exchange = exchange ?? throw new ArgumentNullException(nameof(exchange));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        StartCommand = ReactiveCommand.CreateFromTask<RxVoid, RxVoid>(
            (_, cancellationToken) => HandleStartAsync(cancellationToken),
            canExecute: this.WhenAnyValue(x => x.IsRunning).Select(static running => !running));
        CancelCommand = ReactiveCommand.Create<RxVoid, RxVoid>(
            _ => HandleCancel(),
            canExecute: this.WhenAnyValue(x => x.IsRunning));
        StartSweepCommand = ReactiveCommand.CreateFromTask<RxVoid, RxVoid>(
            (_, cancellationToken) => HandleStartSweepAsync(cancellationToken),
            canExecute: this.WhenAnyValue(x => x.IsRunning).Select(static running => !running));
        StartWalkForwardCommand = ReactiveCommand.CreateFromTask<RxVoid, RxVoid>(
            (_, cancellationToken) => HandleStartWalkForwardAsync(cancellationToken),
            canExecute: this.WhenAnyValue(x => x.IsRunning).Select(static running => !running));
        StartMonteCarloCommand = ReactiveCommand.CreateFromTask<RxVoid, RxVoid>(
            (_, cancellationToken) => HandleStartMonteCarloAsync(cancellationToken),
            canExecute: this.WhenAnyValue(x => x.IsRunning, x => x.HasReport, static (running, hasReport) => !running && hasReport));

        // The strategy picker arms before any rendering: the catalog's scan
        // is a one-time session snapshot, so a cheap refetch is all the
        // honesty a re-opened surface needs (the registration itself rides
        // AttachRunEvents below — the window owns the disposal).
        RefreshStrategies();
        RenderLocalizedState();
    }

    #endregion Constructors

    #region Request Form

    /// <summary>The canonical time frames the interval picker offers (the chart's list).</summary>
    public IReadOnlyList<TimeFrame> TimeFrames { get; } =
    [
        TimeFrame.M1,
        TimeFrame.M5,
        TimeFrame.M15,
        TimeFrame.M30,
        TimeFrame.H1,
        TimeFrame.H4,
        TimeFrame.D1,
        TimeFrame.W1,
        TimeFrame.MN1,
    ];

    /// <summary>The strategy ids the catalog holds, load order (the T-111 scan's snapshot).</summary>
    public IReadOnlyList<string> StrategyIds
    {
        get => _strategyIds;
        private set => this.RaiseAndSetIfChanged(ref _strategyIds, value);
    }

    /// <summary>The canonical strategy id the next run evaluates.</summary>
    public string StrategyId
    {
        get => _strategyId;
        set
        {
            if (value is null)
            {
                // The bind/init race's null write (T-225): a null is never
                // a user choice — the standing id stays and the deferred
                // raise re-asserts it into the picker once its items exist.
                SelectionReAssert.Later(this, nameof(StrategyId));
                return;
            }

            this.RaiseAndSetIfChanged(ref _strategyId, value);
        }
    }

    /// <summary>The symbol spelling the next run parses (normalized on success, refused on garbage).</summary>
    public string SymbolText
    {
        get => _symbolText;
        set => this.RaiseAndSetIfChanged(ref _symbolText, value);
    }

    /// <summary>The time frame the next run replays.</summary>
    public TimeFrame SelectedTimeFrame
    {
        get => _selectedTimeFrame;
        set => this.RaiseAndSetIfChanged(ref _selectedTimeFrame, value);
    }

    /// <summary>The bar target spelling of the next run (parsed and validated at start).</summary>
    public string BarsText
    {
        get => _barsText;
        set => this.RaiseAndSetIfChanged(ref _barsText, value);
    }

    /// <summary>The seeded replay's seed spelling (any int; the same seed reproduces the same curve).</summary>
    public string SeedText
    {
        get => _seedText;
        set => this.RaiseAndSetIfChanged(ref _seedText, value);
    }

    /// <summary>The seeded slippage magnitude spelling (zero = the exact paper reference fills).</summary>
    public string SlippageText
    {
        get => _slippageText;
        set => this.RaiseAndSetIfChanged(ref _slippageText, value);
    }

    /// <summary>The long-side sizing fraction spelling (validated by the request).</summary>
    public string LongFractionText
    {
        get => _fractionText;
        set => this.RaiseAndSetIfChanged(ref _fractionText, value);
    }

    #endregion Request Form

    #region Sweep Form

    /// <summary>The sweep picker's knob labels, declared order (the enum's defined values).</summary>
    public IReadOnlyList<string> SweepParameters
    {
        get => _sweepParameters;
        private set => this.RaiseAndSetIfChanged(ref _sweepParameters, value);
    }

    /// <summary>The first axis' knob (the picker's index into <see cref="SweepParameters"/>).</summary>
    public int SweepFirstIndex
    {
        get => _sweepFirstIndex;
        set => this.RaiseAndSetIfChanged(ref _sweepFirstIndex, value);
    }

    /// <summary>The second axis' knob (the picker's index into <see cref="SweepParameters"/>).</summary>
    public int SweepSecondIndex
    {
        get => _sweepSecondIndex;
        set => this.RaiseAndSetIfChanged(ref _sweepSecondIndex, value);
    }

    /// <summary>The first axis' values spelling (a comma-separated list, parsed at start).</summary>
    public string SweepFirstValuesText
    {
        get => _sweepFirstValuesText;
        set => this.RaiseAndSetIfChanged(ref _sweepFirstValuesText, value);
    }

    /// <summary>The second axis' values spelling (a comma-separated list, parsed at start).</summary>
    public string SweepSecondValuesText
    {
        get => _sweepSecondValuesText;
        set => this.RaiseAndSetIfChanged(ref _sweepSecondValuesText, value);
    }

    /// <summary>The ranked result table's rows (best net first; empty until a sweep produced rows).</summary>
    public IReadOnlyList<BacktestSweepRowViewModel> SweepRows
    {
        get => _sweepRows;
        private set
        {
            this.RaiseAndSetIfChanged(ref _sweepRows, value);
            this.RaisePropertyChanged(nameof(HasSweepRows));
        }
    }

    /// <summary>True while the result table carries rows.</summary>
    public bool HasSweepRows => _sweepRows.Count > 0;

    /// <summary>
    /// The table's selected row: the click-through (T-263). Rank 1 of a
    /// completed sweep shows the sweep's own best report (no second run —
    /// the engine folded it on the sweep's own window); any other row
    /// re-replays its parameterization through the single-run lane and
    /// swaps the report in when the replay completes. A replay in flight
    /// owns the surface: a newer open request supersedes it (the stale
    /// continuation never renders), a click while one runs waits.
    /// </summary>
    public BacktestSweepRowViewModel? SelectedSweepRow
    {
        get => _selectedSweepRow;
        set
        {
            if (ReferenceEquals(_selectedSweepRow, value))
            {
                return;
            }

            _selectedSweepRow = value;
            this.RaisePropertyChanged(nameof(SelectedSweepRow));
            if (value is { } selected)
            {
                // The click-through's task is observed, never discarded: the
                // handler logs what the method's own catches could not (the
                // standing no-fire-and-forget rule).
                OpenSweepRowReportAsync(selected).ToObservable().Subscribe(
                    _ => { },
                    ex => _logger.LogError(ex, "The sweep row click-through stopped unexpectedly"));
            }
        }
    }

    /// <summary>Start one deterministic parameter sweep over the composed grid.</summary>
    public ReactiveCommand<RxVoid, RxVoid> StartSweepCommand { get; }

    #endregion Sweep Form

    #region Robustness Form (T-254)

    /// <summary>The folds spelling of the walk-forward form (parsed and range-checked at start).</summary>
    public string FoldsText
    {
        get => _foldsText;
        set => this.RaiseAndSetIfChanged(ref _foldsText, value);
    }

    /// <summary>The paths spelling of the Monte Carlo form (parsed and range-checked at start).</summary>
    public string MonteCarloPathsText
    {
        get => _monteCarloPathsText;
        set => this.RaiseAndSetIfChanged(ref _monteCarloPathsText, value);
    }

    /// <summary>The sampling seed spelling of the Monte Carlo form (the sampling's own knob —
    /// the same report over the same seed folds the same distribution).</summary>
    public string MonteCarloSeedText
    {
        get => _monteCarloSeedText;
        set => this.RaiseAndSetIfChanged(ref _monteCarloSeedText, value);
    }

    /// <summary>Start one walk-forward study over the sweep form's own grid (T-254).</summary>
    public ReactiveCommand<RxVoid, RxVoid> StartWalkForwardCommand { get; }

    /// <summary>Sample the report surface's completed report (T-254).</summary>
    public ReactiveCommand<RxVoid, RxVoid> StartMonteCarloCommand { get; }

    /// <summary>The walk-forward's chronological fold rows (empty until a study produced rows).</summary>
    public IReadOnlyList<BacktestWalkForwardRowViewModel> WalkForwardRows
    {
        get => _walkForwardRows;
        private set
        {
            this.RaiseAndSetIfChanged(ref _walkForwardRows, value);
            this.RaisePropertyChanged(nameof(HasWalkForwardRows));
        }
    }

    /// <summary>True while the walk-forward table carries rows.</summary>
    public bool HasWalkForwardRows => _walkForwardRows.Count > 0;

    /// <summary>True while a completed sampling carries its distribution (the tiles panel).</summary>
    public bool HasMonteCarlo => _monteCarloDistribution is not null;

    /// <summary>The distribution's localized headline line ("paths over trades, seed").</summary>
    public string MonteCarloHeadline
    {
        get => _monteCarloHeadline;
        private set => this.RaiseAndSetIfChanged(ref _monteCarloHeadline, value);
    }

    /// <summary>The distribution's localized below-start line ("k of n paths ended below the start").</summary>
    public string MonteCarloBelowStart
    {
        get => _monteCarloBelowStart;
        private set => this.RaiseAndSetIfChanged(ref _monteCarloBelowStart, value);
    }

    /// <summary>The final-equity percentiles, rendered in the current culture.</summary>
    public string MonteCarloFinalEquityP5
    {
        get => _monteCarloFinalEquityP5;
        private set => this.RaiseAndSetIfChanged(ref _monteCarloFinalEquityP5, value);
    }

    /// <summary>The final-equity percentiles, rendered in the current culture.</summary>
    public string MonteCarloFinalEquityP25
    {
        get => _monteCarloFinalEquityP25;
        private set => this.RaiseAndSetIfChanged(ref _monteCarloFinalEquityP25, value);
    }

    /// <summary>The final-equity percentiles, rendered in the current culture.</summary>
    public string MonteCarloFinalEquityMedian
    {
        get => _monteCarloFinalEquityMedian;
        private set => this.RaiseAndSetIfChanged(ref _monteCarloFinalEquityMedian, value);
    }

    /// <summary>The final-equity percentiles, rendered in the current culture.</summary>
    public string MonteCarloFinalEquityP75
    {
        get => _monteCarloFinalEquityP75;
        private set => this.RaiseAndSetIfChanged(ref _monteCarloFinalEquityP75, value);
    }

    /// <summary>The final-equity percentiles, rendered in the current culture.</summary>
    public string MonteCarloFinalEquityP95
    {
        get => _monteCarloFinalEquityP95;
        private set => this.RaiseAndSetIfChanged(ref _monteCarloFinalEquityP95, value);
    }

    /// <summary>The max-drawdown percentiles, rendered in the current culture.</summary>
    public string MonteCarloDrawdownP5
    {
        get => _monteCarloDrawdownP5;
        private set => this.RaiseAndSetIfChanged(ref _monteCarloDrawdownP5, value);
    }

    /// <summary>The max-drawdown percentiles, rendered in the current culture.</summary>
    public string MonteCarloDrawdownP25
    {
        get => _monteCarloDrawdownP25;
        private set => this.RaiseAndSetIfChanged(ref _monteCarloDrawdownP25, value);
    }

    /// <summary>The max-drawdown percentiles, rendered in the current culture.</summary>
    public string MonteCarloDrawdownMedian
    {
        get => _monteCarloDrawdownMedian;
        private set => this.RaiseAndSetIfChanged(ref _monteCarloDrawdownMedian, value);
    }

    /// <summary>The max-drawdown percentiles, rendered in the current culture.</summary>
    public string MonteCarloDrawdownP75
    {
        get => _monteCarloDrawdownP75;
        private set => this.RaiseAndSetIfChanged(ref _monteCarloDrawdownP75, value);
    }

    /// <summary>The max-drawdown percentiles, rendered in the current culture.</summary>
    public string MonteCarloDrawdownP95
    {
        get => _monteCarloDrawdownP95;
        private set => this.RaiseAndSetIfChanged(ref _monteCarloDrawdownP95, value);
    }

    #endregion Robustness Form (T-254)

    #region Run Surface

    /// <summary>True while a replay is in flight (the progress surface).</summary>
    public bool IsRunning => _phase == BacktestRunPhase.Running;

    /// <summary>The lifecycle phase of the last run this model drove.</summary>
    public BacktestRunPhase Phase
    {
        get => _phase;
        private set
        {
            this.RaiseAndSetIfChanged(ref _phase, value);
            this.RaisePropertyChanged(nameof(IsRunning));
            RenderLocalizedState();
        }
    }

    /// <summary>The localized phase line (re-resolves live on language changes).</summary>
    public string StatusText
    {
        get => _statusText;
        private set => this.RaiseAndSetIfChanged(ref _statusText, value);
    }

    /// <summary>The mid-run progress line ("n of m candles replayed"); empty while no run is in flight.</summary>
    public string ProgressText
    {
        get => _progressText;
        private set => this.RaiseAndSetIfChanged(ref _progressText, value);
    }

    /// <summary>The mid-run progress fraction for the bar, clamped to [0, 1].</summary>
    public double ProgressFraction
    {
        get => _progressFraction;
        private set => this.RaiseAndSetIfChanged(ref _progressFraction, value);
    }

    /// <summary>The last completed run's report; absent for a cancelled or failed run.</summary>
    public BacktestReport? LastReport
    {
        get => _lastReport;
        private set
        {
            this.RaiseAndSetIfChanged(ref _lastReport, value);
            this.RaisePropertyChanged(nameof(HasReport));
        }
    }

    /// <summary>True while the last run produced a report.</summary>
    public bool HasReport => _lastReport is not null;

    /// <summary>True while the last report carries at least one equity point.</summary>
    public bool HasEquity => _equityPoints.Count > 0;

    /// <summary>The last report's canonical text (the byte-stable serialization).</summary>
    public string ReportText
    {
        get => _reportText;
        private set => this.RaiseAndSetIfChanged(ref _reportText, value);
    }

    /// <summary>The last report's localized headline summary line.</summary>
    public string SummaryText
    {
        get => _summaryText;
        private set => this.RaiseAndSetIfChanged(ref _summaryText, value);
    }

    /// <summary>The last report's mark-to-market equity curve, rendered in the share chart control.</summary>
    public IReadOnlyList<EquityCurvePoint> EquityPoints
    {
        get => _equityPoints;
        private set
        {
            this.RaiseAndSetIfChanged(ref _equityPoints, value);
            this.RaisePropertyChanged(nameof(HasEquity));
        }
    }

    /// <summary>The localized refusal or failure line of the last start; null while clean.</summary>
    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (_errorMessage == value)
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

    /// <summary>Start one offline deterministic replay with the composed request.</summary>
    public ReactiveCommand<RxVoid, RxVoid> StartCommand { get; }

    /// <summary>Cancel the in-flight replay cooperatively (clean stop, no artifacts).</summary>
    public ReactiveCommand<RxVoid, RxVoid> CancelCommand { get; }

    #endregion Run Surface

    #region Lifecycle

    /// <summary>
    /// Attach the transient window's listeners: the harness's event stream
    /// (the progress line) and the language change stream (the phase line
    /// re-resolves live on language changes), plus the strategy snapshot
    /// refresh. The caller owns the returned handle — the
    /// notification-center attach pattern; the singleton harness never
    /// retains the dead window's subscription.
    /// </summary>
    /// <returns>The attach handle; disposing stops the run-event and language mirrors.</returns>
    public IDisposable AttachRunEvents()
    {
        var composite = new CompositeDisposable();

        composite.Add(_backtest.Events
            .ObserveOnUiThread()
            .Subscribe(
                OnRunEvent,
                ex => _logger.LogError(ex, "The backtest run-event stream stopped unexpectedly")));

        composite.Add(_localization.LanguageChanged
            .ObserveOnUiThread()
            .Subscribe(
                _ => RenderLocalizedState(),
                ex => _logger.LogError(ex, "The backtest language mirror stopped unexpectedly")));

        RefreshStrategies();
        return composite;
    }

    /// <summary>Refetch the catalog's ids (cheap: the scan is a session snapshot).</summary>
    private void RefreshStrategies()
    {
        StrategyIds = _strategies.All.Select(strategy => strategy.Id).ToArray();
        if (_strategyId.Length == 0)
        {
            // The picker lands on the first offered strategy; the user
            // decides from there — never a hidden default evaluation.
            StrategyId = StrategyIds.Count > 0 ? StrategyIds[0] : string.Empty;
        }
    }

    #endregion Lifecycle

    #region Command Handlers

    private async Task<RxVoid> HandleStartAsync(CancellationToken ct)
    {
        if (!TryComposeRequest(out var request, out var inputError))
        {
            // The unparsable or malformed input is refused up front — nothing defaults.
            ErrorMessage = inputError;
            Phase = BacktestRunPhase.Failed;
            return RxVoid.Default;
        }

        ErrorMessage = null;
        ProgressText = string.Empty;
        ProgressFraction = 0d;
        LastReport = null;
        ReportText = string.Empty;
        SummaryText = string.Empty;
        EquityPoints = [];
        SelectedSweepRow = null;
        SweepRows = [];
        _sweepBestReport = null;
        WalkForwardRows = [];
        _walkForwardDoneRows = null;
        _walkForwardDoneTotal = null;
        ClearMonteCarlo();
        Phase = BacktestRunPhase.Running;
        _runCancellation = new CancellationTokenSource();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _runCancellation.Token);
        try
        {
            var result = await _backtest.RunAsync(request, linked.Token);
            ApplyResult(result);
        }
        catch (OperationCanceledException) when (_runCancellation.IsCancellationRequested)
        {
            // The user's stop: the cancelled run surfaces no report.
            LastReport = null;
            Phase = BacktestRunPhase.Cancelled;
        }
        catch (Exception ex)
        {
            // A refused or failed start says so explicitly (never defaults).
            _logger.LogError(ex, "Backtest run failed for '{Strategy}'", request.StrategyId);
            ErrorMessage = _localization.GetString(StartFailedKey, ex.Message);
            Phase = BacktestRunPhase.Failed;
        }
        finally
        {
            _runCancellation.Dispose();
            _runCancellation = null;
            _sweepTotal = 0;
            _sweepCompletedRows = 0;
        }

        return RxVoid.Default;
    }

    /// <summary>
    /// The sweep start: compose the grid from the sweep form (every spelling
    /// parsed and range-checked explicitly — the standing refuse-not-default
    /// rule), then run it through the harness's sweep lane. The same phase
    /// machine, the same cancel command and the same report surface ride
    /// along; the stop lands between parameterizations, so a cancelled sweep
    /// keeps every row whose replay had completed.
    /// </summary>
    private async Task<RxVoid> HandleStartSweepAsync(CancellationToken ct)
    {
        if (!TryComposeSweep(out var sweep, out var inputError))
        {
            // The unparsable or malformed input is refused up front — nothing defaults.
            ErrorMessage = inputError;
            Phase = BacktestRunPhase.Failed;
            return RxVoid.Default;
        }

        ErrorMessage = null;
        ProgressText = string.Empty;
        ProgressFraction = 0d;
        LastReport = null;
        ReportText = string.Empty;
        SummaryText = string.Empty;
        EquityPoints = [];
        SelectedSweepRow = null;
        SweepRows = [];
        _sweepBestReport = null;
        _sweepDoneRows = null;
        _sweepDoneTotal = null;
        WalkForwardRows = [];
        _walkForwardDoneRows = null;
        _walkForwardDoneTotal = null;
        ClearMonteCarlo();
        Phase = BacktestRunPhase.Running;
        _runCancellation = new CancellationTokenSource();
        _sweepTotal = sweep.FirstAxis.Values.Count * sweep.SecondAxis.Values.Count;
        _sweepCompletedRows = 0;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _runCancellation.Token);
        try
        {
            var result = await _backtest.SweepAsync(sweep, linked.Token);
            ApplySweep(result);
        }
        catch (OperationCanceledException) when (_runCancellation.IsCancellationRequested)
        {
            // The harness answers a boundary stop with the cancelled result;
            // this catch only stands for an external stop (a closing window).
            Phase = BacktestRunPhase.Cancelled;
        }
        catch (Exception ex)
        {
            // A refused or failed start says so explicitly (never defaults).
            _logger.LogError(ex, "Backtest sweep failed for '{Strategy}'", sweep.Template.StrategyId);
            ErrorMessage = _localization.GetString(StartFailedKey, ex.Message);
            Phase = BacktestRunPhase.Failed;
        }
        finally
        {
            _runCancellation.Dispose();
            _runCancellation = null;
            _sweepTotal = 0;
            _sweepCompletedRows = 0;
        }

        return RxVoid.Default;
    }

    /// <summary>
    /// The walk-forward start (T-254): compose the study from the run form
    /// (the template), the sweep form's own two axes and the folds field
    /// (every spelling parsed and range-checked explicitly — the standing
    /// refuse-not-default rule), then run it through the harness's
    /// walk-forward lane. The same phase machine, the same cancel command
    /// and the same single-flight gate ride along; the stop lands at a fold
    /// boundary, so a cancelled study keeps every fold whose whole
    /// optimize-then-verify pass had completed.
    /// </summary>
    private async Task<RxVoid> HandleStartWalkForwardAsync(CancellationToken ct)
    {
        if (!TryComposeWalkForward(out var study, out var inputError))
        {
            // The unparsable or malformed input is refused up front — nothing defaults.
            ErrorMessage = inputError;
            Phase = BacktestRunPhase.Failed;
            return RxVoid.Default;
        }

        ErrorMessage = null;
        ProgressText = string.Empty;
        ProgressFraction = 0d;
        LastReport = null;
        ReportText = string.Empty;
        SummaryText = string.Empty;
        EquityPoints = [];
        SelectedSweepRow = null;
        SweepRows = [];
        _sweepBestReport = null;
        _sweepDoneRows = null;
        _sweepDoneTotal = null;
        WalkForwardRows = [];
        _walkForwardDoneRows = null;
        _walkForwardDoneTotal = null;
        ClearMonteCarlo();
        Phase = BacktestRunPhase.Running;
        _runCancellation = new CancellationTokenSource();
        _walkForwardTotal = study.Folds;
        _walkForwardCompletedFolds = 0;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _runCancellation.Token);
        try
        {
            var result = await _backtest.WalkForwardAsync(study, linked.Token);
            ApplyWalkForward(result);
        }
        catch (OperationCanceledException) when (_runCancellation.IsCancellationRequested)
        {
            // The harness answers a boundary stop with the cancelled result;
            // this catch only stands for an external stop (a closing window).
            Phase = BacktestRunPhase.Cancelled;
        }
        catch (Exception ex)
        {
            // A refused or failed start says so explicitly (never defaults).
            _logger.LogError(ex, "Backtest walk-forward failed for '{Strategy}'", study.Template.StrategyId);
            ErrorMessage = _localization.GetString(StartFailedKey, ex.Message);
            Phase = BacktestRunPhase.Failed;
        }
        finally
        {
            _runCancellation.Dispose();
            _runCancellation = null;
            _walkForwardTotal = 0;
            _walkForwardCompletedFolds = 0;
        }

        return RxVoid.Default;
    }

    /// <summary>
    /// The Monte Carlo start (T-254): sample the report surface's completed
    /// report (the run's, the sweep's auto-selected rank-1 or the
    /// click-through's — every completed surface flows through the same
    /// report slot) over the paths and the sampling seed the form composes.
    /// The compute is pure and rides the worker lane; the stop is honored
    /// between paths and a cancelled sampling surfaces no tiles.
    /// </summary>
    private async Task<RxVoid> HandleStartMonteCarloAsync(CancellationToken ct)
    {
        if (LastReport is not { } report)
        {
            // The sampling has no sequence without a completed report: the
            // start refuses up front, never fabricates a distribution.
            ErrorMessage = _localization.GetString(MonteCarloErrorNoReportKey);
            Phase = BacktestRunPhase.Failed;
            return RxVoid.Default;
        }

        if (!int.TryParse(_monteCarloPathsText, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var paths))
        {
            ErrorMessage = _localization.GetString(MonteCarloErrorPathsKey, _monteCarloPathsText);
            Phase = BacktestRunPhase.Failed;
            return RxVoid.Default;
        }

        if (!int.TryParse(_monteCarloSeedText, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var seed))
        {
            ErrorMessage = _localization.GetString(MonteCarloErrorSeedKey, _monteCarloSeedText);
            Phase = BacktestRunPhase.Failed;
            return RxVoid.Default;
        }

        var request = new BacktestMonteCarloRequest
        {
            Report = report,
            Paths = paths,
            Seed = seed,
        };

        try
        {
            // The shape's own bounds refuse here, before the worker lane —
            // the exception's own message names the violated bound.
            request.Validate();
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Backtest Monte Carlo input refused");
            ErrorMessage = _localization.GetString(StartFailedKey, ex.Message);
            Phase = BacktestRunPhase.Failed;
            return RxVoid.Default;
        }

        ErrorMessage = null;
        ProgressText = string.Empty;
        ProgressFraction = 0d;
        SelectedSweepRow = null;
        SweepRows = [];
        _sweepBestReport = null;
        _sweepDoneRows = null;
        _sweepDoneTotal = null;
        WalkForwardRows = [];
        _walkForwardDoneRows = null;
        _walkForwardDoneTotal = null;
        ClearMonteCarlo();
        Phase = BacktestRunPhase.Running;
        _runCancellation = new CancellationTokenSource();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _runCancellation.Token);
        try
        {
            var result = await _backtest.MonteCarloAsync(request, linked.Token);
            ApplyMonteCarlo(result);
        }
        catch (OperationCanceledException) when (_runCancellation.IsCancellationRequested)
        {
            // The stop is the outcome: the cancelled sampling surfaces no tiles.
            ClearMonteCarlo();
            Phase = BacktestRunPhase.Cancelled;
        }
        catch (Exception ex)
        {
            // A refused or failed start says so explicitly (never defaults).
            _logger.LogError(ex, "Backtest Monte Carlo failed for '{Strategy}'", request.Report.Request.StrategyId);
            ErrorMessage = _localization.GetString(StartFailedKey, ex.Message);
            Phase = BacktestRunPhase.Failed;
        }
        finally
        {
            _runCancellation.Dispose();
            _runCancellation = null;
        }

        return RxVoid.Default;
    }

    /// <summary>The cancel command: a synchronous cooperative stop, the run's own source answers.</summary>
    private RxVoid HandleCancel()
    {
        _runCancellation?.Cancel();
        return RxVoid.Default;
    }

    private void ApplyResult(BacktestRunResult result)
    {
        if (result.IsCompleted && result.Report is { } report)
        {
            RenderReportSurface(report);
            Phase = BacktestRunPhase.Completed;
        }
        else
        {
            // The stop is the outcome: no report, nothing half-run shown.
            LastReport = null;
            Phase = BacktestRunPhase.Cancelled;
        }
    }

    /// <summary>
    /// Swap the report surface over to one complete report: the canonical
    /// text, the localized headline line and the equity curve (the
    /// single-run completion and the sweep table's click-through share it).
    /// </summary>
    private void RenderReportSurface(BacktestReport report)
    {
        LastReport = report;
        ReportText = report.ToCanonicalText();
        SummaryText = _localization.GetString(
            SummaryFormatKey,
            report.Statistics.Fills,
            report.Statistics.Trades,
            Render(report.Statistics.WinRate),
            Render(report.Statistics.ProfitFactor),
            Render(report.Statistics.MaxDrawdown),
            Render(report.Statistics.FinalEquity),
            Render(report.Request.InitialQuoteAmount));
        EquityPoints = [.. report.Equity.Select(point => new EquityCurvePoint(point.Time, point.Value))];
    }

    /// <summary>
    /// Apply one sweep result: build the ranked table's rows, carry the
    /// completed count into the done line, and carve the phase — a completed
    /// sweep auto-selects rank 1 (its report becomes the report surface), a
    /// cancelled sweep keeps the completed rows in the table and leaves the
    /// report surface empty until a click-through fills it.
    /// </summary>
    private void ApplySweep(BacktestSweepResult result)
    {
        var firstParameter = result.Request.FirstAxis.Parameter;
        var secondParameter = result.Request.SecondAxis.Parameter;
        var total = result.Request.FirstAxis.Values.Count * result.Request.SecondAxis.Values.Count;

        // The completed sweep's own best report (T-263): rank 1's replay on
        // the sweep's own window. The auto-selection below renders it with
        // no second run; a cancelled sweep carries none (the click-through
        // re-replays instead).
        _sweepBestReport = result.BestReport;

        var rows = result.Rows
            .Select(row => new BacktestSweepRowViewModel(row, firstParameter, secondParameter, _localization))
            .ToArray();
        SweepRows = rows;

        _sweepDoneRows = result.Rows.Count;
        _sweepDoneTotal = total;
        RenderSweepDoneLine();
        ProgressFraction = total > 0 ? Math.Clamp((double)result.Rows.Count / total, 0d, 1d) : 0d;

        if (result.IsCompleted)
        {
            Phase = BacktestRunPhase.Completed;
            if (rows.Length > 0)
            {
                // The auto-selection renders the sweep's OWN best report
                // directly (T-263): synchronous, no second run — the setter's
                // open machinery serves user clicks only.
                _selectedSweepRow = rows[0];
                this.RaisePropertyChanged(nameof(SelectedSweepRow));
                if (_sweepBestReport is { } best)
                {
                    _reportGeneration++;
                    RenderReportSurface(best);
                }
            }
        }
        else
        {
            // The stop is the outcome: the completed rows stay in the table,
            // the report surface stays empty (nothing half-run is ever shown;
            // a click-through only ever opens a COMPLETE row's report).
            LastReport = null;
            Phase = BacktestRunPhase.Cancelled;
        }
    }

    /// <summary>
    /// Apply one walk-forward result (T-254): build the chronological fold
    /// table's rows, carry the completed count into the done line, and carve
    /// the phase — a completed study lands on Completed; a cancelled study
    /// keeps every completed fold's row in the table (each an honest
    /// optimize-then-verify fact) and answers Cancelled.
    /// </summary>
    private void ApplyWalkForward(BacktestWalkForwardResult result)
    {
        var firstParameter = result.Request.FirstAxis.Parameter;
        var secondParameter = result.Request.SecondAxis.Parameter;
        WalkForwardRows = result.Rows
            .Select(row => new BacktestWalkForwardRowViewModel(row, firstParameter, secondParameter, _localization))
            .ToArray();

        _walkForwardDoneRows = result.Rows.Count;
        _walkForwardDoneTotal = result.Request.Folds;
        RenderWalkForwardDoneLine();
        ProgressFraction = result.Request.Folds > 0
            ? Math.Clamp((double)result.Rows.Count / result.Request.Folds, 0d, 1d)
            : 0d;

        if (result.IsCompleted)
        {
            Phase = BacktestRunPhase.Completed;
        }
        else
        {
            // The stop is the outcome: the completed folds stay in the table,
            // nothing half-run is ever shown.
            Phase = BacktestRunPhase.Cancelled;
        }
    }

    /// <summary>
    /// Apply one Monte Carlo result (T-254): a completed sampling renders
    /// the tiles; a cancelled sampling answers nothing (the stop is the
    /// outcome, no distribution is ever half-shown).
    /// </summary>
    private void ApplyMonteCarlo(BacktestMonteCarloResult result)
    {
        if (result.IsCompleted && result.Distribution is { } distribution)
        {
            RenderMonteCarlo(distribution);
            Phase = BacktestRunPhase.Completed;
        }
        else
        {
            ClearMonteCarlo();
            Phase = BacktestRunPhase.Cancelled;
        }
    }

    /// <summary>Render one folded distribution: the headline, the below-start
    /// line and both percentile bundles (the current culture).</summary>
    private void RenderMonteCarlo(BacktestMonteCarloDistribution distribution)
    {
        _monteCarloDistribution = distribution;
        this.RaisePropertyChanged(nameof(HasMonteCarlo));
        MonteCarloHeadline = _localization.GetString(MonteCarloHeadlineFormatKey, distribution.Paths, distribution.Trades, distribution.Seed);
        MonteCarloBelowStart = _localization.GetString(MonteCarloBelowStartFormatKey, distribution.PathsBelowStart, distribution.Paths);
        MonteCarloFinalEquityP5 = Render(distribution.FinalEquity.P5);
        MonteCarloFinalEquityP25 = Render(distribution.FinalEquity.P25);
        MonteCarloFinalEquityMedian = Render(distribution.FinalEquity.Median);
        MonteCarloFinalEquityP75 = Render(distribution.FinalEquity.P75);
        MonteCarloFinalEquityP95 = Render(distribution.FinalEquity.P95);
        MonteCarloDrawdownP5 = Render(distribution.MaxDrawdown.P5);
        MonteCarloDrawdownP25 = Render(distribution.MaxDrawdown.P25);
        MonteCarloDrawdownMedian = Render(distribution.MaxDrawdown.Median);
        MonteCarloDrawdownP75 = Render(distribution.MaxDrawdown.P75);
        MonteCarloDrawdownP95 = Render(distribution.MaxDrawdown.P95);
    }

    /// <summary>Clear the Monte Carlo result surfaces (the tiles' honest empty state).</summary>
    private void ClearMonteCarlo()
    {
        _monteCarloDistribution = null;
        this.RaisePropertyChanged(nameof(HasMonteCarlo));
        MonteCarloHeadline = string.Empty;
        MonteCarloBelowStart = string.Empty;
        MonteCarloFinalEquityP5 = string.Empty;
        MonteCarloFinalEquityP25 = string.Empty;
        MonteCarloFinalEquityMedian = string.Empty;
        MonteCarloFinalEquityP75 = string.Empty;
        MonteCarloFinalEquityP95 = string.Empty;
        MonteCarloDrawdownP5 = string.Empty;
        MonteCarloDrawdownP25 = string.Empty;
        MonteCarloDrawdownMedian = string.Empty;
        MonteCarloDrawdownP75 = string.Empty;
        MonteCarloDrawdownP95 = string.Empty;
    }

    /// <summary>
    /// Open one sweep row's report surface (the T-263 click-through): rank 1
    /// of a completed sweep renders the sweep's own best report — the engine
    /// replayed that exact parameterization on the sweep's own loaded
    /// window, so a second run could only drift off the cached series'
    /// current state and never add honesty. Any other row re-replays through
    /// the single-run lane: the replay's own request echo is the report's
    /// provenance, and the table's row metrics stay the sweep's own. A
    /// replay in flight owns the harness and the surface: a newer open
    /// request bumps the generation (the stale continuation never renders
    /// over it); a click while one runs waits for its completion.
    /// </summary>
    private async Task OpenSweepRowReportAsync(BacktestSweepRowViewModel row)
    {
        if (row.Rank == 1 && _sweepBestReport is { } best)
        {
            _reportGeneration++;
            RenderReportSurface(best);
            return;
        }

        // The in-flight run owns the harness (the single-flight gate refuses
        // a second start anyway): the click waits for its completion.
        if (IsRunning)
        {
            return;
        }

        var generation = ++_reportGeneration;
        ErrorMessage = null;
        ProgressText = string.Empty;
        ProgressFraction = 0d;
        Phase = BacktestRunPhase.Running;
        _runCancellation = new CancellationTokenSource();
        try
        {
            var result = await _backtest.RunAsync(row.Row.Request, _runCancellation.Token);
            if (generation == _reportGeneration)
            {
                ApplyResult(result);
            }
            else
            {
                // Superseded: the newer open request owns the report surface;
                // the phase settles back to the sweep's completed state.
                Phase = BacktestRunPhase.Completed;
            }
        }
        catch (OperationCanceledException) when (_runCancellation.IsCancellationRequested)
        {
            if (generation == _reportGeneration)
            {
                // The user's stop: the cancelled replay surfaces no report.
                LastReport = null;
                Phase = BacktestRunPhase.Cancelled;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Backtest click-through replay failed for '{Strategy}'",
                row.Row.Request.StrategyId);
            if (generation == _reportGeneration)
            {
                ErrorMessage = _localization.GetString(StartFailedKey, ex.Message);
                Phase = BacktestRunPhase.Failed;
            }
        }
        finally
        {
            _runCancellation.Dispose();
            _runCancellation = null;
        }
    }

    /// <summary>Apply one run-event: the progress line only (the terminal
    /// states surface through the awaited result, never through the stream).
    /// While a sweep is in flight the per-row events wrap into the global
    /// counter — "Row k of n: bars" and the fraction ((rows done) + row
    /// fraction) / total — since each parameterization replays the full
    /// window's bar line. While a walk-forward is in flight (T-254) the
    /// out-of-sample replays' events wrap into the fold counter — "Fold k of
    /// n: bars" and the same honest fraction over the folds; the in-sample
    /// optimization replays are silent, so nothing re-counts them.</summary>
    private void OnRunEvent(BacktestRunEvent @event)
    {
        switch (@event.Kind)
        {
            case BacktestRunEventKind.Started:
                if (_sweepTotal > 0)
                {
                    RenderSweepRowLine(_sweepCompletedRows + 1, _sweepCompletedRows, 0, @event.BarsRequested);
                    break;
                }

                if (_walkForwardTotal > 0)
                {
                    RenderWalkForwardFoldLine(_walkForwardCompletedFolds + 1, _walkForwardCompletedFolds, 0, @event.BarsRequested);
                    break;
                }

                ProgressText = _localization.GetString(ProgressFormatKey, 0, @event.BarsRequested);
                ProgressFraction = 0d;
                break;

            case BacktestRunEventKind.Progress:
                if (_sweepTotal > 0)
                {
                    RenderSweepRowLine(
                        _sweepCompletedRows + 1,
                        _sweepCompletedRows,
                        @event.BarsProcessed,
                        @event.BarsRequested);
                    break;
                }

                if (_walkForwardTotal > 0)
                {
                    RenderWalkForwardFoldLine(
                        _walkForwardCompletedFolds + 1,
                        _walkForwardCompletedFolds,
                        @event.BarsProcessed,
                        @event.BarsRequested);
                    break;
                }

                ProgressText = _localization.GetString(ProgressFormatKey, @event.BarsProcessed, @event.BarsRequested);
                ProgressFraction = @event.BarsRequested > 0
                    ? Math.Clamp((double)@event.BarsProcessed / @event.BarsRequested, 0d, 1d)
                    : 0d;
                break;

            case BacktestRunEventKind.Completed:
                if (_sweepTotal > 0)
                {
                    // The row is fully done: the event's bar count is the
                    // full target, so the fraction lands exactly on
                    // (rows done) / total, then the counter moves past it.
                    RenderSweepRowLine(
                        _sweepCompletedRows + 1,
                        _sweepCompletedRows,
                        @event.BarsProcessed,
                        @event.BarsRequested);
                    _sweepCompletedRows++;
                    break;
                }

                if (_walkForwardTotal > 0)
                {
                    // The fold is fully done: the same exact-landing shape a
                    // sweep row takes, then the counter moves past it.
                    RenderWalkForwardFoldLine(
                        _walkForwardCompletedFolds + 1,
                        _walkForwardCompletedFolds,
                        @event.BarsProcessed,
                        @event.BarsRequested);
                    _walkForwardCompletedFolds++;
                    break;
                }

                ProgressText = _localization.GetString(ProgressFormatKey, @event.BarsProcessed, @event.BarsRequested);
                ProgressFraction = @event.BarsRequested > 0
                    ? Math.Clamp((double)@event.BarsProcessed / @event.BarsRequested, 0d, 1d)
                    : 0d;
                break;

            case BacktestRunEventKind.Cancelled:
                // The stop is the outcome: the phase carves the surface, the
                // progress line stays honest about how far the replay got.
                // (A sweep never publishes this: its rows ride no token and
                // its stop is the result's outcome, never an event.)
                ProgressText = _localization.GetString(ProgressFormatKey, @event.BarsProcessed, @event.BarsRequested);
                break;

            default:
                // An unknown event kind never defaults into a layout line.
                break;
        }
    }

    /// <summary>Render one in-flight fold's line (T-254): "Fold k of n: bars"
    /// and the global fraction ((folds done so far) + this fold's out-of-sample
    /// bar fraction) / folds, clamped — the honest fold of the per-fold events
    /// (the in-sample optimization replays are silent and re-count nothing).</summary>
    /// <param name="fold">The 1-based fold the line names.</param>
    /// <param name="foldsDone">How many folds count as done in the fraction.</param>
    /// <param name="barsProcessed">The fold's out-of-sample bar count so far.</param>
    /// <param name="barsRequested">The fold's out-of-sample bar target.</param>
    private void RenderWalkForwardFoldLine(int fold, int foldsDone, int barsProcessed, int barsRequested)
    {
        var barsLine = _localization.GetString(ProgressFormatKey, barsProcessed, barsRequested);
        ProgressText = _localization.GetString(WalkForwardProgressFormatKey, fold, _walkForwardTotal, barsLine);
        var barFraction = barsRequested > 0
            ? Math.Clamp((double)barsProcessed / barsRequested, 0d, 1d)
            : 0d;
        ProgressFraction = _walkForwardTotal > 0
            ? Math.Clamp((foldsDone + barFraction) / _walkForwardTotal, 0d, 1d)
            : 0d;
    }

    /// <summary>Render one in-flight sweep row's line: "Row k of n: bars" and
    /// the global fraction ((rows done so far) + this row's bar fraction) /
    /// total, clamped — the honest fold of the per-row events.</summary>
    /// <param name="row">The 1-based row the line names.</param>
    /// <param name="rowsDone">How many rows count as done in the fraction.</param>
    /// <param name="barsProcessed">The row's own bar count so far.</param>
    /// <param name="barsRequested">The row's own bar target.</param>
    private void RenderSweepRowLine(int row, int rowsDone, int barsProcessed, int barsRequested)
    {
        var barsLine = _localization.GetString(ProgressFormatKey, barsProcessed, barsRequested);
        ProgressText = _localization.GetString(SweepProgressFormatKey, row, _sweepTotal, barsLine);
        var barFraction = barsRequested > 0
            ? Math.Clamp((double)barsProcessed / barsRequested, 0d, 1d)
            : 0d;
        ProgressFraction = _sweepTotal > 0
            ? Math.Clamp((rowsDone + barFraction) / _sweepTotal, 0d, 1d)
            : 0d;
    }

    #endregion Command Handlers

    #region Request Composition

    /// <summary>
    /// Compose the run request from the form fields: every spelling parsed
    /// explicitly — an unparsable field is a refused start with its own
    /// localized line, never a silently substituted default.
    /// </summary>
    private bool TryComposeRequest(out BacktestRunRequest request, out string? inputError)
    {
        request = null!;
        inputError = null;

        // The picker's absence: an empty catalog composes no request — the
        // start refuses up front, the service is never called silently.
        if (_strategyId.Length == 0)
        {
            inputError = _localization.GetString(NoStrategyKey);
            return false;
        }

        if (!int.TryParse(_barsText, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var bars))
        {
            inputError = _localization.GetString(InvalidBarsKey, _barsText);
            return false;
        }

        if (!int.TryParse(_seedText, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var seed))
        {
            inputError = _localization.GetString(InvalidSeedKey, _seedText);
            return false;
        }

        if (!decimal.TryParse(_slippageText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var slippage))
        {
            inputError = _localization.GetString(InvalidSlippageKey, _slippageText);
            return false;
        }

        if (!decimal.TryParse(_fractionText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var fraction))
        {
            inputError = _localization.GetString(InvalidFractionKey, _fractionText);
            return false;
        }

        Symbol symbol;
        try
        {
            symbol = Symbol.Parse(_symbolText);
        }
        catch (ArgumentException)
        {
            inputError = _localization.GetString(InvalidSymbolKey, _symbolText);
            return false;
        }

        request = new BacktestRunRequest
        {
            StrategyId = _strategyId,
            AdapterId = _exchange.Current.Id,
            Symbol = symbol,
            Interval = _selectedTimeFrame,
            Bars = bars,
            Seed = seed,
            SlippageValue = slippage,
            LongNotionalFraction = fraction,
        };
        return true;
    }

    #endregion Request Composition

    #region Sweep Composition

    /// <summary>
    /// Compose the study inputs both compound surfaces share (the sweep and
    /// the walk-forward): the run form's template and the sweep form's two
    /// axes — every spelling parsed and range-checked explicitly, an
    /// unparsable list, a too-long axis, a duplicate-knob grid or an
    /// out-of-range value is a refused start with its own localized line,
    /// never a silently corrected or clamped input.
    /// </summary>
    private bool TryComposeStudyInputs(
        out BacktestRunRequest template,
        out BacktestSweepAxis firstAxis,
        out BacktestSweepAxis secondAxis,
        out string? inputError)
    {
        template = null!;
        firstAxis = null!;
        secondAxis = null!;
        inputError = null;

        // The template is the run form's own composition: strategy, series
        // identity, bars, seed and base policy parse exactly as a single
        // run would — an unparsable field refuses the study with the same
        // line the run start would give.
        if (!TryComposeRequest(out template, out var requestError))
        {
            inputError = requestError;
            return false;
        }

        if (!TryParseAxisValues(_sweepFirstValuesText, out var firstValues, out var firstValuesError))
        {
            inputError = firstValuesError;
            return false;
        }

        if (!TryParseAxisValues(_sweepSecondValuesText, out var secondValues, out var secondValuesError))
        {
            inputError = secondValuesError;
            return false;
        }

        // The pickers map onto the enum's declared order; an undefined
        // picker state (a cleared selection) is a refused start, never a
        // defaulted knob.
        if (KnobOf(_sweepFirstIndex) is not { } firstParameter
            || KnobOf(_sweepSecondIndex) is not { } secondParameter)
        {
            inputError = _localization.GetString(SweepErrorParameterKey);
            return false;
        }

        if (firstParameter == secondParameter)
        {
            inputError = _localization.GetString(SweepErrorSameParameterKey);
            return false;
        }

        if (firstValues.Count == 0 || firstValues.Count > BacktestSweepAxis.MaxValuesPerAxis)
        {
            inputError = _localization.GetString(SweepErrorCountKey, firstValues.Count, BacktestSweepAxis.MaxValuesPerAxis);
            return false;
        }

        if (secondValues.Count == 0 || secondValues.Count > BacktestSweepAxis.MaxValuesPerAxis)
        {
            inputError = _localization.GetString(SweepErrorCountKey, secondValues.Count, BacktestSweepAxis.MaxValuesPerAxis);
            return false;
        }

        foreach (var value in firstValues)
        {
            if (!InKnobRange(firstParameter, value))
            {
                inputError = _localization.GetString(SweepErrorRangeKey, Render(value), KnobLabel(firstParameter));
                return false;
            }
        }

        foreach (var value in secondValues)
        {
            if (!InKnobRange(secondParameter, value))
            {
                inputError = _localization.GetString(SweepErrorRangeKey, Render(value), KnobLabel(secondParameter));
                return false;
            }
        }

        firstAxis = new BacktestSweepAxis(firstParameter, firstValues);
        secondAxis = new BacktestSweepAxis(secondParameter, secondValues);
        return true;
    }

    /// <summary>
    /// Compose the sweep request from the study inputs (the template and
    /// both axes, exactly as a walk-forward composes them — T-254): the grid
    /// composes and range-checks at start, nothing defaults.
    /// </summary>
    private bool TryComposeSweep(out BacktestSweepRequest sweep, out string? inputError)
    {
        sweep = null!;
        inputError = null;

        if (!TryComposeStudyInputs(out var template, out var firstAxis, out var secondAxis, out var inputsError))
        {
            inputError = inputsError;
            return false;
        }

        sweep = new BacktestSweepRequest
        {
            Template = template,
            FirstAxis = firstAxis,
            SecondAxis = secondAxis,
        };
        return true;
    }

    /// <summary>
    /// Compose the walk-forward request (T-254): the sweep form's own study
    /// inputs (the template and both axes) plus the folds spelling — parsed
    /// and range-checked explicitly, an out-of-band count refuses with its
    /// own line, never a clamped or defaulted carve.
    /// </summary>
    private bool TryComposeWalkForward(out BacktestWalkForwardRequest study, out string? inputError)
    {
        study = null!;
        inputError = null;

        if (!TryComposeStudyInputs(out var template, out var firstAxis, out var secondAxis, out var inputsError))
        {
            inputError = inputsError;
            return false;
        }

        if (!int.TryParse(_foldsText, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var folds))
        {
            inputError = _localization.GetString(WalkForwardErrorFoldsKey, _foldsText);
            return false;
        }

        if (folds < BacktestWalkForwardRequest.MinFolds || folds > BacktestWalkForwardRequest.MaxFolds)
        {
            inputError = _localization.GetString(
                WalkForwardErrorFoldRangeKey,
                folds,
                BacktestWalkForwardRequest.MinFolds,
                BacktestWalkForwardRequest.MaxFolds);
            return false;
        }

        study = new BacktestWalkForwardRequest
        {
            Template = template,
            FirstAxis = firstAxis,
            SecondAxis = secondAxis,
            Folds = folds,
        };
        return true;
    }

    /// <summary>Parse one axis' values spelling: a comma-separated list of
    /// invariant numbers, kept in declaration order (duplicates stay — the
    /// enumeration keeps the caller's grid, ties break by order).</summary>
    private bool TryParseAxisValues(string text, out List<decimal> values, out string? inputError)
    {
        values = [];
        inputError = null;

        foreach (var token in text.Split(',', StringSplitOptions.TrimEntries))
        {
            if (!decimal.TryParse(
                    token,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var value))
            {
                inputError = _localization.GetString(SweepErrorValuesKey, text);
                return false;
            }

            values.Add(value);
        }

        return true;
    }

    /// <summary>Map a picker index onto its knob; anything outside the
    /// declared list is no knob (the compose refuses it).</summary>
    private static BacktestSweepParameter? KnobOf(int index) => index switch
    {
        0 => BacktestSweepParameter.LongNotionalFraction,
        1 => BacktestSweepParameter.SlippageValue,
        _ => null,
    };

    /// <summary>One knob's request range (the exact bounds the run request
    /// and the sweep axis enforce: a fraction in (0, 1], a non-negative
    /// slippage magnitude).</summary>
    private static bool InKnobRange(BacktestSweepParameter parameter, decimal value) => parameter switch
    {
        BacktestSweepParameter.LongNotionalFraction => value > 0m && value <= 1m,
        BacktestSweepParameter.SlippageValue => value >= 0m,
        _ => throw new InvalidOperationException(
            $"The sweep parameter '{parameter}' is not a defined knob; the value is refused, never defaulted."),
    };

    /// <summary>One knob's live label (the picker's own lines).</summary>
    private string KnobLabel(BacktestSweepParameter parameter) => _localization.GetString(parameter switch
    {
        BacktestSweepParameter.LongNotionalFraction => SweepFractionParamKey,
        BacktestSweepParameter.SlippageValue => SweepSlippageParamKey,
        _ => throw new InvalidOperationException(
            $"The sweep parameter '{parameter}' is not a defined knob; the label is refused, never defaulted."),
    });

    #endregion Sweep Composition

    #region Localized Render

    private void RenderLocalizedState()
    {
        RenderPhaseText();
        RenderSweepState();
        foreach (var row in _walkForwardRows)
        {
            row.Refresh(_localization);
        }

        RenderWalkForwardDoneLine();
        if (_monteCarloDistribution is { } distribution)
        {
            RenderMonteCarlo(distribution);
        }
    }

    private void RenderPhaseText()
    {
        var key = Phase switch
        {
            BacktestRunPhase.Idle => StatusIdleKey,
            BacktestRunPhase.Running => StatusRunningKey,
            BacktestRunPhase.Completed => StatusCompletedKey,
            BacktestRunPhase.Cancelled => StatusCancelledKey,
            BacktestRunPhase.Failed => StatusFailedKey,
            _ => StatusIdleKey,
        };
        StatusText = _localization.GetString(key);
    }

    /// <summary>
    /// Re-render the sweep's localized surfaces: the picker's knob labels
    /// (the selection indexes re-announced so the combos survive the list
    /// swap), every table row's texts, and the done line while a sweep
    /// result is showing — the never-a-static-localized-string lifecycle.
    /// </summary>
    private void RenderSweepState()
    {
        SweepParameters =
        [
            _localization.GetString(SweepFractionParamKey),
            _localization.GetString(SweepSlippageParamKey),
        ];
        this.RaisePropertyChanged(nameof(SweepFirstIndex));
        this.RaisePropertyChanged(nameof(SweepSecondIndex));

        foreach (var row in _sweepRows)
        {
            row.Refresh(_localization);
        }

        RenderSweepDoneLine();
    }

    /// <summary>Render the sweep's done line ("Sweep finished: k of n
    /// parameterizations replayed") while a sweep result is showing; the
    /// counts ride the last applied result and clear on the next start.</summary>
    private void RenderSweepDoneLine()
    {
        if (_sweepDoneRows is { } done && _sweepDoneTotal is { } doneTotal)
        {
            ProgressText = _localization.GetString(SweepDoneFormatKey, done, doneTotal);
        }
    }

    /// <summary>Render the walk-forward's done line ("Walk-forward finished:
    /// k of n folds replayed") while a study result is showing; the counts
    /// ride the last applied result and clear on the next start.</summary>
    private void RenderWalkForwardDoneLine()
    {
        if (_walkForwardDoneRows is { } done && _walkForwardDoneTotal is { } doneTotal)
        {
            ProgressText = _localization.GetString(WalkForwardDoneFormatKey, done, doneTotal);
        }
    }

    /// <summary>Render one raw decimal for the localized summary (current culture).</summary>
    private string Render(decimal value) => value.ToString(null, System.Globalization.CultureInfo.CurrentCulture);

    /// <summary>Render one optional decimal for the localized summary; the honest absence is a dash, never zero.</summary>
    private string Render(decimal? value) => value is { } present ? Render(present) : "-";

    #endregion Localized Render
}
