namespace ZettaTrade.AdapterKit.Tests.Battery;

using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using FluentAssertions;
using Xunit;
using ZettaTrade.AdapterKit.Tests.Capture;
using ZettaTrade.AdapterKit.Tests.Harness;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.Infrastructure.Exchange;

/// <summary>
/// The adapter conformance battery: the standing recipe every adapter must
/// clear before it is an adapter in this repo. Eight legs, each a sentence
/// of the shared <see cref="IMarketDataAdapter"/> contract — identity, the
/// connect flip, the guard rules, the captured universe, the depth contract
/// (with the T-072 resolver as the referee on both sides), and the restore
/// story across a reconnect. The pack's capture drives every leg: it is a
/// complete, honest recipe for one adapter, and the battery behaves exactly
/// as the recipe says — the rejected-not-default contract is asserted where
/// the pack declares it, the clamp contract where the pack declares the
/// clamp, always holding the pack to its own words.
/// A battery run is <c>leg × pack</c>: onboard a new adapter by writing its
/// capture pack plus a battery subclass in <c>Packs/</c>; the legs are
/// already written. See the kit's <c>README.md</c> for the step by step.
/// </summary>
public abstract class AdapterConformanceBattery
{
    #region Constants

    /// <summary>Bounded wait for one adapter event on the wire (connect, subscribe frame, guard error).</summary>
    private static readonly TimeSpan EventWait = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Bounded wait for the restore: the shipped T-105 reconnect policy pays
    /// 5 s initial delay plus up to 25% jitter, and this budget gives that
    /// three headroom, so a scheduled-delay reconnect is never racing the
    /// assertion.
    /// </summary>
    private static readonly TimeSpan RestoreWait = TimeSpan.FromSeconds(20);

    /// <summary>Bounded wait for a canned adapter's cadence tick (the demo cadences run 1–2 s; the budget is double).</summary>
    private static readonly TimeSpan CadenceTickWait = TimeSpan.FromSeconds(5);

    /// <summary>The post-restore settle: a churned adapter piles sessions within a couple of seconds, a healthy pin does not.</summary>
    private static readonly TimeSpan StabilityWait = TimeSpan.FromMilliseconds(1500);

    /// <summary>Time frame the bar guards ride (no bar content is asserted from it — only the guards).</summary>
    private static readonly TimeFrame AnchorTimeFrame = TimeFrame.M1;

    /// <summary>Unix seconds of the bars anchor (an already-on M1 boundary; content is immaterial, only the guards are judged).</summary>
    private const long AnchorUnixSeconds = 1730000040;

    /// <summary>The non-positive bar limit the guard leg probes.</summary>
    private const int BarsLimitProbe = 0;

    /// <summary>The bar request the bars leg shapes (a valid limit; the guard is what is judged from its opposite).</summary>
    private const int BarsRequestLimit = 10;

    /// <summary>How many subscriptions one rejected depth request is handed out to (the per-subscription shape of the rejection).</summary>
    private const int SubscribeRejectionProbes = 2;

    /// <summary>A transcript restore story is the pre-reconnect session plus the restore session.</summary>
    private const int TranscriptSessionCount = 2;

    /// <summary>The parameter every adapter's unknown-symbol guard reports.</summary>
    private const string SymbolParameterName = "symbol";

    /// <summary>The parameter every adapter's bar-limit guard reports.</summary>
    private const string LimitParameterName = "limit";

    #endregion

    #region Constructor

    private readonly AdapterCapturePack _pack;

    /// <summary>Create the battery over one capture pack.</summary>
    /// <param name="pack">The pack this battery subclass stands for.</param>
    protected AdapterConformanceBattery(AdapterCapturePack pack)
    {
        _pack = pack;
    }

    /// <summary>The capture pack this battery instance stands for (the subclass wiring reads its canned routes).</summary>
    protected AdapterCapturePack Pack => _pack;

    #endregion

    #region Tests

