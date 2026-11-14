namespace ZettaTrade.App.Tests.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using ZettaTrade.App.Services;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.Infrastructure.Exchange;

/// <summary>
/// The palette's instrument source (T-095): ONE snapshot aggregating ALL
/// registered adapters in registration order, a faulting adapter
/// contributing zero rows without breaking the whole, an epoch drop
/// discarding a fetch superseded by a newer composition, the warm-up
/// riding IAsyncInitializable without blocking, and every adapter change
/// kicking a re-fetch through the subscribed stream — never a bare task.
/// </summary>
public sealed class SymbolPaletteSourceTests
{
    #region The fake adapter

    /// <summary>
    /// Market data fake with a scripted symbol plan: the plan pops one
    /// composition per fetch (the last one stands), an optional gate parks
    /// the FIRST fetch mid-flight (the epoch drop's parked arm), and an
    /// optional one-shot fault serves the first attempt broken.
    /// </summary>
    private sealed class FakeMarketData : IMarketDataAdapter
    {
        private readonly Queue<IReadOnlyList<Symbol>> _symbolPlans;
        private int _fetches;

        public FakeMarketData(
            string id,
            string name,
            IReadOnlyList<string>? standingSymbols = null,
            IReadOnlyList<IReadOnlyList<Symbol>>? symbolPlan = null,
            TaskCompletionSource? parkFirstFetch = null,
            bool throwOnFirstFetch = false,
            bool throwMissingCredentialsEveryFetch = false)
        {
            Id = id;
            Name = name;
            _symbolPlans = new Queue<IReadOnlyList<Symbol>>();
            if (symbolPlan is { } plan)
            {
                foreach (var composition in plan)
                {
                    _symbolPlans.Enqueue(composition);
                }
            }
            else if (standingSymbols is { } standing)
            {
                _symbolPlans.Enqueue(standing.Select(Symbol.Parse).ToArray());
            }

            ParkFirstFetch = parkFirstFetch;
            ThrowOnFirstFetch = throwOnFirstFetch;
            ThrowMissingCredentialsEveryFetch = throwMissingCredentialsEveryFetch;
        }

        public string Id { get; }

        public string Name { get; }

        public bool IsConnected => true;

        public ExchangeCapabilities Capabilities => ExchangeCapabilities.MarketData;

        public int SymbolFetches { get; private set; }

        /// <summary>When set, the FIRST fetch hangs until release — the parking arm of the epoch race.</summary>
        private TaskCompletionSource? ParkFirstFetch { get; }

        /// <summary>The first attempt faults (a one-shot network blip).</summary>
        private bool ThrowOnFirstFetch { get; }

        /// <summary>Every fetch faults with the standing no-credentials error (the T-227 typed absence).</summary>
        private bool ThrowMissingCredentialsEveryFetch { get; }

        public async Task<IReadOnlyList<Symbol>> GetSymbolsAsync(CancellationToken ct = default)
        {
            _fetches++;
            SymbolFetches = _fetches;

            if (ThrowMissingCredentialsEveryFetch)
            {
                throw new AdapterCredentialsMissingException(Id, AlpacaAdapter.ApiKeySecretKey);
            }

            if (_fetches == 1 && ThrowOnFirstFetch)
            {
                throw new HttpRequestException("offline");
            }

            if (_fetches == 1 && ParkFirstFetch is { } gate)
            {
                await gate.Task;
            }

            // The plan pops one composition per fetch; the last one stands
            // (what a live feed approximates between refreshes).
            return _symbolPlans.Count > 1
                ? _symbolPlans.Dequeue()
                : _symbolPlans.Peek();
        }

        public async Task<IReadOnlyList<Bar>> GetBarsAsync(
            Symbol symbol,
            TimeFrame timeFrame,
            DateTimeOffset from,
            DateTimeOffset? to = null,
            int limit = 500,
            CancellationToken ct = default)
        {
            await Task.CompletedTask;
            return [];
        }

        public IObservable<Ticker> SubscribeTicker(Symbol symbol) => Observable.Never<Ticker>();

        public IObservable<OrderBook> SubscribeOrderBook(Symbol symbol, int depth = 20) =>
            Observable.Never<OrderBook>();

        public IObservable<MarketTrade> SubscribeTrades(Symbol symbol) => Observable.Never<MarketTrade>();

        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    #endregion The fake adapter

    #region Kit

