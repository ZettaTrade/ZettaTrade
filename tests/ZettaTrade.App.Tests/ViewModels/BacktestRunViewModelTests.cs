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
/// The backtest run view model's facts (T-113): the form composes one
/// deterministic request (the active venue's id is the cached-series
/// adapter key), the completed report drives the canonical text, the
/// localized summary line and the equity points, garbage spellings refuse
/// the start each with its own localized line (nothing defaults — the
/// service is never called), the service's own refusal surfaces the honest
/// failure line, a cancelled run lands the cancelled phase with no report,
/// and the attach pattern renders the run-event progress, re-renders the
/// phase on a language switch, and dies with the returned handle. Facts
/// run inside the AvaloniaDispatcherSerial collection and settle through
/// the pump.
/// </summary>
[Collection("AvaloniaDispatcherSerial")]
public sealed class BacktestRunViewModelTests
{
    static BacktestRunViewModelTests() => AvaloniaDispatcherPump.EnsureInstalled();

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

    /// <summary>The one-crafted-ids catalog fake.</summary>
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

    /// <summary>The one-adapter offline facade fake: Current resolves to the first registered id.</summary>
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

    /// <summary>
    /// Build the model over a fresh harness substitute; the caller owns the
    /// substitute's plans (Events, RunAsync) and the localization fake.
    /// </summary>
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

    #region Completed Report

    [Fact]
    public async Task StartCommand_ComposesRequestFromTheForm_AndRendersTheCompletedReport()
    {
        var (model, backtest, _, _, _) = Build(
            new FakeStrategy("sma-golden-cross"),
            new FakeStrategy("ema-strategy"));
        var request = new BacktestRunRequest
        {
            StrategyId = "sma-golden-cross",
            AdapterId = "fake",
            Symbol = Symbol.Parse("BTC/USDT"),
            Interval = TimeFrame.H1,
        };
        var equity = new[]
        {
            new BacktestEquityPoint(Base, 1m),
            new BacktestEquityPoint(Base.AddHours(1), 2m),
        };
        var statistics = new BacktestStatistics(
            Fills: 2,
            Trades: 1,
            Wins: 1,
            Losses: 0,
            RealizedTotal: 1m,
            CommissionTotal: 0m,
            Volume: 200m,
            MaxDrawdown: 0m,
            PeakEquity: 2m,
            FinalEquity: 2m,
            Unrealized: 0m,
            AverageWinner: 1m,
            AverageLoser: null,
            WinRate: 1m,
            ProfitFactor: null);
        var report = new BacktestReport(request, equity, [], [], statistics);
        BacktestRunRequest? composed = null;
        backtest.RunAsync(Arg.Any<BacktestRunRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                composed = ci.Arg<BacktestRunRequest>();
                return Task.FromResult(new BacktestRunResult(BacktestRunOutcome.Completed, report));
            });

        await model.StartCommand.Execute(RxVoid.Default);