    /// <summary>This pack's adapter reports the identity, the display name, the capability flags and the construction-time connectedness exactly as captured.</summary>
    [Fact]
    public async Task AdapterIdentity_MatchesThePackDeclaration()
    {
        await using var session = await OpenSessionAsync(CancellationToken.None);

        session.Adapter.Id.Should().Be(_pack.AdapterId, "the pack stands for one adapter identity");
        session.Adapter.Name.Should().Be(_pack.AdapterName, "the pack stands for one display name");
        session.Adapter.Capabilities.Should().Be(_pack.Capabilities, "the pack captures the exact capability flags");
        session.Adapter.IsConnected.Should().Be(_pack.ConnectsAtConstruction, "the pack captures the construction-time connectedness");
    }

    /// <summary>ConnectAsync flips the adapter into the connected state, and on a transport-backed pack the rig sees the session.</summary>
    [Fact]
    public async Task ConnectAsync_MarksTheAdapterConnected()
    {
        await using var session = await OpenSessionAsync(CancellationToken.None);

        await session.Adapter.ConnectAsync(CancellationToken.None);

        session.Adapter.IsConnected.Should().BeTrue("a connect is a connect, canned or wired");
        if (session.Server is not null)
        {
            await TestPoll.PollUntilAsync(() => session.Server.SessionCount >= 1, EventWait);
            session.Server.SessionCount.Should().Be(1, "one connect, one accepted session");
        }
    }

    /// <summary>
    /// Every guard the pack declares holds: invalid input raises on the face
    /// that owns it and nothing is ever substituted (the standing
    /// never-a-default trading-safety rule).
    /// </summary>
    [Fact]
    public async Task TheDeclaredGuards_RejectInsteadOfDefaulting()
    {
        if (_pack.ClientSideGuards.Contains(AdapterCapturePack.GuardUnknownSymbol) ||
            _pack.ClientSideGuards.Contains(AdapterCapturePack.GuardBarsLimit))
        {
            await using var session = await OpenSessionAsync(CancellationToken.None);
            await AssertDataFaceGuardsAsync(session.Adapter);
        }

        if (_pack.ClientSideGuards.Contains(AdapterCapturePack.GuardBlankEndpoints))
        {
            await RunCtorGuardProbeAsync();
        }
    }

    /// <summary>The captured universe is the exact symbol list the pack says it is, in the order the adapter serves it.</summary>
    [Fact]
    public async Task TheCapturedUniverse_ListsExactlyThePackSymbols()
    {
        await using var session = await OpenSessionAsync(CancellationToken.None);

        var symbols = await session.Adapter.GetSymbolsAsync(CancellationToken.None);

        symbols
            .Select(symbol => symbol.Normalized)
            .Should().Equal(
                _pack.Symbols.Select(symbol => symbol.Normalized),
                "a closed capture owns its order: the pack's list is the served list");
    }

    /// <summary>
    /// The depth profile the pack declares shows on the adapter surface: a
    /// declared served set on an explicit-set adapter, and honestly no set
    /// on a clamped one.
    /// </summary>
    [Fact]
    public async Task TheDepthProfile_MatchesTheAdapterSurface()
    {
        await using var session = await OpenSessionAsync(CancellationToken.None);

        if (_pack.DepthProfile == CaptureDepthProfile.ExplicitSet)
        {
            var baseAdapter = session.Adapter.Should().BeAssignableTo<ExchangeAdapterBase>().Subject;
            baseAdapter.ServedBookDepths.Should().BeEquivalentTo(
                _pack.ServedBookDepths,
                "the pack's served set is the adapter's own declaration");
        }
        else
        {
            session.Adapter.Should().NotBeAssignableTo<ExchangeAdapterBase>(
                "the clamped profile does not carry a served-set surface: the pack is honest about that, too");
        }
    }

