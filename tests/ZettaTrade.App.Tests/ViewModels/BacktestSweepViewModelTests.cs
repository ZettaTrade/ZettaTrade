namespace ZettaTrade.App.Tests.ViewModels;

using System;
using System.Collections.Generic;
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
using ZettaTrade.Strategies;

/// <summary>The half-day-agnostic status byte ReactiveUI.Primitives names RxVoid.</summary>
using RxVoid = ReactiveUI.Primitives.RxVoid;

/// <summary>
/// The sweep surface's view-model facts (T-212): the sweep form composes
/// the grid from the run form (the template) and both axes (the value
/// lists parse and range-check explicitly — every malformed input refuses
/// with its own localized line and never reaches the service), a completed
/// sweep renders the ranked table and auto-selects rank 1 (the sweep's own
/// best report becomes the report surface — no second run, T-263), the
/// click-through re-replays the selected row's parameterization through the
/// single-run lane and swaps its report in, a cancelled sweep keeps the
/// completed rows with no auto-selection, the per-row events wrap into the
/// global progress counter, and a language switch re-renders the picker and
/// the rows. Facts run inside the AvaloniaDispatcherSerial collection.
/// </summary>
[Collection("AvaloniaDispatcherSerial")]
public sealed class BacktestSweepViewModelTests
{
    static BacktestSweepViewModelTests() => AvaloniaDispatcherPump.EnsureInstalled();

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

        public Task<IReadOnlyList<Symbol>> GetSymbolsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Symbol>>([]);

        public IObservable<Ticker> SubscribeTicker(Symbol symbol) => Observable.Empty<Ticker>();

        public IObservable<OrderBook> SubscribeOrderBook(Symbol symbol, int depth = 20) => Observable.Empty<OrderBook>();

        public IObservable<MarketTrade> SubscribeTrades(Symbol symbol) => Observable.Empty<MarketTrade>();

