namespace ZettaTrade.App.ViewModels;

using System.Reactive.Disposables;
using System.Reactive.Linq;
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
        set => this.RaiseAndSetIfChanged(ref _strategyId, value);
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
    /// The table's selected row: the click-through — setting it swaps the
    /// report surface over to the row's own complete single-run report
    /// (the equity curve, the summary line and the canonical text).
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
                RenderReportSurface(selected.Row.Report);
            }
        }
    }

    /// <summary>Start one deterministic parameter sweep over the composed grid.</summary>
    public ReactiveCommand<RxVoid, RxVoid> StartSweepCommand { get; }

    #endregion Sweep Form

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
        _sweepDoneRows = null;
        _sweepDoneTotal = null;
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
                SelectedSweepRow = rows[0];
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

    /// <summary>Apply one run-event: the progress line only (the terminal
    /// states surface through the awaited result, never through the stream).
    /// While a sweep is in flight the per-row events wrap into the global
    /// counter — "Row k of n: bars" and the fraction ((rows done) + row
    /// fraction) / total — since each parameterization replays the full
    /// window's bar line.</summary>
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
    /// Compose the sweep request from the run form (the template) and the
    /// sweep form (both axes): every spelling parsed and range-checked
    /// explicitly — an unparsable list, a too-long axis, a duplicate-knob
    /// grid or an out-of-range value is a refused start with its own
    /// localized line, never a silently corrected or clamped input.
    /// </summary>
    private bool TryComposeSweep(out BacktestSweepRequest sweep, out string? inputError)
    {
        sweep = null!;
        inputError = null;

        // The template is the run form's own composition: strategy, series
        // identity, bars, seed and base policy parse exactly as a single
        // run would — an unparsable field refuses the sweep with the same
        // line the run start would give.
        if (!TryComposeRequest(out var template, out var requestError))
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

        sweep = new BacktestSweepRequest
        {
            Template = template,
            FirstAxis = new BacktestSweepAxis(firstParameter, firstValues),
            SecondAxis = new BacktestSweepAxis(secondParameter, secondValues),
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

    /// <summary>Render one raw decimal for the localized summary (current culture).</summary>
    private string Render(decimal value) => value.ToString(null, System.Globalization.CultureInfo.CurrentCulture);

    /// <summary>Render one optional decimal for the localized summary; the honest absence is a dash, never zero.</summary>
    private string Render(decimal? value) => value is { } present ? Render(present) : "-";

    #endregion Localized Render
}