    /// <summary>
    /// Outside the served set the request is the pack-declared path: on an
    /// explicit-set adapter the rejection (an error handed to the
    /// subscriber, never a silently served other depth — BUG-032), on a
    /// clamped adapter the deterministic shape of its own capacity.
    /// </summary>
    [Fact]
    public async Task UnsupportedDepths_FollowThePackDeclaredContract()
    {
        await using var session = await OpenSessionAsync(CancellationToken.None);
        var adapter = session.Adapter;
        var symbol = TheConformanceSymbol();

        if (_pack.DepthProfile == CaptureDepthProfile.ExplicitSet)
        {
            var rejectedDepth = _pack.RejectedDepthRequest!.Value;
            var source = adapter.SubscribeOrderBook(symbol, rejectedDepth);
            var failures = new List<Exception>();

            using (source.Subscribe(_ => { }, failures.Add))
            {
                await TestPoll.PollUntilAsync(() => failures.Count >= 1, EventWait);
            }

            // The rejection rides each subscription, not a one-shot state.
            using (source.Subscribe(_ => { }, failures.Add))
            {
                await TestPoll.PollUntilAsync(() => failures.Count >= 2, EventWait);
            }

            failures.Should().HaveCount(SubscribeRejectionProbes)
                .And.OnlyContain(failure => failure is ArgumentOutOfRangeException);
            foreach (var failure in failures)
            {
                failure.Message.Should().Contain(ServedDepthsText(), "the rejection names the served depths")
                    .And.Contain($"requested {rejectedDepth}", "the rejection names the rejected request");
            }

            session.Server!.SnapshotFrames().Should().BeEmpty("a rejected depth request never starts any stream");
        }
        else
        {
            foreach (var probe in _pack.DepthProbeRequests)
            {
                var served = new List<OrderBook>();
                using (adapter.SubscribeOrderBook(symbol, probe).Take(1).Subscribe(served.Add))
                {
                    await TestPoll.PollUntilAsync(() => served.Count >= 1, EventWait);
                }

                served.Should().ContainSingle(
                    $"the clamped profile serves its first snapshot at subscribe time (requested {probe})");
                var expected = ServedLevelCount(probe);
                served[0].Bids.Should().HaveCount(expected, "both sides carry the clamped level count");
                served[0].Asks.Should().HaveCount(expected, "the clamped profile shapes the book to its own capacity");
            }
        }
    }

    /// <summary>
    /// Depth requests resolve exactly through the T-072 resolver: on an
    /// explicit-set adapter the resolved depth is what lands on the wire
    /// (and the raw unresolvable request never rides it), on a clamped pack
    /// the no-declaration profile passes every request through untouched.
    /// </summary>
    [Fact]
    public async Task DepthRequests_ResolveThroughTheT072Resolver()
    {
        await using var session = await OpenSessionAsync(CancellationToken.None);
        var adapter = session.Adapter;
        var symbol = TheConformanceSymbol();
        var server = session.Server;

        if (_pack.DepthProfile == CaptureDepthProfile.ExplicitSet)
        {
            var baseAdapter = (ExchangeAdapterBase)adapter;
            foreach (var probe in _pack.DepthProbeRequests)
            {
                var resolved = ExchangeAdapterBase.ResolveBookDepth(baseAdapter.ServedBookDepths, probe);
                var expectedTopic = ExpectedBookTopic(resolved);

                using var subscription = adapter.SubscribeOrderBook(symbol, resolved).Subscribe(_ => { });
                await server!.WaitForFrameAsync(
                    frame => frame.Contains(_pack.SubscribeMarker, StringComparison.Ordinal) &&
                             frame.Contains(expectedTopic, StringComparison.Ordinal),
                    EventWait);

                if (probe != resolved)
                {
                    server.SnapshotFrames().Should().NotContain(
                        frame => frame.Contains(ExpectedBookTopic(probe), StringComparison.Ordinal),
                        $"the kit serves depth {resolved} for request {probe}: the raw request is never a third depth");
                }

                subscription.Dispose();
            }
        }
        else
        {
            // The clamped profile's resolution rule lives in the resolver itself.
            foreach (var probe in _pack.DepthProbeRequests)
            {
                ExchangeAdapterBase.ResolveBookDepth(_pack.ServedBookDepths, probe)
                    .Should().Be(probe, $"a serve-anything profile passes request {probe} through untouched");
            }
        }
    }