        public Task<IReadOnlyList<Bar>> GetBarsAsync(
            Symbol symbol,
            TimeFrame timeFrame,
            DateTimeOffset from,
            DateTimeOffset? to = null,
            int limit = 500,
            CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Bar>>([]);
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

    /// <summary>One minimal complete report: zero draws, the equity is the closing marks.</summary>
    private static BacktestReport Report(decimal finalEquity, decimal fraction = 1m, decimal slippage = 0m)
    {
        var request = new BacktestRunRequest
        {
            StrategyId = "sma-golden-cross",
            AdapterId = "fake",
            Symbol = Symbol.Parse("BTC/USDT"),
            Interval = TimeFrame.H1,
            Bars = 100,
            Seed = 7,
            LongNotionalFraction = fraction,
            SlippageValue = slippage,
        };
        var equity = new[] { new BacktestEquityPoint(Base, 1m), new BacktestEquityPoint(Base.AddHours(1), finalEquity) };
        var statistics = new BacktestStatistics(
            Fills: 2,
            Trades: 1,
            Wins: 1,
            Losses: 0,
            RealizedTotal: finalEquity - 1m,
            CommissionTotal: 0m,
            Volume: 200m,
            MaxDrawdown: 0.5m,
            PeakEquity: finalEquity,
            FinalEquity: finalEquity,
            Unrealized: 0m,
            AverageWinner: finalEquity - 1m,
            AverageLoser: null,
            WinRate: 1m,
            ProfitFactor: null);
        return new BacktestReport(request, equity, [], [], statistics);
    }

    #region Composition

    [Fact]
    public async Task StartSweepCommand_ComposesTheGrid_FromTheRunFormAndBothAxes()
    {
        var (model, backtest, _, _, _) = Build(new FakeStrategy("sma-golden-cross"));
        BacktestSweepRequest? composed = null;
        BacktestReport? bestReport = null;
        backtest.SweepAsync(Arg.Any<BacktestSweepRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                composed = ci.Arg<BacktestSweepRequest>();
                var request = composed!;
                var first = request.FirstAxis.Values;
                var second = request.SecondAxis.Values;
                var rows = new List<BacktestSweepRow>();
                var reports = new List<BacktestReport>();
                var index = 0;
                foreach (var firstValue in first)
                {
                    foreach (var secondValue in second)
                    {
                        var report = Report(100m + index, firstValue, secondValue);
                        reports.Add(report);
                        rows.Add(new BacktestSweepRow(
                            Rank: 0,
                            Index: index,
                            FirstValue: firstValue,
                            SecondValue: secondValue,
                            Request: report.Request,
                            Statistics: report.Statistics));
                        index++;
                    }
                }

                var ranked = rows
                    .OrderByDescending(static row => row.Net)
                    .ThenBy(static row => row.Index)
                    .Select((row, rank) => row with { Rank = rank + 1 })
                    .ToList();
                // NSubstitute wraps the lambda's own result value for the
                // method's Task return shape — no explicit wrap needed. The
                // best report is rank 1's own replay (T-263: the engine's
                // completed sweep always carries it).
                bestReport = reports[ranked[0].Index];
                return new BacktestSweepResult(
                    BacktestSweepOutcome.Completed,
                    request,
                    ranked,
                    bestReport);
            });
        // A tight grid keeps the done line's arithmetic readable: 2 x 1.
        model.SweepFirstValuesText = "0.25, 0.5";
        model.SweepSecondValuesText = "1";

        await model.StartSweepCommand.Execute(RxVoid.Default);

        composed.Should().NotBeNull();
        composed!.Template.StrategyId.Should().Be("sma-golden-cross", "the template rides the run form");
        composed.Template.AdapterId.Should().Be("fake");
        composed.Template.Bars.Should().Be(BacktestRunRequest.DefaultBars);
        composed.Template.Seed.Should().Be(BacktestRunRequest.DefaultSeed);
        composed.Template.SlippageValue.Should().Be(BacktestRunRequest.DefaultSlippageValue);
        composed.Template.LongNotionalFraction.Should().Be(BacktestRunRequest.DefaultLongNotionalFraction);
        composed.FirstAxis.Parameter.Should().Be(BacktestSweepParameter.LongNotionalFraction, "the picker's first row");
        composed.FirstAxis.Values.Should().Equal([0.25m, 0.5m]);
        composed.SecondAxis.Parameter.Should().Be(BacktestSweepParameter.SlippageValue);
        composed.SecondAxis.Values.Should().Equal([1m]);

        // The ranked table renders, rank 1 auto-selects and its report
        // becomes the report surface.
        model.Phase.Should().Be(BacktestRunPhase.Completed);
        model.HasSweepRows.Should().BeTrue();
        model.SweepRows.Should().HaveCount(2);
        model.SweepRows.Select(row => row.Rank).Should().Equal([1, 2]);
        model.SelectedSweepRow.Should().Be(model.SweepRows[0]);
        model.HasReport.Should().BeTrue();
        model.ReportText.Should().Be(bestReport!.ToCanonicalText());
        model.ProgressText.Should().Be("[Backtest.Sweep.Done.Format/2|2]");
        model.ProgressFraction.Should().Be(1d);
        model.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task StartSweepCommand_ClickThrough_RankOneShowsTheSweepsOwnReport_AndDeeperRowsReRun()
    {
        var (model, backtest, _, _, _) = Build(new FakeStrategy("sma-golden-cross"));
        var firstReport = Report(110m, fraction: 0.25m, slippage: 1m);
        var secondReport = Report(120m, fraction: 0.5m, slippage: 1m);
        var sweepRequest = new BacktestSweepRequest
        {
            Template = firstReport.Request,
            FirstAxis = new BacktestSweepAxis(BacktestSweepParameter.LongNotionalFraction, [0.25m, 0.5m]),
            SecondAxis = new BacktestSweepAxis(BacktestSweepParameter.SlippageValue, [1m]),
        };
        backtest.SweepAsync(Arg.Any<BacktestSweepRequest>(), Arg.Any<CancellationToken>())
            .Returns(new BacktestSweepResult(
                BacktestSweepOutcome.Completed,
                sweepRequest,
                [
                    new BacktestSweepRow(
                        1, 0, 0.25m, 1m,
                        Request: firstReport.Request,
                        Statistics: firstReport.Statistics),
                    new BacktestSweepRow(
                        2, 1, 0.5m, 1m,
                        Request: secondReport.Request,
                        Statistics: secondReport.Statistics),
                ],
                // The engine's own best report: rank 1's replay (T-263).
                firstReport));
        backtest.RunAsync(Arg.Any<BacktestRunRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new BacktestRunResult(BacktestRunOutcome.Completed, secondReport)));
        model.SweepFirstValuesText = "0.25, 0.5";
        model.SweepSecondValuesText = "1";

        await model.StartSweepCommand.Execute(RxVoid.Default);
        // Rank 1 auto-selected: the report surface shows the sweep's own best
        // report — no second run was ever needed for it.
        var rankOneText = model.ReportText;
        rankOneText.Should().Be(firstReport.ToCanonicalText());
        await backtest.DidNotReceive().RunAsync(
            Arg.Any<BacktestRunRequest>(),
            Arg.Any<CancellationToken>());

        model.SelectedSweepRow = model.SweepRows[1];

        // The deeper row re-runs the single lane with the row's own request
        // and swaps the report in when the replay completes.
        await backtest.Received().RunAsync(
            Arg.Is<BacktestRunRequest>(request => request.LongNotionalFraction == 0.5m && request.SlippageValue == 1m),
            Arg.Any<CancellationToken>());
        model.Phase.Should().Be(BacktestRunPhase.Completed);
        model.ReportText.Should().NotBe(rankOneText);
        model.ReportText.Should().Be(secondReport.ToCanonicalText());
        model.EquityPoints.Should().BeEquivalentTo(
            [
                new EquityCurvePoint(Base, 1m),
                new EquityCurvePoint(Base.AddHours(1), 120m),
            ]);
        model.SummaryText.Should().StartWith("[Backtest.Report.Summary.Format/2|1|");
    }