        composed.Should().NotBeNull();
        composed!.StrategyId.Should().Be("sma-golden-cross", "the picker lands on the catalog's first id");
        composed.AdapterId.Should().Be("fake", "the active venue is the cached-series adapter key");
        composed.Symbol.Normalized.Should().Be("BTC/USDT", "the default spelling parses through Symbol");
        composed.Interval.Should().Be(TimeFrame.H1);
        composed.Bars.Should().Be(BacktestRunRequest.DefaultBars);
        composed.Seed.Should().Be(BacktestRunRequest.DefaultSeed);
        composed.SlippageValue.Should().Be(BacktestRunRequest.DefaultSlippageValue);
        composed.LongNotionalFraction.Should().Be(BacktestRunRequest.DefaultLongNotionalFraction);
        model.Phase.Should().Be(BacktestRunPhase.Completed);
        model.IsRunning.Should().BeFalse();
        model.HasReport.Should().BeTrue();
        model.ReportText.Should().Be(report.ToCanonicalText());
        model.EquityPoints.Should().BeEquivalentTo(
            [
                new EquityCurvePoint(Base, 1m),
                new EquityCurvePoint(Base.AddHours(1), 2m),
            ]);
        model.HasEquity.Should().BeTrue();
        model.SummaryText.Should().StartWith(
            "[Backtest.Report.Summary.Format/2|1|",
            "the headline leads with fills and closed trades");
        model.ErrorMessage.Should().BeNull("a clean completed run carries no refusal");
    }

    [Fact]
    public async Task StartCommand_EmptyCatalog_RefusesWithItsOwnLine_AndNeverCallsTheService()
    {
        var (model, backtest, _, _, _) = Build();

        await model.StartCommand.Execute(RxVoid.Default);

        model.StrategyIds.Should().BeEmpty();
        model.Phase.Should().Be(BacktestRunPhase.Failed);
        model.ErrorMessage.Should().Be("[Backtest.Error.NoStrategy]");
        model.HasReport.Should().BeFalse();
        await backtest.DidNotReceive().RunAsync(
            Arg.Any<BacktestRunRequest>(),
            Arg.Any<CancellationToken>());
    }

    #endregion Completed Report

    #region Refusals

    [Fact]
    public async Task StartCommand_BlankSymbolRefusesWithItsOwnLine()
    {
        var (model, backtest, _, _, _) = Build(new FakeStrategy("sma-golden-cross"));
        model.SymbolText = "   ";

        await model.StartCommand.Execute(RxVoid.Default);

        model.Phase.Should().Be(BacktestRunPhase.Failed);
        model.ErrorMessage.Should().Be("[Backtest.Error.InvalidSymbol/   ]");
        model.IsRunning.Should().BeFalse();
        await backtest.DidNotReceive().RunAsync(
            Arg.Any<BacktestRunRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("Bars", "abc", "Backtest.Error.InvalidBars")]
    [InlineData("Seed", "x1", "Backtest.Error.InvalidSeed")]
    [InlineData("Slippage", "q", "Backtest.Error.InvalidSlippage")]
    [InlineData("Fraction", "1z", "Backtest.Error.InvalidFraction")]
    public async Task StartCommand_GarbageSpellingsRefuseEachWithItsOwnLine(
        string field,
        string garbage,
        string expectedKey)
    {
        var (model, backtest, _, _, _) = Build(new FakeStrategy("sma-golden-cross"));
        switch (field)
        {
            case "Bars":
                model.BarsText = garbage;
                break;
            case "Seed":
                model.SeedText = garbage;
                break;
            case "Slippage":
                model.SlippageText = garbage;
                break;
            case "Fraction":
                model.LongFractionText = garbage;
                break;
        }

        await model.StartCommand.Execute(RxVoid.Default);

        model.Phase.Should().Be(BacktestRunPhase.Failed);
        model.ErrorMessage.Should().Be("[" + expectedKey + "/" + garbage + "]");
        await backtest.DidNotReceive().RunAsync(
            Arg.Any<BacktestRunRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartCommand_ServiceRefusalSurfacesTheLocalizedStartFailedLine()
    {
        var (model, backtest, _, _, _) = Build(new FakeStrategy("sma-golden-cross"));
        backtest.RunAsync(Arg.Any<BacktestRunRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<BacktestRunResult>>(_ =>
                throw new InvalidOperationException("unknown strategy"));

        await model.StartCommand.Execute(RxVoid.Default);

        model.Phase.Should().Be(BacktestRunPhase.Failed);
        model.ErrorMessage.Should().Be("[Backtest.Error.StartFailed/unknown strategy]");
        model.IsRunning.Should().BeFalse();
        model.HasReport.Should().BeFalse();
    }

    #endregion Refusals

    #region Cancel

    [Fact]
    public async Task CancelCommand_MidRun_YieldsTheCancelledPhaseWithoutArtifacts()
    {
        var (model, backtest, _, _, _) = Build(new FakeStrategy("sma-golden-cross"));
        var gate = new TaskCompletionSource<BacktestRunResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        backtest.RunAsync(Arg.Any<BacktestRunRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => gate.Task);

        // Park the run: the IsRunning watcher subscribes before the command
        // starts (the chat's parked-run pattern), then the watcher rides the
        // execution through completion.
        var parked = model.WhenAnyValue(vm => vm.IsRunning)
            .Where(running => running)
            .FirstAsync()
            .Timeout(ParkedWaitBudget)
            .ToTask();
        var done = new TaskCompletionSource<RxVoid>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = model.StartCommand.Execute(RxVoid.Default).Subscribe(
            value => done.TrySetResult(value),
            exception => done.SetException(exception),
            () => done.TrySetResult(RxVoid.Default));
        await parked;
        model.Phase.Should().Be(BacktestRunPhase.Running);

        // The user stops: the run's own source answers, the parked task ends
        // in the cancelled envelope — the stop is the outcome.
        await model.CancelCommand.Execute(RxVoid.Default);
        gate.SetResult(BacktestRunResult.Cancelled);
        await done.Task;

        model.Phase.Should().Be(BacktestRunPhase.Cancelled);
        model.IsRunning.Should().BeFalse();
        model.LastReport.Should().BeNull("a cancelled run surfaces no report");
        model.HasReport.Should().BeFalse();
        model.ErrorMessage.Should().BeNull("the stop is the outcome, never an error");
    }

    #endregion Cancel

    #region Attach Pattern

    [Fact]
    public async Task AttachRunEvents_RendersProgressAndReRendersOnLanguageChange_AndDiesWithTheHandle()
    {
        var (model, backtest, loc, events, languageChanges) = Build(new FakeStrategy("sma-golden-cross"));

        using (var attach = model.AttachRunEvents())
        {
            events.OnNext(new BacktestRunEvent(BacktestRunEventKind.Started, 100, 0, Report: null));
            await AvaloniaDispatcherPump.DrainAsync();
            model.ProgressText.Should().Be("[Backtest.Progress.Format/0|100]");
            model.ProgressFraction.Should().Be(0d);

            events.OnNext(new BacktestRunEvent(BacktestRunEventKind.Progress, 100, 40, Report: null));
            await AvaloniaDispatcherPump.DrainAsync();
            model.ProgressText.Should().Be("[Backtest.Progress.Format/40|100]");
            model.ProgressFraction.Should().Be(0.4d);

            // A language switch re-renders the phase line live.
            var before = loc.RenderCalls;
            languageChanges.OnNext("ru");
            await AvaloniaDispatcherPump.DrainAsync();
            (loc.RenderCalls - before).Should().BeGreaterThan(0, "a language switch re-renders the phase");
        }

        // The handle's dispose kills the mirrors: no progress lands after it.
        var frozen = model.ProgressText;
        events.OnNext(new BacktestRunEvent(BacktestRunEventKind.Progress, 100, 90, Report: null));
        await AvaloniaDispatcherPump.DrainAsync();
        model.ProgressText.Should().Be(frozen, "the disposed attach handle ends the run-event mirror");
    }

    #endregion Attach Pattern
}