    /// <summary>
    /// The restore story: subscribed feeds survive the pack-declared
    /// reconnect semantics with fresh data per session — the transcript pack
    /// replays its recorded sessions through a real reconnect and joins a
    /// fresh subscriber, the always-on pack survives its own connect cycle
    /// mid-stream. Feeds are exact where the pack captures exact prices; all
    /// feeds move forward with session-distinguishing data, the shared
    /// subject included.
    /// </summary>
    [Fact]
    public async Task SubscribedFeeds_RestoreDataAcrossTheReconnect()
    {
        await using var session = await OpenSessionAsync(CancellationToken.None);
        var adapter = session.Adapter;
        var symbol = TheConformanceSymbol();

        if (_pack.ReconnectMode == CaptureReconnectMode.Transcript)
        {
            await RunTranscriptRestoreAsync(session, symbol);
        }
        else
        {
            await RunAlwaysOnRestoreAsync(adapter, symbol);
        }
    }

    #endregion

    #region Helpers: restore legs

    /// <summary>
    /// The transcript restore: ticker, book and trade feeds take one event
    /// per session across a real reconnect; a late subscriber joins after
    /// the reconnect and rides the shared subject through the restore; the
    /// session count stays pinned (no churn).
    /// </summary>
    /// <param name="session">The open conformance session.</param>
    /// <param name="symbol">The pack's conformance probe symbol.</param>
    private async Task RunTranscriptRestoreAsync(AdapterSession session, Symbol symbol)
    {
        var server = session.Server!;
        var sessions = _pack.TranscriptSessions;
        sessions.Should().HaveCount(TranscriptSessionCount, "the kit restores one session into the next");

        var baseAdapter = (ExchangeAdapterBase)session.Adapter;
        var adapter = session.Adapter;
        var bookDepth = ExchangeAdapterBase.ResolveBookDepth(
            baseAdapter.ServedBookDepths,
            _pack.DepthProbeRequests[^1]);

        var tickerFeed = CollectTickerAsync(adapter, symbol, ticks: 2, RestoreWait);
        var bookFeed = CollectBooksAsync(adapter, symbol, bookDepth, snapshots: 2, RestoreWait);
        var tradeFeed = CollectTradesAsync(adapter, symbol, trades: 2, RestoreWait);

        // The three feeds' starts ride the wire one by one (one subscribe
        // frame each, every one carrying the symbol's topics): all three are
        // seen before the kit replays anything.
        await TestPoll.PollUntilAsync(
            () => ServerCountOfSubscribes(server, bookDepth) >= 3,
            EventWait);

        await server.SendFramesAsync(1, sessions[0].Frames);

        var first = await server.WaitForSessionAsync(1, EventWait);
        await first.DropAsync();
        await TestPoll.PollUntilAsync(() => server.SessionCount >= 2, RestoreWait);

        // The late subscriber joins after the reconnect: its own start is
        // the fifth subscribe frame. Joining BEFORE the restore replay pins
        // the shared-subject claim: the fresh subscriber receives the same
        // rebuilt book the pre-reconnect subscriber receives.
        var lateBooks = new List<OrderBook>();
        using var lateSubscribe = adapter.SubscribeOrderBook(symbol, bookDepth).Subscribe(lateBooks.Add);
        await TestPoll.PollUntilAsync(
            () => ServerCountOfSubscribes(server, bookDepth) >= 5,
            RestoreWait);

        await server.SendFramesAsync(2, sessions[1].Frames);

        var tickers = await tickerFeed;
        var books = await bookFeed;
        var trades = await tradeFeed;

        tickers.Should().HaveCount(2, "one ticker per session proves the topics survive the reconnect");
        tickers[0].LastPrice.Should().Be(sessions[0].TickerPrice, "the first session's captured price flows");
        tickers[1].LastPrice.Should().Be(sessions[1].TickerPrice, "the restored session's captured price flows");
        tickers.Should().OnlyContain(ticker => ticker.Symbol == symbol);

        books.Should().HaveCount(2, "the book rebuilds once per session from each session's snapshot");
        books[1].Bids[0].Price.Should().NotBe(books[0].Bids[0].Price, "the restored session serves session-distinguishing data");
        lateBooks.Should().ContainSingle(
            book => book.Bids[0].Price == books[1].Bids[0].Price,
            "the fresh subscriber rides the same shared subject the restore publishes into");
        foreach (var book in books)
        {
            book.Symbol.Should().Be(symbol);
            book.Bids[0].Price.Should().BeLessThan(book.Asks[0].Price, "a rebuilt book stays coherent");
        }

        trades.Should().HaveCount(2, "one trade per session keeps the public trade topic alive through the restore");
        trades.Should().OnlyContain(trade => trade.Symbol == symbol);
        trades[0].Id.Should().NotBe(trades[1].Id, "the restored session's trade is its own captured event");

        await Task.Delay(StabilityWait);
        server.SessionCount.Should().Be(TranscriptSessionCount, "a healthy adapter stays pinned; a churned one grows sessions");
    }