    private static ActiveExchangeService BuildService(params IMarketDataAdapter[] adapters)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.Changes.Returns(Observable.Empty<SettingsChange>());
        settings.GetAsync<string>(Arg.Any<string>(), Arg.Any<string>())
            .Returns(string.Empty);
        return new ActiveExchangeService(
            adapters,
            settings,
            NullLogger<ActiveExchangeService>.Instance);
    }

    private static SymbolPaletteSource BuildSource(ActiveExchangeService service) => new(
        service,
        new SymbolListProvider(service, NullLogger<SymbolListProvider>.Instance),
        NullLogger<SymbolPaletteSource>.Instance);

    /// <summary>
    /// Read the subject's current latest without blocking.
    /// </summary>
    private static async Task<PaletteSnapshot> CurrentSnapshotAsync(SymbolPaletteSource source) =>
        await source.Snapshots.FirstAsync();

    /// <summary>
    /// Subscribe the next publication's promise. Call it BEFORE the action
    /// that publishes (the kick, the warm-up) — the behavioral subject
    /// delivers to already-subscribed observers only, so a promise made
    /// after the publish would wait a second publish that never comes.
    /// </summary>
    private static (Task<PaletteSnapshot> Promise, IDisposable Subscription) NextPublicationPromise(
        SymbolPaletteSource source)
    {
        var promise = new TaskCompletionSource<PaletteSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var subscription = source.Snapshots.Skip(1).Subscribe(
            snapshot => promise.TrySetResult(snapshot),
            ex => promise.TrySetException(ex));
        return (promise.Task, subscription);
    }

    /// <summary>
    /// Collect a promise with a bounded grace: the kick paths' completion
    /// surface is the subject only, and a broken fetch must surface as a
    /// test failure, not a hang.
    /// </summary>
    private static async Task<PaletteSnapshot> AwaitPublicationAsync(
        Task<PaletteSnapshot> promise) =>
        await promise.WaitAsync(TimeSpan.FromSeconds(5));

    private static string[] RowNames(PaletteSnapshot snapshot) =>
        snapshot.Instruments
            .Select(static item => $"{item.ExchangeId}:{item.Symbol.Normalized}")
            .ToArray();

    #endregion Kit

    #region Aggregation

    [Fact]
    public async Task ARefresh_AggregatesEveryAdapter_InRegistrationOrder()
    {
        var service = BuildService(
            new FakeMarketData("beta", "Beta", standingSymbols: ["BTC/USDT", "ETH/BTC"]),
            new FakeMarketData("alpha", "Alpha", standingSymbols: ["SOL/USDC"]));
        var source = BuildSource(service);

        await source.RefreshOnceAsync(CancellationToken.None);
        var snapshot = await CurrentSnapshotAsync(source);

        snapshot.Settled.Should().BeTrue();
        // Each adapter's own order kept, the catalogue order defining the page.
        RowNames(snapshot).Should().Equal(
            "beta:BTC/USDT",
            "beta:ETH/BTC",
            "alpha:SOL/USDC");
        // The match key is the uppercase normalized name, fixed once at the source.
        snapshot.Instruments.Select(static item => item.MatchKey)
            .Should().Equal(
                "BTC/USDT",
                "ETH/BTC",
                "SOL/USDC");
    }

    [Fact]
    public async Task DuplicatedPairsPerAdapter_StayOneRowEach()
    {
        // The same normalized pair on both adapters is TWO instruments (the
        // exchange is part of the pair's identity); the dedupe, when any,
        // is the ladder's concern, never the source's.
        var service = BuildService(
            new FakeMarketData("beta", "Beta", standingSymbols: ["BTC/USDT"]),
            new FakeMarketData("alpha", "Alpha", standingSymbols: ["BTC/USDT"]));
        var source = BuildSource(service);

        await source.RefreshOnceAsync(CancellationToken.None);
        var snapshot = await CurrentSnapshotAsync(source);

        RowNames(snapshot).Should().Equal("beta:BTC/USDT", "alpha:BTC/USDT");
    }

    #endregion Aggregation

    #region Fault containment

    [Fact]
    public async Task AFaultingAdapter_ContributesZeroRows_AndTheOthersServe()
    {
        // The fault is a completed attempt: the snapshot settles honestly
        // over the healthy rows only — no fabricated substitution, no
        // broken whole.
        var service = BuildService(
            new FakeMarketData("beta", "Beta", standingSymbols: ["BTC/USDT"], throwOnFirstFetch: true),
            new FakeMarketData("alpha", "Alpha", standingSymbols: ["SOL/USDC"]));
        var source = BuildSource(service);

        await source.RefreshOnceAsync(CancellationToken.None);
        var snapshot = await CurrentSnapshotAsync(source);

        snapshot.Settled.Should().BeTrue();
        // The broken adapter invented nothing; the healthy one served.
        RowNames(snapshot).Should().Equal("alpha:SOL/USDC");
    }

