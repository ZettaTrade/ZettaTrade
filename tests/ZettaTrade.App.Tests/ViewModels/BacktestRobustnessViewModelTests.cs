namespace ZettaTrade.App.Tests.ViewModels;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Livery.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ReactiveUI;
using Xunit;
using ZettaTrade.App.Services;
using ZettaTrade.App.ViewModels;
using ZettaTrade.Backtest;
using ZettaTrade.Charting;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.Infrastructure.Fees;
using ZettaTrade.Strategies;

/// <summary>The half-day-agnostic status byte ReactiveUI.Primitives names RxVoid.</summary>
using RxVoid = ReactiveUI.Primitives.RxVoid;

/// <summary>
/// The robustness surfaces' view-model facts (T-254): the walk-forward
/// composes its study from the run form (the template), the sweep form's
/// own two axes and the folds spelling (every malformed input refuses with
/// its own localized line and never reaches the service), renders the
/// chronological fold table with the chosen pairs and the honest
/// out-of-sample numbers, and wraps the out-of-sample replays' events into
/// the fold counter; the Monte Carlo samples the report surface's completed
/// report (its canExecute requires one), folds the tiles and keeps the
/// report surface; every start clears the other surfaces; a language switch
/// re-renders the rows and the tiles. Facts run inside the
/// AvaloniaDispatcherSerial collection.
/// </summary>
[Collection("AvaloniaDispatcherSerial")]
public sealed class BacktestRobustnessViewModelTests
{
    static BacktestRobustnessViewModelTests() => AvaloniaDispatcherPump.EnsureInstalled();

    /// <summary>The parked-run watchdog: a fact must never hang forever.</summary>
    private static readonly TimeSpan ParkedWaitBudget = TimeSpan.FromSeconds(10);

    private static readonly DateTimeOffset Base = new(2026, 3, 2, 0, 0, 0, TimeSpan.Zero);

    /// <summary>The localized-text fake: keys read as bracketed ids, formats name their arguments in order.</summary>
    private sealed class FakeLocalization : ILocalizationManager
    {
        public Subject<string> Changes { get; } = new();

        public int RenderCalls { get; private set; }

        public string CurrentLanguage => "en";

        public IReadOnlyList<LanguageInfo> AvailableLanguages { get; } =
        [
            new LanguageInfo("en", "English", "English", true),
            new LanguageInfo("ru", "Russian", "Русский", false),
        ];

        public Task SetLanguageAsync(string cultureCode, CancellationToken ct = default) => Task.CompletedTask;

        public string GetString(string key)
        {
            RenderCalls++;
            return "[" + key + "]";
        }

        public string GetString(string key, params object[] args)
        {
            RenderCalls++;
            return "[" + key + "/" + string.Join("|", args) + "]";
        }

        public IObservable<string> LanguageChanged => Changes;

        public IObservable<string> GetObservable(string key) => Observable.Empty<string>();
    }

    private sealed class TestCatalog(params IStrategy[] strategies) : IStrategyCatalog
    {
        private readonly IStrategy[] _strategies = strategies;

        public IReadOnlyList<IStrategy> All => _strategies;

        public IStrategy? Get(string strategyId) => _strategies.FirstOrDefault(strategy => strategy.Id == strategyId);
    }

    private sealed class FakeStrategy(string id) : IStrategy
    {
        public string Id => id;

        public StrategyStance Evaluate(StrategyContext context) => StrategyStance.Neutral;
    }

    private sealed class FakeAdapter(string id, string name) : IMarketDataAdapter
    {
        public string Id => id;

        public string Name => name;

        public bool IsConnected => true;

        public ExchangeCapabilities Capabilities => ExchangeCapabilities.MarketData;

        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public async Task<IReadOnlyList<Symbol>> GetSymbolsAsync(CancellationToken ct = default) => [];

        public IObservable<Ticker> SubscribeTicker(Symbol symbol) => Observable.Empty<Ticker>();