    /// <summary>
    /// The always-on restore: ticker, book and trade feeds take their
    /// snapshot and their cadence tick while the adapter cycles its own
    /// disconnect/disconnect — nothing dies, because the surface never
    /// drops.
    /// </summary>
    /// <param name="adapter">The adapter under judgment.</param>
    /// <param name="symbol">The pack's conformance probe symbol.</param>
    private async Task RunAlwaysOnRestoreAsync(IMarketDataAdapter adapter, Symbol symbol)
    {
        var bookDepth = _pack.DepthProbeRequests[^1];
        var tickerFeed = CollectTickerAsync(adapter, symbol, ticks: 2, CadenceTickWait);
        var bookFeed = CollectBooksAsync(adapter, symbol, bookDepth, snapshots: 2, CadenceTickWait);
        var tradeFeed = CollectTradesAsync(adapter, symbol, trades: 2, CadenceTickWait);

        // The always-on pack's connect cycle: the streams survive it, so the
        // second cadence tick still lands after both calls.
        await adapter.DisconnectAsync(CancellationToken.None);
        await adapter.ConnectAsync(CancellationToken.None);

        var tickers = await tickerFeed;
        var books = await bookFeed;
        var trades = await tradeFeed;

        tickers.Should().HaveCount(2, "the first snapshot at subscribe time, then the cadence tick across the cycle");
        books.Should().HaveCount(2, "the book cadence rides the cycle the same way");
        trades.Should().HaveCount(2, "the trade cadence rides the cycle the same way");
        books[1].Bids.Should().HaveCount(ServedLevelCount(bookDepth), "the cadence book carries the clamped shape");
        trades[0].Id.Should().NotBe(trades[1].Id, "distinct cadence events across the cycle");
    }

    #endregion

    #region Helpers: guards

    /// <summary>The data-face guards (unknown symbols, bars limit) run against a live session adapter.</summary>
    /// <param name="adapter">The adapter under judgment.</param>
    private async Task AssertDataFaceGuardsAsync(IMarketDataAdapter adapter)
    {
        var stranger = _pack.StrangerSymbol;

        if (_pack.ClientSideGuards.Contains(AdapterCapturePack.GuardUnknownSymbol))
        {
            FluentActions.Invoking(() => adapter.SubscribeTicker(stranger))
                .Should().ThrowExactly<ArgumentException>().WithParameterName(SymbolParameterName);
            FluentActions.Invoking(() => adapter.SubscribeOrderBook(stranger))
                .Should().ThrowExactly<ArgumentException>().WithParameterName(SymbolParameterName);
            FluentActions.Invoking(() => adapter.SubscribeTrades(stranger))
                .Should().ThrowExactly<ArgumentException>().WithParameterName(SymbolParameterName);

            (await FluentActions.Awaiting(() =>
                    adapter.GetBarsAsync(
                        stranger,
                        AnchorTimeFrame,
                        Anchor(),
                        null,
                        BarsRequestLimit,
                        CancellationToken.None))
                .Should().ThrowAsync<ArgumentException>())
                .WithParameterName(SymbolParameterName);
        }

        if (_pack.ClientSideGuards.Contains(AdapterCapturePack.GuardBarsLimit))
        {
            (await FluentActions.Awaiting(() =>
                    adapter.GetBarsAsync(
                        TheConformanceSymbol(),
                        AnchorTimeFrame,
                        Anchor(),
                        null,
                        BarsLimitProbe,
                        CancellationToken.None))
                .Should().ThrowAsync<ArgumentOutOfRangeException>())
                .WithParameterName(LimitParameterName);
        }
    }