    [Fact]
    public async Task EveryAdapterSettlingEmpty_IsAnEmptySettledSnapshot()
    {
        var service = BuildService(
            new FakeMarketData("beta", "Beta", standingSymbols: []),
            new FakeMarketData("alpha", "Alpha", standingSymbols: []));
        var source = BuildSource(service);

        await source.RefreshOnceAsync(CancellationToken.None);
        var snapshot = await CurrentSnapshotAsync(source);

        snapshot.Settled.Should().BeTrue();
        snapshot.Instruments.Should().BeEmpty(
            "empty adapters are a settled-empty aggregate, not a loading one");
    }

    [Fact]
    public async Task ACredentialsMissingAdapter_ContributesZeroRows_Quietly()
    {
        // The standing absence (T-227): the no-key adapter contributes zero
        // rows without a single warning — the adapter reports the absence
        // itself once per session, the palette's repeated refreshes ride
        // the debug level silently.
        var recording = new RecordingLogger();
        var service = BuildService(
            new FakeMarketData("beta", "Beta", throwMissingCredentialsEveryFetch: true),
            new FakeMarketData("alpha", "Alpha", standingSymbols: ["SOL/USDC"]));
        var source = new SymbolPaletteSource(
            service,
            new SymbolListProvider(service, NullLogger<SymbolListProvider>.Instance),
            recording);

        await source.RefreshOnceAsync(CancellationToken.None);
        var snapshot = await CurrentSnapshotAsync(source);

        snapshot.Settled.Should().BeTrue();
        RowNames(snapshot).Should().Equal("alpha:SOL/USDC");
        recording.Entries
            .Where(entry => entry.Level >= LogLevel.Warning)
            .Should().BeEmpty("a standing absence is not a failure family");
        recording.Entries.Should().Contain(
            entry => entry.Level == LogLevel.Debug && entry.Message.Contains("beta"),
            "the absence rides debug for the test runs");
    }

    #endregion Fault containment

    #region The epoch drop

    [Fact]
    public async Task AFetchSupersededByANewerComposition_PublishesNothing()
    {
        // The first fetch parks inside the gate; an adapter change bumps
        // the epoch and kicks a re-fetch (the kick queues behind the held
        // semaphore); when the parked fetch releases, its rows must never
        // publish — the newer composition does.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var beta = new FakeMarketData(
            "beta",
            "Beta",
            symbolPlan:
            [
                [Symbol.Parse("BTC/USDT")],
                [Symbol.Parse("SOL/BETA")],
            ],
            parkFirstFetch: gate);
        var alpha = new FakeMarketData("alpha", "Alpha", standingSymbols: ["SOL/USDC"]);
        var service = BuildService(beta, alpha);
        var source = BuildSource(service);

        var observed = new List<PaletteSnapshot>();
        using var recording = source.Snapshots.Subscribe(observed.Add);

        // Start the refresh: the synchronous prologue runs on this thread —
        // the semaphore is free, the epoch is read, and the beta fetch
        // parks inside the fake's gate.
        var parked = source.RefreshOnceAsync(CancellationToken.None);
        observed.Should().ContainSingle("only the behavioral seed landed; nothing published yet");
        parked.IsCompleted.Should().BeFalse("the fetch waits behind the parking gate");

        // The kick's own subscription rides the switch; promise the next
        // publication BEFORE the gate releases, so landings during the
        // interleaved unwind are collected either way.
        var (kickedPromise, kickedSubscription) = NextPublicationPromise(source);
        using var _ = kickedSubscription;
        await service.SwitchToAsync("alpha");

        // Release: the parked (now stale) fetch unwinds WITHOUT publishing
        // and frees the gate; the kick's fetch then publishes the new one.
        gate.TrySetResult();
        await parked;