    #endregion Composition

    #region Refusals

    [Fact]
    public async Task StartSweepCommand_UnparsableValuesText_RefusesWithItsOwnLine()
    {
        var (model, backtest, _, _, _) = Build(new FakeStrategy("sma-golden-cross"));
        model.SweepFirstValuesText = "1, x";

        await model.StartSweepCommand.Execute(RxVoid.Default);

        model.Phase.Should().Be(BacktestRunPhase.Failed);
        model.ErrorMessage.Should().Be("[Backtest.Sweep.Error.Values/1, x]");
        await backtest.DidNotReceive().SweepAsync(
            Arg.Any<BacktestSweepRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartSweepCommand_TooLongAxis_RefusesWithItsOwnLine()
    {
        var (model, backtest, _, _, _) = Build(new FakeStrategy("sma-golden-cross"));
        model.SweepFirstValuesText = "1,2,3,4,5,6,7,8,9";

        await model.StartSweepCommand.Execute(RxVoid.Default);

        model.Phase.Should().Be(BacktestRunPhase.Failed);
        model.ErrorMessage.Should().Be("[Backtest.Sweep.Error.Count/9|8]");
        await backtest.DidNotReceive().SweepAsync(
            Arg.Any<BacktestSweepRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartSweepCommand_SameKnobOnBothAxes_RefusesWithItsOwnLine()
    {
        var (model, backtest, _, _, _) = Build(new FakeStrategy("sma-golden-cross"));
        model.SweepSecondIndex = 0;

        await model.StartSweepCommand.Execute(RxVoid.Default);

        model.Phase.Should().Be(BacktestRunPhase.Failed);
        model.ErrorMessage.Should().Be("[Backtest.Sweep.Error.SameParameter]");
        await backtest.DidNotReceive().SweepAsync(
            Arg.Any<BacktestSweepRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartSweepCommand_OutOfRangeValue_RefusesWithItsOwnLine()
    {
        var (model, backtest, _, _, _) = Build(new FakeStrategy("sma-golden-cross"));
        model.SweepFirstValuesText = "0, 0.5";

        await model.StartSweepCommand.Execute(RxVoid.Default);

        model.Phase.Should().Be(BacktestRunPhase.Failed);
        model.ErrorMessage.Should().Be(
            "[Backtest.Sweep.Error.Range/0|[Backtest.Sweep.Param.Fraction]]",
            "the zero fraction lies outside (0, 1] and is refused, never clamped");
        await backtest.DidNotReceive().SweepAsync(
            Arg.Any<BacktestSweepRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartSweepCommand_TemplateGarbage_RefusesWithTheRunFormsLine()
    {
        var (model, backtest, _, _, _) = Build(new FakeStrategy("sma-golden-cross"));
        model.BarsText = "abc";

        await model.StartSweepCommand.Execute(RxVoid.Default);

        model.Phase.Should().Be(BacktestRunPhase.Failed);
        model.ErrorMessage.Should().Be("[Backtest.Error.InvalidBars/abc]");
        await backtest.DidNotReceive().SweepAsync(
            Arg.Any<BacktestSweepRequest>(),
            Arg.Any<CancellationToken>());
    }

    #endregion Refusals

    #region Cancelled Outcome

    [Fact]
    public async Task StartSweepCommand_CancelledSweep_KeepsTheCompletedRows_WithoutAutoSelection()
    {
        var (model, backtest, _, _, _) = Build(new FakeStrategy("sma-golden-cross"));
        var completedReport = Report(105m, fraction: 0.25m, slippage: 1m);
        var sweepRequest = new BacktestSweepRequest
        {
            Template = completedReport.Request,
            FirstAxis = new BacktestSweepAxis(BacktestSweepParameter.LongNotionalFraction, [0.25m, 0.5m]),
            SecondAxis = new BacktestSweepAxis(BacktestSweepParameter.SlippageValue, [1m]),
        };
        backtest.SweepAsync(Arg.Any<BacktestSweepRequest>(), Arg.Any<CancellationToken>())
            .Returns(new BacktestSweepResult(
                BacktestSweepOutcome.Cancelled,
                sweepRequest,
                [
                    new BacktestSweepRow(
                        1, 0, 0.25m, 1m,
                        Request: completedReport.Request,
                        Statistics: completedReport.Statistics),
                ]));
        model.SweepFirstValuesText = "0.25, 0.5";
        model.SweepSecondValuesText = "1";

        await model.StartSweepCommand.Execute(RxVoid.Default);

        model.Phase.Should().Be(BacktestRunPhase.Cancelled);
        model.HasSweepRows.Should().BeTrue("the parameterization whose replay completed stays in the table");
        model.SweepRows.Should().HaveCount(1);
        model.SweepRows[0].Rank.Should().Be(1);
        model.LastReport.Should().BeNull("the stop is the outcome; a click-through only opens a row's report");
        model.SelectedSweepRow.Should().BeNull();
        model.HasReport.Should().BeFalse();
        model.ProgressText.Should().Be("[Backtest.Sweep.Done.Format/1|2]");
        model.ProgressFraction.Should().Be(0.5d);
    }

    #endregion Cancelled Outcome

    #region Progress Wrap

    [Fact]
    public async Task SweepProgress_WrapsTheRowEventsIntoTheGlobalCounter()
    {
        var (model, backtest, _, events, _) = Build(new FakeStrategy("sma-golden-cross"));
        var gate = new TaskCompletionSource<BacktestSweepResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        backtest.SweepAsync(Arg.Any<BacktestSweepRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => gate.Task);
        model.SweepFirstValuesText = "0.25, 0.5";
        model.SweepSecondValuesText = "1";

        using (var attach = model.AttachRunEvents())
        {
            // Park the sweep: the IsRunning watcher subscribes before the
            // command starts, then the events ride the execution through
            // the gate's release.
            var parked = model.WhenAnyValue(vm => vm.IsRunning)
                .Where(running => running)
                .FirstAsync()
                .Timeout(ParkedWaitBudget)
                .ToTask();
            var done = new TaskCompletionSource<RxVoid>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var watcher = model.StartSweepCommand.Execute(RxVoid.Default).Subscribe(
                value => done.TrySetResult(value),
                exception => done.SetException(exception),
                () => done.TrySetResult(RxVoid.Default));
            await parked;
            model.Phase.Should().Be(BacktestRunPhase.Running);

            // Row 1 starts: the counter names row 1 of 2.
            events.OnNext(new BacktestRunEvent(BacktestRunEventKind.Started, 100, 0, Report: null));
            await AvaloniaDispatcherPump.DrainAsync();
            model.ProgressText.Should().Be("[Backtest.Sweep.Progress.Format/1|2|[Backtest.Progress.Format/0|100]]");
            model.ProgressFraction.Should().Be(0d);

            // Row 1's mid bars: the fraction folds the row into the grid.
            events.OnNext(new BacktestRunEvent(BacktestRunEventKind.Progress, 100, 50, Report: null));
            await AvaloniaDispatcherPump.DrainAsync();
            model.ProgressText.Should().Be("[Backtest.Sweep.Progress.Format/1|2|[Backtest.Progress.Format/50|100]]");
            model.ProgressFraction.Should().Be(0.25d, "(0 rows done + half the bars) / 2 rows");

            // Row 1 completes exactly: the fraction lands on 1/2 and stays
            // there as row 2 starts (never a backwards step).
            events.OnNext(new BacktestRunEvent(BacktestRunEventKind.Completed, 100, 100, Report: null));
            await AvaloniaDispatcherPump.DrainAsync();
            model.ProgressFraction.Should().Be(0.5d);

            events.OnNext(new BacktestRunEvent(BacktestRunEventKind.Started, 100, 0, Report: null));
            await AvaloniaDispatcherPump.DrainAsync();
            model.ProgressText.Should().Be("[Backtest.Sweep.Progress.Format/2|2|[Backtest.Progress.Format/0|100]]");
            model.ProgressFraction.Should().Be(0.5d);

            // The user's stop at the boundary: the cancelled result carries
            // no rows here — the done line still tells 0 of 2.
            gate.SetResult(new BacktestSweepResult(
                BacktestSweepOutcome.Cancelled,
                new BacktestSweepRequest
                {
                    Template = Report(1m).Request,
                    FirstAxis = new BacktestSweepAxis(BacktestSweepParameter.LongNotionalFraction, [0.25m, 0.5m]),
                    SecondAxis = new BacktestSweepAxis(BacktestSweepParameter.SlippageValue, [1m]),
                },
                []));
            await done.Task;

            model.Phase.Should().Be(BacktestRunPhase.Cancelled);
            model.ProgressText.Should().Be("[Backtest.Sweep.Done.Format/0|2]");
            model.ProgressFraction.Should().Be(0d);
        }
    }

    #endregion Progress Wrap

    #region Language Mirror

    [Fact]
    public async Task LanguageSwitch_ReRendersThePickerAndEveryTableRow()
    {
        var (model, backtest, loc, _, languageChanges) = Build(new FakeStrategy("sma-golden-cross"));
        var report = Report(105m, fraction: 0.25m, slippage: 1m);
        var sweepRequest = new BacktestSweepRequest
        {
            Template = report.Request,
            FirstAxis = new BacktestSweepAxis(BacktestSweepParameter.LongNotionalFraction, [0.25m]),
            SecondAxis = new BacktestSweepAxis(BacktestSweepParameter.SlippageValue, [1m]),
        };
        backtest.SweepAsync(Arg.Any<BacktestSweepRequest>(), Arg.Any<CancellationToken>())
            .Returns(new BacktestSweepResult(
                BacktestSweepOutcome.Completed,
                sweepRequest,
                [
                    new BacktestSweepRow(
                        1, 0, 0.25m, 1m,
                        Request: report.Request,
                        Statistics: report.Statistics),
                ],
                report));
        model.SweepFirstValuesText = "0.25";
        model.SweepSecondValuesText = "1";
        await model.StartSweepCommand.Execute(RxVoid.Default);
        model.SweepParameters.Should().Equal(
            ["[Backtest.Sweep.Param.Fraction]", "[Backtest.Sweep.Param.Slippage]"]);
        model.SweepRows[0].ParameterText.Should().StartWith(
            "[Backtest.Sweep.Param.Fraction]=",
            "the row's pair renders through the picker's own live labels");
        model.SweepRows[0].ParameterText.Should().Contain("[Backtest.Sweep.Param.Slippage]=");

        var before = loc.RenderCalls;
        using (var attach = model.AttachRunEvents())
        {
            languageChanges.OnNext("ru");
            await AvaloniaDispatcherPump.DrainAsync();
        }

        // The picker's labels and the row's texts re-resolve live: the fake
        // resolver's render count moves for both surfaces.
        (loc.RenderCalls - before).Should().BeGreaterThan(0);
        model.SweepParameters.Should().HaveCount(2);
        model.SweepFirstIndex.Should().Be(0, "the selection survives the label list swap");
        model.SweepSecondIndex.Should().Be(1);
        model.SweepRows[0].ParameterText.Should().StartWith("[Backtest.Sweep.Param.Fraction]=");
        model.SweepRows[0].ParameterText.Should().Contain("[Backtest.Sweep.Param.Slippage]=");
    }

    #endregion Language Mirror
}