        public IObservable<OrderBook> SubscribeOrderBook(Symbol symbol, int depth = 20) => Observable.Empty<OrderBook>();

        public IObservable<MarketTrade> SubscribeTrades(Symbol symbol) => Observable.Empty<MarketTrade>();

        public async Task<IReadOnlyList<Bar>> GetBarsAsync(
            Symbol symbol,
            TimeFrame timeFrame,
            DateTimeOffset from,
            DateTimeOffset? to = null,
            int limit = 500,
            CancellationToken ct = default) => [];
    }

    private static ISettingsService CreateSettings()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(string.Empty);
        return settings;
    }

    private static (
        BacktestRunViewModel Model,
        IBacktestService Backtest,
        FakeLocalization Loc,
        Subject<BacktestRunEvent> Events,
        Subject<string> LanguageChanges
    ) Build(params IStrategy[] strategies)
    {
        var backtest = Substitute.For<IBacktestService>();
        var events = new Subject<BacktestRunEvent>();
        var loc = new FakeLocalization();
        backtest.Events.Returns(events);
        var exchange = new ActiveExchangeService(
            [new FakeAdapter("fake", "Fake"), new FakeAdapter("fake2", "Fake2")],
            CreateSettings(),
            NullLogger<ActiveExchangeService>.Instance);
        var model = new BacktestRunViewModel(
            new TestCatalog(strategies),
            backtest,
            exchange,
            loc,
            NullLogger<BacktestRunViewModel>.Instance);
        return (model, backtest, loc, events, loc.Changes);
    }

    /// <summary>
    /// One completed report whose fill sequence closes one honest round
    /// trip (the ledger books it): the Monte Carlo's own Validate reads the
    /// fills, so the fixture's report must carry a real trade sequence —
    /// never a statistics-only shell.
    /// </summary>
    private static BacktestReport Report(decimal finalEquity)
    {
        var fees = Substitute.For<IFeeScheduleProvider>();
        fees.GetSchedule("fake").Returns(new FeeSchedule(0m, 0m, 0m));
        var ledger = new BacktestLedger("fake", Symbol.Parse("BTC/USDT"), fees, 1_000_000m);
        ledger.FillSide(OrderSide.Buy, 1m, 100m, Base).Should().BeTrue("the fixture's book carries the entry");
        ledger.FillSide(OrderSide.Sell, 1m, 110m, Base.AddHours(1)).Should().BeTrue("the fixture's book carries the exit");

        var request = new BacktestRunRequest
        {
            StrategyId = "sma-golden-cross",
            AdapterId = "fake",
            Symbol = Symbol.Parse("BTC/USDT"),
            Interval = TimeFrame.H1,
            Bars = 100,
            Seed = 7,
            LongNotionalFraction = 0.25m,
            SlippageValue = 1m,
            InitialQuoteAmount = 1_000_000m,
        };
        var equity = new[] { new BacktestEquityPoint(Base, 1_000_000m), new BacktestEquityPoint(Base.AddHours(1), finalEquity) };
        return new BacktestReport(request, equity, ledger.Fills, ledger.Refusals, ledger.ComputeStatistics(finalEquity));
    }

    /// <summary>A completed walk-forward result over the given request: two folds with honest carves and numbers.</summary>
    private static BacktestWalkForwardResult WalkForward(BacktestWalkForwardRequest request) => new(
        BacktestWalkForwardOutcome.Completed,
        request,
        [
            new BacktestWalkForwardRow(
                Fold: 1,
                InSampleBars: 64,
                OutOfSampleBars: 64,
                FirstValue: 0.5m,
                SecondValue: 1m,
                InSampleNet: 10m,
                OutOfSampleNet: 4m,
                OutOfSampleMaxDrawdown: 2m),
            new BacktestWalkForwardRow(
                Fold: 2,
                InSampleBars: 64,
                OutOfSampleBars: 64,
                FirstValue: 0.25m,
                SecondValue: 2m,
                InSampleNet: 5m,
                OutOfSampleNet: -1m,
                OutOfSampleMaxDrawdown: 3m),
        ]);

    /// <summary>A completed Monte Carlo result over the given request: the tiles' fixed percentiles.</summary>
    private static BacktestMonteCarloResult MonteCarlo(BacktestMonteCarloRequest request) => new(
        BacktestMonteCarloOutcome.Completed,
        new BacktestMonteCarloDistribution(
            request,
            Trades: 1,
            FinalEquity: new BacktestMonteCarloPercentiles(999_000m, 999_500m, 1_000_000m, 1_000_500m, 1_001_000m),
            MaxDrawdown: new BacktestMonteCarloPercentiles(0m, 100m, 500m, 1_000m, 2_000m),
            PathsBelowStart: 400));

    /// <summary>Render one raw decimal exactly the way the view model's tiles do (culture-proof assertions).</summary>
    private static string Number(decimal value) => value.ToString(null, CultureInfo.CurrentCulture);

    #region Walk-Forward Composition

    [Fact]
    public async Task StartWalkForwardCommand_ComposesTheStudy_RendersTheFoldTable()
    {
        var (model, backtest, _, _, _) = Build(new FakeStrategy("sma-golden-cross"));
        BacktestWalkForwardRequest? composed = null;
        backtest.WalkForwardAsync(Arg.Any<BacktestWalkForwardRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                composed = ci.Arg<BacktestWalkForwardRequest>();
                return WalkForward(composed!);
            });
        model.FoldsText = "3";
        model.SweepFirstValuesText = "0.5, 0.25";
        model.SweepSecondValuesText = "1, 2";

        await model.StartWalkForwardCommand.Execute(RxVoid.Default);

        composed.Should().NotBeNull();
        composed!.Template.StrategyId.Should().Be("sma-golden-cross", "the template rides the run form");
        composed.Template.AdapterId.Should().Be("fake");
        composed.Template.Bars.Should().Be(BacktestRunRequest.DefaultBars);
        composed.Template.Seed.Should().Be(BacktestRunRequest.DefaultSeed);
        composed.FirstAxis.Parameter.Should().Be(BacktestSweepParameter.LongNotionalFraction, "the axes are the sweep form's own");
        composed.FirstAxis.Values.Should().Equal([0.5m, 0.25m]);
        composed.SecondAxis.Parameter.Should().Be(BacktestSweepParameter.SlippageValue);
        composed.SecondAxis.Values.Should().Equal([1m, 2m]);
        composed.Folds.Should().Be(3, "the folds spelling rides the robustness form");

        model.Phase.Should().Be(BacktestRunPhase.Completed);
        model.HasWalkForwardRows.Should().BeTrue();
        model.WalkForwardRows.Should().HaveCount(2);
        model.WalkForwardRows[0].Fold.Should().Be(1);
        model.WalkForwardRows[0].Row.InSampleBars.Should().Be(64);
        model.WalkForwardRows[0].Row.OutOfSampleBars.Should().Be(64);
        model.WalkForwardRows[0].ParameterText.Should().StartWith(
            "[Backtest.Sweep.Param.Fraction]=",
            "the fold's chosen pair renders through the knobs' own live labels");
        model.WalkForwardRows[0].ParameterText.Should().Contain("[Backtest.Sweep.Param.Slippage]=");
        model.WalkForwardRows[0].InSampleNetText.Should().Be(Number(10m));
        model.WalkForwardRows[0].OutOfSampleNetText.Should().Be(Number(4m));
        model.WalkForwardRows[0].OutOfSampleDrawdownText.Should().Be(Number(2m));
        model.WalkForwardRows[1].OutOfSampleNetText.Should().Be(Number(-1m));
        model.ProgressText.Should().Be("[Backtest.WalkForward.Done.Format/2|3]");
        model.ProgressFraction.Should().Be(2d / 3d);
        model.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task StartWalkForwardCommand_BadFoldsSpelling_RefusesWithItsOwnLine()
    {
        var (model, backtest, _, _, _) = Build(new FakeStrategy("sma-golden-cross"));
        model.FoldsText = "x";

        await model.StartWalkForwardCommand.Execute(RxVoid.Default);

        model.Phase.Should().Be(BacktestRunPhase.Failed);
        model.ErrorMessage.Should().Be("[Backtest.WalkForward.Error.Folds/x]");
        await backtest.DidNotReceive().WalkForwardAsync(
            Arg.Any<BacktestWalkForwardRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartWalkForwardCommand_FoldsOutOfRange_RefusesWithItsOwnLine()
    {
        var (model, backtest, _, _, _) = Build(new FakeStrategy("sma-golden-cross"));
        model.FoldsText = "1";

        await model.StartWalkForwardCommand.Execute(RxVoid.Default);

        model.Phase.Should().Be(BacktestRunPhase.Failed);
        model.ErrorMessage.Should().Be("[Backtest.WalkForward.Error.FoldRange/1|2|64]");
        await backtest.DidNotReceive().WalkForwardAsync(
            Arg.Any<BacktestWalkForwardRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartWalkForwardCommand_DuplicateAxes_RefusesWithTheSweepFormsLine()
    {
        var (model, backtest, _, _, _) = Build(new FakeStrategy("sma-golden-cross"));
        model.SweepSecondIndex = 0;

        await model.StartWalkForwardCommand.Execute(RxVoid.Default);

        model.Phase.Should().Be(BacktestRunPhase.Failed);
        model.ErrorMessage.Should().Be("[Backtest.Sweep.Error.SameParameter]");
        await backtest.DidNotReceive().WalkForwardAsync(
            Arg.Any<BacktestWalkForwardRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartWalkForwardCommand_CancelledStudy_KeepsTheCompletedFolds()
    {
        var (model, backtest, _, _, _) = Build(new FakeStrategy("sma-golden-cross"));
        backtest.WalkForwardAsync(Arg.Any<BacktestWalkForwardRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                // The engine's cancelled shape: only the fold whose whole
                // pass completed stays — the second never started.
                var request = ci.Arg<BacktestWalkForwardRequest>();
                return new BacktestWalkForwardResult(
                    BacktestWalkForwardOutcome.Cancelled,
                    request,
                    [
                        new BacktestWalkForwardRow(
                            Fold: 1,
                            InSampleBars: 64,
                            OutOfSampleBars: 64,
                            FirstValue: 0.5m,
                            SecondValue: 1m,
                            InSampleNet: 10m,
                            OutOfSampleNet: 4m,
                            OutOfSampleMaxDrawdown: 2m),
                    ]);
            });
        model.FoldsText = "2";

        await model.StartWalkForwardCommand.Execute(RxVoid.Default);

        model.Phase.Should().Be(BacktestRunPhase.Cancelled);
        model.HasWalkForwardRows.Should().BeTrue("the folds whose whole pass completed stay in the table");
        model.WalkForwardRows.Should().HaveCount(1);
        model.WalkForwardRows[0].Fold.Should().Be(1);
        model.ProgressText.Should().Be("[Backtest.WalkForward.Done.Format/1|2]");
        model.ProgressFraction.Should().Be(0.5d);
    }

    [Fact]
    public async Task StartWalkForwardCommand_ClearsTheOtherSurfaces()
    {
        var (model, backtest, _, _, _) = Build(new FakeStrategy("sma-golden-cross"));
        var report = Report(1_000_010m);
        backtest.RunAsync(Arg.Any<BacktestRunRequest>(), Arg.Any<CancellationToken>())
            .Returns(new BacktestRunResult(BacktestRunOutcome.Completed, report));
        backtest.MonteCarloAsync(Arg.Any<BacktestMonteCarloRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci => MonteCarlo(ci.Arg<BacktestMonteCarloRequest>()));
        backtest.WalkForwardAsync(Arg.Any<BacktestWalkForwardRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci => WalkForward(ci.Arg<BacktestWalkForwardRequest>()));
        await model.StartCommand.Execute(RxVoid.Default);
        await model.StartMonteCarloCommand.Execute(RxVoid.Default);
        model.HasMonteCarlo.Should().BeTrue("the pre-condition: tiles stand before the study clears them");

        model.FoldsText = "2";
        await model.StartWalkForwardCommand.Execute(RxVoid.Default);

        model.HasMonteCarlo.Should().BeFalse("the study's start clears the sampling's tiles");
        model.MonteCarloHeadline.Should().BeEmpty();
        model.HasSweepRows.Should().BeFalse();
        model.HasWalkForwardRows.Should().BeTrue();
        model.HasReport.Should().BeFalse("the study's start clears the report surface too");
    }

    #endregion Walk-Forward Composition

    #region Walk-Forward Progress Wrap

    [Fact]
    public async Task WalkForwardProgress_WrapsTheFoldEventsIntoTheGlobalCounter()
    {
        var (model, backtest, _, events, _) = Build(new FakeStrategy("sma-golden-cross"));
        var gate = new TaskCompletionSource<BacktestWalkForwardResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        backtest.WalkForwardAsync(Arg.Any<BacktestWalkForwardRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => gate.Task);
        model.FoldsText = "3";

        using (var attach = model.AttachRunEvents())
        {
            var parked = model.WhenAnyValue(vm => vm.IsRunning)
                .Where(running => running)
                .FirstAsync()
                .Timeout(ParkedWaitBudget)
                .ToTask();
            var done = new TaskCompletionSource<RxVoid>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var watcher = model.StartWalkForwardCommand.Execute(RxVoid.Default).Subscribe(
                value => done.TrySetResult(value),
                exception => done.SetException(exception),
                () => done.TrySetResult(RxVoid.Default));
            await parked;
            model.Phase.Should().Be(BacktestRunPhase.Running);

            // Fold 1's out-of-sample replay starts: the counter names fold 1 of 3.
            events.OnNext(new BacktestRunEvent(BacktestRunEventKind.Started, 64, 0, Report: null));
            await AvaloniaDispatcherPump.DrainAsync();
            model.ProgressText.Should().Be("[Backtest.WalkForward.Progress.Format/1|3|[Backtest.Progress.Format/0|64]]");
            model.ProgressFraction.Should().Be(0d);

            // The fold's mid bars: the fraction folds the fold into the study.
            events.OnNext(new BacktestRunEvent(BacktestRunEventKind.Progress, 64, 32, Report: null));
            await AvaloniaDispatcherPump.DrainAsync();
            model.ProgressText.Should().Be("[Backtest.WalkForward.Progress.Format/1|3|[Backtest.Progress.Format/32|64]]");
            model.ProgressFraction.Should().BeApproximately(0.5d / 3d, 1e-12, "(0 folds done + half the bars) / 3 folds");

            // Fold 1 completes exactly: the fraction lands on 1/3 and stays
            // there as fold 2 starts (never a backwards step).
            events.OnNext(new BacktestRunEvent(BacktestRunEventKind.Completed, 64, 64, Report: null));
            await AvaloniaDispatcherPump.DrainAsync();
            model.ProgressFraction.Should().BeApproximately(1d / 3d, 1e-12);

            events.OnNext(new BacktestRunEvent(BacktestRunEventKind.Started, 64, 0, Report: null));
            await AvaloniaDispatcherPump.DrainAsync();
            model.ProgressText.Should().Be("[Backtest.WalkForward.Progress.Format/2|3|[Backtest.Progress.Format/0|64]]");
            model.ProgressFraction.Should().BeApproximately(1d / 3d, 1e-12);

            // The user's stop at the fold boundary: the cancelled result
            // carries no completed folds here — the done line tells 0 of 3.
            gate.SetResult(new BacktestWalkForwardResult(
                BacktestWalkForwardOutcome.Cancelled,
                new BacktestWalkForwardRequest
                {
                    Template = Report(1m).Request,
                    FirstAxis = new BacktestSweepAxis(BacktestSweepParameter.LongNotionalFraction, [0.5m, 0.25m]),
                    SecondAxis = new BacktestSweepAxis(BacktestSweepParameter.SlippageValue, [1m, 2m]),
                    Folds = 3,
                },
                []));
            await done.Task;

            model.Phase.Should().Be(BacktestRunPhase.Cancelled);
            model.ProgressText.Should().Be("[Backtest.WalkForward.Done.Format/0|3]");
            model.ProgressFraction.Should().Be(0d);
        }
    }

    #endregion Walk-Forward Progress Wrap

    #region Monte Carlo

    [Fact]
    public async Task StartMonteCarloCommand_CanExecute_RequiresACompletedReport()
    {
        var (model, backtest, _, _, _) = Build(new FakeStrategy("sma-golden-cross"));

        ((System.Windows.Input.ICommand)model.StartMonteCarloCommand).CanExecute(RxVoid.Default).Should().BeFalse(
            "the sampling has no sequence without a completed report on the surface");

        var report = Report(1_000_010m);
        backtest.RunAsync(Arg.Any<BacktestRunRequest>(), Arg.Any<CancellationToken>())
            .Returns(new BacktestRunResult(BacktestRunOutcome.Completed, report));
        await model.StartCommand.Execute(RxVoid.Default);

        ((System.Windows.Input.ICommand)model.StartMonteCarloCommand).CanExecute(RxVoid.Default).Should().BeTrue(
            "the completed run's report is the sampling's own sequence");
    }

    [Fact]
    public async Task StartMonteCarloCommand_BadPathsText_RefusesWithItsOwnLine()
    {
        var (model, backtest, _, _, _) = Build(new FakeStrategy("sma-golden-cross"));
        var report = Report(1_000_010m);
        backtest.RunAsync(Arg.Any<BacktestRunRequest>(), Arg.Any<CancellationToken>())
            .Returns(new BacktestRunResult(BacktestRunOutcome.Completed, report));
        await model.StartCommand.Execute(RxVoid.Default);
        model.MonteCarloPathsText = "x";

        await model.StartMonteCarloCommand.Execute(RxVoid.Default);

        model.Phase.Should().Be(BacktestRunPhase.Failed);
        model.ErrorMessage.Should().Be("[Backtest.MonteCarlo.Error.Paths/x]");
        await backtest.DidNotReceive().MonteCarloAsync(
            Arg.Any<BacktestMonteCarloRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartMonteCarloCommand_BadSeedText_RefusesWithItsOwnLine()
    {
        var (model, backtest, _, _, _) = Build(new FakeStrategy("sma-golden-cross"));
        var report = Report(1_000_010m);
        backtest.RunAsync(Arg.Any<BacktestRunRequest>(), Arg.Any<CancellationToken>())
            .Returns(new BacktestRunResult(BacktestRunOutcome.Completed, report));
        await model.StartCommand.Execute(RxVoid.Default);
        model.MonteCarloSeedText = "y";

        await model.StartMonteCarloCommand.Execute(RxVoid.Default);

        model.Phase.Should().Be(BacktestRunPhase.Failed);
        model.ErrorMessage.Should().Be("[Backtest.MonteCarlo.Error.Seed/y]");
        await backtest.DidNotReceive().MonteCarloAsync(
            Arg.Any<BacktestMonteCarloRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartMonteCarloCommand_SamplesTheReportSurface_RendersTheTiles()
    {
        var (model, backtest, _, _, _) = Build(new FakeStrategy("sma-golden-cross"));
        var report = Report(1_000_010m);
        // A completed sweep stands in the tables first: its auto-selected
        // rank-1 report is the report surface (T-263) — the sampling's own
        // sequence rides THAT report, and the sweep's rows clear (the
        // sampling's surface keeps the report, never the tables).
        var sweepRequest = new BacktestSweepRequest
        {
            Template = report.Request,
            FirstAxis = new BacktestSweepAxis(BacktestSweepParameter.LongNotionalFraction, [0.5m]),
            SecondAxis = new BacktestSweepAxis(BacktestSweepParameter.SlippageValue, [1m]),
        };
        backtest.SweepAsync(Arg.Any<BacktestSweepRequest>(), Arg.Any<CancellationToken>())
            .Returns(new BacktestSweepResult(
                BacktestSweepOutcome.Completed,
                sweepRequest,
                [
                    new BacktestSweepRow(
                        1, 0, 0.5m, 1m,
                        Request: report.Request,
                        Statistics: report.Statistics),
                ],
                report));
        model.SweepFirstValuesText = "0.5";
        model.SweepSecondValuesText = "1";
        await model.StartSweepCommand.Execute(RxVoid.Default);
        model.HasSweepRows.Should().BeTrue();

        BacktestMonteCarloRequest? sampled = null;
        backtest.MonteCarloAsync(Arg.Any<BacktestMonteCarloRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                sampled = ci.Arg<BacktestMonteCarloRequest>();
                return MonteCarlo(sampled!);
            });
        model.MonteCarloPathsText = "2000";
        model.MonteCarloSeedText = "5";

        await model.StartMonteCarloCommand.Execute(RxVoid.Default);

        sampled.Should().NotBeNull();
        sampled!.Report.ToCanonicalText().Should().Be(report.ToCanonicalText(), "the sampling rides the report surface's own report");
        sampled.Paths.Should().Be(2000);
        sampled.Seed.Should().Be(5);

        model.Phase.Should().Be(BacktestRunPhase.Completed);
        model.HasReport.Should().BeTrue("the sampling keeps the report surface");
        model.ReportText.Should().Be(report.ToCanonicalText());
        model.HasWalkForwardRows.Should().BeFalse("the sampling's start clears the tables");
        model.HasSweepRows.Should().BeFalse("the sampling's start clears the tables");
        model.HasMonteCarlo.Should().BeTrue();
        model.MonteCarloHeadline.Should().Be("[Backtest.MonteCarlo.Headline.Format/2000|1|5]");
        model.MonteCarloBelowStart.Should().Be("[Backtest.MonteCarlo.BelowStart.Format/400|2000]");
        model.MonteCarloFinalEquityP5.Should().Be(Number(999_000m));
        model.MonteCarloFinalEquityP25.Should().Be(Number(999_500m));
        model.MonteCarloFinalEquityMedian.Should().Be(Number(1_000_000m));
        model.MonteCarloFinalEquityP75.Should().Be(Number(1_000_500m));
        model.MonteCarloFinalEquityP95.Should().Be(Number(1_001_000m));
        model.MonteCarloDrawdownP5.Should().Be(Number(0m));
        model.MonteCarloDrawdownMedian.Should().Be(Number(500m));
        model.MonteCarloDrawdownP95.Should().Be(Number(2_000m));
        model.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task StartMonteCarloCommand_CancelledSampling_AnswersNoTiles()
    {
        var (model, backtest, _, _, _) = Build(new FakeStrategy("sma-golden-cross"));
        var report = Report(1_000_010m);
        backtest.RunAsync(Arg.Any<BacktestRunRequest>(), Arg.Any<CancellationToken>())
            .Returns(new BacktestRunResult(BacktestRunOutcome.Completed, report));
        await model.StartCommand.Execute(RxVoid.Default);
        backtest.MonteCarloAsync(Arg.Any<BacktestMonteCarloRequest>(), Arg.Any<CancellationToken>())
            .Returns(BacktestMonteCarloResult.Cancelled);

        await model.StartMonteCarloCommand.Execute(RxVoid.Default);

        model.Phase.Should().Be(BacktestRunPhase.Cancelled);
        model.HasMonteCarlo.Should().BeFalse("the stop is the outcome; the cancelled sampling answers nothing");
        model.MonteCarloHeadline.Should().BeEmpty();
        model.MonteCarloFinalEquityP5.Should().BeEmpty();
        model.MonteCarloDrawdownP95.Should().BeEmpty();
    }

    #endregion Monte Carlo

    #region Language Mirror

    [Fact]
    public async Task LanguageSwitch_ReRendersTheFoldRows()
    {
        var (model, backtest, loc, _, languageChanges) = Build(new FakeStrategy("sma-golden-cross"));
        backtest.WalkForwardAsync(Arg.Any<BacktestWalkForwardRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci => WalkForward(ci.Arg<BacktestWalkForwardRequest>()));
        model.FoldsText = "2";
        await model.StartWalkForwardCommand.Execute(RxVoid.Default);
        model.HasWalkForwardRows.Should().BeTrue();

        var before = loc.RenderCalls;
        using (var attach = model.AttachRunEvents())
        {
            languageChanges.OnNext("ru");
            await AvaloniaDispatcherPump.DrainAsync();
        }

        // The fold rows re-resolve live: the fake resolver's render count
        // moves for the table's surfaces.
        (loc.RenderCalls - before).Should().BeGreaterThan(0);
        model.WalkForwardRows.Should().HaveCount(2);
        model.WalkForwardRows[0].ParameterText.Should().StartWith("[Backtest.Sweep.Param.Fraction]=");
        model.WalkForwardRows[0].ParameterText.Should().Contain("[Backtest.Sweep.Param.Slippage]=");
        model.WalkForwardRows[1].InSampleNetText.Should().Be(Number(5m));
    }

    [Fact]
    public async Task LanguageSwitch_ReRendersTheMonteCarloTiles()
    {
        var (model, backtest, loc, _, languageChanges) = Build(new FakeStrategy("sma-golden-cross"));
        var report = Report(1_000_010m);
        backtest.RunAsync(Arg.Any<BacktestRunRequest>(), Arg.Any<CancellationToken>())
            .Returns(new BacktestRunResult(BacktestRunOutcome.Completed, report));
        backtest.MonteCarloAsync(Arg.Any<BacktestMonteCarloRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci => MonteCarlo(ci.Arg<BacktestMonteCarloRequest>()));
        await model.StartCommand.Execute(RxVoid.Default);
        model.MonteCarloPathsText = "2000";
        model.MonteCarloSeedText = "5";
        await model.StartMonteCarloCommand.Execute(RxVoid.Default);
        model.HasMonteCarlo.Should().BeTrue();

        var before = loc.RenderCalls;
        using (var attach = model.AttachRunEvents())
        {
            languageChanges.OnNext("ru");
            await AvaloniaDispatcherPump.DrainAsync();
        }

        // The tiles re-resolve live: the fake resolver's render count moves
        // for the tiles' surfaces.
        (loc.RenderCalls - before).Should().BeGreaterThan(0);
        model.MonteCarloHeadline.Should().Be("[Backtest.MonteCarlo.Headline.Format/2000|1|5]");
        model.MonteCarloBelowStart.Should().Be("[Backtest.MonteCarlo.BelowStart.Format/400|2000]");
        model.MonteCarloFinalEquityP5.Should().Be(Number(999_000m));
        model.MonteCarloDrawdownP95.Should().Be(Number(2_000m));
    }

    #endregion Language Mirror
}