        var kicked = await AwaitPublicationAsync(kickedPromise);
        // The recording subscription captured everything already; the
        // parked composition stayed unpublished (the epoch drop), so only
        // the behavioral seed and the kick's publication stand.
        observed.Should().HaveCount(2, "the parked composition stays unpublished; only the kick lands");
        observed[0].Settled.Should().BeFalse("the behavioral seed carries no rows");
        observed[1].Should().BeSameAs(kicked, "the kick's publication is the subject's only post-seed one");
        // The kick re-fetched everything still registered.
        RowNames(kicked).Should().Equal(
            "beta:SOL/BETA",
            "alpha:SOL/USDC");
        observed.SelectMany(static s => s.Instruments.Select(static i => i.Symbol.Normalized))
            .Should().NotContain(
                "BTC/USDT",
                "the superseded composition's rows must not leak into any later publish");
    }

    [Fact]
    public async Task AnAdapterChange_KicksAReFetchWithoutAManualCall()
    {
        var beta = new FakeMarketData(
            "beta",
            "Beta",
            symbolPlan:
            [
                [Symbol.Parse("BTC/USDT")],
                [Symbol.Parse("ETH/BTC")],
            ]);
        var alpha = new FakeMarketData(
            "alpha",
            "Alpha",
            symbolPlan:
            [
                [Symbol.Parse("SOL/USDC")],
                [Symbol.Parse("SOL/USDC")],
            ]);
        var service = BuildService(beta, alpha);
        var source = BuildSource(service);

        // The catalog flips — a kick re-fetch must ride the same emission.
        // Both promises subscribe BEFORE the switch that publishes, so the
        // kick's fast fetch cannot outrun their subscription.
        var (firstPromise, firstSubscription) = NextPublicationPromise(source);
        using var firstGuard = firstSubscription;
        await service.SwitchToAsync("alpha");
        var afterFirstSwitch = await AwaitPublicationAsync(firstPromise);
        // The registry's rows re-composed on the kick.
        RowNames(afterFirstSwitch).Should().Equal(
            "beta:BTC/USDT",
            "alpha:SOL/USDC");

        var (secondPromise, secondSubscription) = NextPublicationPromise(source);
        using var secondGuard = secondSubscription;
        await service.SwitchToAsync("beta");
        var afterSecondSwitch = await AwaitPublicationAsync(secondPromise);
        // The second kick picked the plan's second composition.
        RowNames(afterSecondSwitch).Should().Equal(
            "beta:ETH/BTC",
            "alpha:SOL/USDC");
        beta.SymbolFetches.Should().Be(2, "the two changes kicked exactly two fetches");
        alpha.SymbolFetches.Should().Be(2);
    }

    #endregion The epoch drop

    #region The warm-up

    [Fact]
    public async Task InitializeAsync_ReturnsImmediately_AndTheWarmUpPublishes()
    {
        var service = BuildService(
            new FakeMarketData("beta", "Beta", standingSymbols: ["BTC/USDT"]),
            new FakeMarketData("alpha", "Alpha", standingSymbols: ["SOL/USDC"]));
        var source = BuildSource(service);

        // The warm-up publish may outrun the fact's own line — the promise
        // subscribes BEFORE the initialization kicks the fetch.
        var (warmUpPromise, warmUpSubscription) = NextPublicationPromise(source);
        using var warmUpGuard = warmUpSubscription;

        var initialization = source.InitializeAsync();

        initialization.IsCompleted.Should().BeTrue(
            "the walk must never await the network: the warm-up rides the kick stream");
        var settled = await AwaitPublicationAsync(warmUpPromise);
        settled.Settled.Should().BeTrue();
        // The warm-up cleared the gate and fetched both adapters.
        RowNames(settled).Should().Equal(
            "beta:BTC/USDT",
            "alpha:SOL/USDC");
    }

    [Fact]
    public async Task TheWarmUpSurvivesAFaultingAdapter()
    {
        var service = BuildService(
            new FakeMarketData("beta", "Beta", standingSymbols: ["BTC/USDT"], throwOnFirstFetch: true),
            new FakeMarketData("alpha", "Alpha", standingSymbols: ["SOL/USDC"]));
        var source = BuildSource(service);

        var (warmUpPromise, warmUpSubscription) = NextPublicationPromise(source);
        using var warmUpGuard = warmUpSubscription;

        await source.InitializeAsync(ct: default);
        var settled = await AwaitPublicationAsync(warmUpPromise);

        settled.Settled.Should().BeTrue();
        // The warm-up's kick completes over the healthy rows.
        RowNames(settled).Should().Equal("alpha:SOL/USDC");
    }

    #endregion The warm-up

    #region The recording logger

    /// <summary>
    /// A capturing logger: the quiet-absence pin asserts the level family
    /// (warnings absent, debug present) rather than any exact wording.
    /// </summary>
    private sealed class RecordingLogger : ILogger<SymbolPaletteSource>
    {
        /// <summary>Every logged entry so far, in arrival order.</summary>
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        /// <inheritdoc/>
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        /// <inheritdoc/>
        public bool IsEnabled(LogLevel logLevel) => true;

        /// <inheritdoc/>
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }

    #endregion The recording logger
}