    /// <summary>
    /// The constructor-guard probe for packs declaring the blank-endpoints
    /// guard: the subclass builds an adapter instance the way its stack does,
    /// against blank endpoint configuration, and the guard's own verdict — a
    /// throw — is the pass. The task shape keeps a wrongly-passing probe
    /// disposable (the leaked instance is closed even then). The default is a
    /// wiring error, never a vacuously green leg.
    /// </summary>
    protected virtual Task RunCtorGuardProbeAsync() =>
        Task.FromException(new InvalidOperationException(
            "the pack declares the blank-endpoints guard; its battery subclass must provide the constructor probe"));

    #endregion

    #region Helpers: fixture

    /// <summary>Open one fresh conformance session: the adapter under test plus whichever rigs this pack rides.</summary>
    /// <param name="ct">The opening cancellation token.</param>
    protected abstract ValueTask<AdapterSession> OpenSessionAsync(CancellationToken ct);

    /// <summary>The pack's own conformance probe symbol: the first captured symbol is the battery's subject, per the recipe.</summary>
    private Symbol TheConformanceSymbol() => _pack.Symbols[0];

    /// <summary>The count a clamped pack answers a depth request with, computed from the pack's own clamp anchor.</summary>
    /// <param name="requestedDepth">The raw depth request.</param>
    private int ServedLevelCount(int requestedDepth) =>
        Math.Min(requestedDepth, _pack.ClampedServedDepth!.Value);

    /// <summary>How many captured subscribe frames carry the pack's marker and this leg's book topic.</summary>
    /// <param name="server">The rig capturing the frames.</param>
    /// <param name="bookDepth">The depth whose resolved topic the frames must carry.</param>
    private int ServerCountOfSubscribes(KitLoopbackServer server, int bookDepth)
    {
        var expectedTopic = ExpectedBookTopic(bookDepth);
        return server.SnapshotFrames()
            .Count(frame =>
                frame.Contains(_pack.SubscribeMarker, StringComparison.Ordinal) &&
                frame.Contains(expectedTopic, StringComparison.Ordinal));
    }

    /// <summary>The resolved-depth book topic text for this pack's dialect.</summary>
    /// <param name="depth">The served depth the topic carries.</param>
    private string ExpectedBookTopic(int depth) =>
        $"{_pack.BookTopicPrefix}{depth}.{TheConformanceSymbol().GetExchangeFormat(_pack.AdapterId)}";

    /// <summary>The pack's served set text exactly as the adapter's own rejection strings it.</summary>
    private string ServedDepthsText() => string.Join(", ", _pack.ServedBookDepths!.OrderBy(depth => depth));

    /// <summary>Bar-guard anchor (an already-on M1 boundary; the anchor content is immaterial to the guards).</summary>
    private static DateTimeOffset Anchor() => DateTimeOffset.FromUnixTimeSeconds(AnchorUnixSeconds);

    private static Task<IList<Ticker>> CollectTickerAsync(
        IMarketDataAdapter adapter,
        Symbol symbol,
        int ticks,
        TimeSpan wait) =>
        adapter.SubscribeTicker(symbol)
            .Take(ticks)
            .ToList()
            .ToTask(TaskCreationOptions.RunContinuationsAsynchronously)
            .WaitAsync(wait);

    private static Task<IList<OrderBook>> CollectBooksAsync(
        IMarketDataAdapter adapter,
        Symbol symbol,
        int bookDepth,
        int snapshots,
        TimeSpan wait) =>
        adapter.SubscribeOrderBook(symbol, bookDepth)
            .Take(snapshots)
            .ToList()
            .ToTask(TaskCreationOptions.RunContinuationsAsynchronously)
            .WaitAsync(wait);

    private static Task<IList<MarketTrade>> CollectTradesAsync(
        IMarketDataAdapter adapter,
        Symbol symbol,
        int trades,
        TimeSpan wait) =>
        adapter.SubscribeTrades(symbol)
            .Take(trades)
            .ToList()
            .ToTask(TaskCreationOptions.RunContinuationsAsynchronously)
            .WaitAsync(wait);

    #endregion
}
