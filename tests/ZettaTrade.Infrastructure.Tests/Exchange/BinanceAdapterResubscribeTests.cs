namespace ZettaTrade.Infrastructure.Tests.Exchange;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.Infrastructure.Exchange;

/// <summary>
/// The Binance reconnect re-subscribe pipeline: after every socket drop the
/// tracked symbol set is re-subscribed through one FIFO queue served by a
/// single consumer, each send riding to completion on the adapter's owned
/// lifetime (BUG-004) — an in-flight send is never cancelled by a newer
/// request (a cancelled send aborts the whole socket). The public
/// subscribe/unsubscribe/start API shares the same send gate, so overlapping
/// calls can never race two <c>ClientWebSocket.SendAsync</c> calls (BUG-028).
/// Exercised over the in-process loopback Kestrel server; the reconnect delay
/// stays with the production 5 s inside the adapter, so every scenario that
/// drops the socket takes that long between sessions.
///
/// Wait budgets (T-116, 2026-11-07): <c>EventWait</c> is the 120 s house
/// completion budget shared by the whole WS E2E family, raised from 10 s.
/// The old cap tripped under full-solution parallel test load (xUnit hosts
/// across sibling assemblies chewing cores): a scheduler stall starved the
/// loopback handshake and frame beats past the cap and the collector died as
/// a bare System.TimeoutException inside WaitAsync(EventWait) while every
/// focused run stayed green — the adapter pipeline was healthy all along.
/// This is load patience, never a product timing claim: solo runs always
/// passed and still must, and deliberate latency ceilings (where any ever
/// appear) stay separate from this budget. The 2 s ConnectionStabilityWait
/// bake stays as-is: it is an intentional observation window after
/// settlement, not a bounded completion wait.
/// </summary>
public sealed class BinanceAdapterResubscribeTests
{
    #region Constants

    /// <summary>
    /// The 120 s house load budget (T-116, see the class header): bounded wait
    /// for any expected event or connection — completion patience, never a
    /// latency ceiling.
    /// </summary>
    private static readonly TimeSpan EventWait = TimeSpan.FromSeconds(120);

    /// <summary>
    /// The bake after settlement: a churned adapter piles up further sessions
    /// every few seconds — this window catches that first pile-up, while a
    /// healthy adapter stays pinned on its sessions.
    /// </summary>
    private static readonly TimeSpan ConnectionStabilityWait = TimeSpan.FromSeconds(2);

    /// <summary>The exchange identity the adapter reports.</summary>
    private const string BinanceId = "binance";

    /// <summary>An explicit non-default depth a book subscription carries through the reconnect.</summary>
    private const int TenBookDepth = 10;

    private static readonly Symbol BtcUsdt = new("btc", "usdt", AssetClass.Crypto);
    private static readonly Symbol EthUsdt = new("eth", "usdt", AssetClass.Crypto);
    private static readonly Symbol SolUsdt = new("sol", "usdt", AssetClass.Crypto);

    #endregion

    #region Tests: reconnect resubscribes

    /// <summary>Three tracked symbols surviving a socket drop: every re-subscribe completes exactly once.</summary>
    [Fact]
    public async Task A_three_symbol_reconnect_resubscribes_every_symbol_to_completion()
    {
        await RunReconnectScenarioAsync(BtcUsdt, EthUsdt, SolUsdt);
    }

    /// <summary>The two-symbol shape of the pre-fix silence (no re-subscribe at all before the fix).</summary>
    [Fact]
    public async Task A_two_symbol_reconnect_resubscribes_every_symbol_and_stays_on_one_socket()
    {
        await RunReconnectScenarioAsync(BtcUsdt, EthUsdt);
    }

    #endregion

    #region Tests: depth wire contract and send gate

    /// <summary>A supported depth is requested exactly on the wire (the depth contract's stream names).</summary>
    [Fact]
    public async Task A_supported_depth_is_honored_in_the_subscribed_streams()
    {
        await using var stack = await ResubscribeStack.StartAsync();
        var server = stack.Server;

        using var subscription = stack.Adapter
            .SubscribeOrderBook(BtcUsdt, 5)
            .Subscribe(_ => { });

        _ = await server.WaitForConnectionAsync(1, EventWait);
        var subscribeFrame = await server.WaitForFrameAsync(
            f => f.Contains("\"method\":\"SUBSCRIBE\"", StringComparison.Ordinal),
            EventWait);

        AssertRequestFrame(subscribeFrame);
        StreamParamsOf(subscribeFrame)
            .Should()
            .Equal(
                "btcusdt@ticker",
                "btcusdt@depth5@100ms",
                "btcusdt@trade");
        subscription.Dispose();
    }

    /// <summary>
    /// Overlapping public subscribe/unsubscribe/start serialize behind the one
    /// send gate: three racing calls produce three whole, well-formed wire
    /// requests (never the socket's "one outstanding send" InvalidOperationException,
    /// BUG-028), the untracked symbol is dropped from the reconnect set, and
    /// the survivor's explicit depth rides the re-subscribe.
    /// </summary>
    [Fact]
    public async Task Overlapping_public_calls_serialize_and_the_reconnect_set_tracks_them()
    {
        await using var stack = await ResubscribeStack.StartAsync();
        var server = stack.Server;
        var adapter = stack.Adapter;

        await adapter.ConnectAsync();
        var first = await server.WaitForConnectionAsync(1, EventWait);

        // Three racing sends: a public subscribe, a public unsubscribe and a
        // book-triggered stream start. None connects (the socket is open).
        var subscribeEth = adapter.SubscribeSymbolAsync(EthUsdt);
        var unsubscribeBtc = adapter.UnsubscribeSymbolAsync(BtcUsdt);
        var solBooksTask = adapter
            .SubscribeOrderBook(SolUsdt, TenBookDepth)
            .Take(1)
            .ToList()
            .ToTask(TaskCreationOptions.RunContinuationsAsynchronously)
            .WaitAsync(EventWait);

        // Both public calls completing IS the serialization assertion: an
        // overlapped send would abort with the socket's "one outstanding
        // send" InvalidOperationException (BUG-028).
        Func<Task> act = () => Task.WhenAll(subscribeEth, unsubscribeBtc);
        await act.Should().NotThrowAsync();

        await first.SendTextAsync(DepthFrame(SolUsdt, TenBookDepth, 1, "49999.5", "12.5", "50000.5", "7.5"));
        var solBooks = await solBooksTask;
        solBooks[0].Bids.Should().Equal(new[] { new PriceLevel(49999.5m, 12.5m) });
        solBooks[0].Asks.Should().Equal(new[] { new PriceLevel(50000.5m, 7.5m) });

        // Exactly three whole requests reached the wire: two SUBSCRIBE and
        // one UNSUBSCRIBE, each parsing cleanly (interleaved writes corrupt
        // JSON — the parse is the interleaving detector).
        var frames = server.SnapshotFrames();
        frames.Count(f => f.Contains("\"method\":\"SUBSCRIBE\"", StringComparison.Ordinal))
            .Should().Be(2);
        frames.Count(f => f.Contains("\"method\":\"UNSUBSCRIBE\"", StringComparison.Ordinal))
            .Should().Be(1);
        StreamParamsOf(frames.Single(f => f.Contains("\"method\":\"UNSUBSCRIBE\"", StringComparison.Ordinal)))
            .Should().Equal(
                "btcusdt@ticker",
                "btcusdt@depth20@100ms",
                "btcusdt@trade");
        server.ConnectionCount.Should().Be(1);

        // The socket drop: only the tracked set (ETH, SOL) re-subscribes —
        // BTC was unsubscribed and must stay silent forever.
        await first.CloseGracefullyAsync();
        await TestPoll.PollUntilAsync(() => server.ConnectionCount >= 2, EventWait);
        await TestPoll.PollUntilAsync(
            () => server.SnapshotFrames().Count(f => IsSubscribeFrameFor(f, EthUsdt, DepthLevelCount.Default)) >= 2
                  && server.SnapshotFrames().Count(f => IsSubscribeFrameFor(f, SolUsdt, TenBookDepth)) >= 2,
            EventWait);
        _ = await server.WaitForConnectionAsync(2, EventWait);
        await Task.Delay(ConnectionStabilityWait);

        var postFrames = server.SnapshotFrames();
        postFrames.Count(f => IsSubscribeFrameFor(f, EthUsdt, DepthLevelCount.Default))
            .Should().Be(2, "the initial subscribe plus one reconnect re-subscribe");
        postFrames.Count(f => IsSubscribeFrameFor(f, SolUsdt, TenBookDepth))
            .Should().Be(2, "the explicit depth survives the reconnect's re-subscribe");
        postFrames.Count(f => IsSubscribeFrameFor(f, BtcUsdt, DepthLevelCount.Default))
            .Should().Be(0, "the symbol was never subscribed here and the reconnect set must not resurrect it");
        server.ConnectionCount.Should().Be(2, "a healthy adapter stays; a churned one grows sessions");

        AssertPumpCompletedCleanly(stack.Logger);
    }

    #endregion

    #region Helpers: scenario

    /// <summary>
    /// The reconnect scenario body: subscribe every symbol's ticker feed,
    /// deliver its session-1 price, drop the socket, and prove on the SECOND
    /// session that every tracked symbol's streams were re-subscribed to
    /// completion — exactly one re-subscribe per symbol, one connection only,
    /// zero failed sends (nothing logs a re-subscribe failure), and the data
    /// path surviving the reconnect on every feed.
    /// </summary>
    private async Task RunReconnectScenarioAsync(params Symbol[] symbols)
    {
        await using var stack = await ResubscribeStack.StartAsync();
        var server = stack.Server;

        // Each feed collects exactly one ticker per session: the session-1
        // price, then the session-2 price — the data path survives the
        // reconnect only if the streams were re-subscribed.
        var feedTasks = symbols
            .Select(symbol => stack.Adapter
                .SubscribeTicker(symbol)
                .Take(2)
                .ToList()
                .ToTask(TaskCreationOptions.RunContinuationsAsynchronously)
                .WaitAsync(EventWait))
            .ToList();

        var first = await server.WaitForConnectionAsync(1, EventWait);
        foreach (var symbol in symbols)
        {
            await server.WaitForFrameAsync(f => IsSubscribeFrameFor(f, symbol, DepthLevelCount.Default), EventWait);
        }

        for (var i = 0; i < symbols.Length; i++)
        {
            await first.SendTextAsync(TickerFrame(symbols[i], SessionLastPrice(i, 1), SessionChangePercent(1), SessionEventTime(i, 1)));
        }

        // The server drops the session; the client reconnects on its own.
        await first.CloseGracefullyAsync();
        await TestPoll.PollUntilAsync(() => server.ConnectionCount >= 2, EventWait);

        // The reconnect burst re-subscribes EVERY tracked symbol: one
        // additional subscribe stream per symbol lands on the new session.
        await TestPoll.PollUntilAsync(
            () => symbols.All(symbol =>
                server.SnapshotFrames().Count(f => IsSubscribeFrameFor(f, symbol, DepthLevelCount.Default)) >= 2),
            EventWait);

        var second = await server.WaitForConnectionAsync(2, EventWait);
        for (var i = 0; i < symbols.Length; i++)
        {
            await second.SendTextAsync(TickerFrame(symbols[i], SessionLastPrice(i, 2), SessionChangePercent(2), SessionEventTime(i, 2)));
        }

        var feeds = await Task.WhenAll(feedTasks);
        for (var i = 0; i < symbols.Length; i++)
        {
            feeds[i].Should().HaveCount(2, "one ticker per session proves the stream survived the reconnect");
            feeds[i][0].LastPrice.Should().Be(SessionLastPrice(i, 1));
            feeds[i][1].LastPrice.Should().Be(SessionLastPrice(i, 2));
        }

        // No churn: exactly one re-subscribe per symbol (plus its original
        // subscription), and the connection count stays pinned on two.
        await Task.Delay(ConnectionStabilityWait);
        server.ConnectionCount.Should().Be(2, "a healthy adapter stays; a churned one grows sessions");
        foreach (var symbol in symbols)
        {
            server.SnapshotFrames()
                .Count(f => IsSubscribeFrameFor(f, symbol, DepthLevelCount.Default))
                .Should().Be(2);
        }

        AssertPumpCompletedCleanly(stack.Logger);
    }

    /// <summary>
    /// The honest pump completed: no Error entries at all (a disconnect path
    /// that crashes a background loop surfaces exactly there) and no
    /// "Binance stream re-subscribe ... failed" entries (a send cancelled
    /// mid-flight surfaces exactly there).
    /// </summary>
    private static void AssertPumpCompletedCleanly(RecordingLogger logger) =>
        logger.Entries.Should().NotContain(
            entry => entry.Level == LogLevel.Error,
            "the reconnect must not surface as a crashed background loop")
            .And.NotContain(
            entry => entry.Message.StartsWith("Binance stream re-subscribe for", StringComparison.Ordinal),
            "no in-flight re-subscribe send was cancelled");

    #endregion

    #region Helpers: frames

    /// <summary>Whether the frame is a SUBSCRIBE request for this symbol's stream set.</summary>
    private static bool IsSubscribeFrameFor(string frame, Symbol symbol, int bookDepth)
    {
        var name = symbol.GetExchangeFormat(BinanceId).ToLowerInvariant();
        return frame.Contains("\"method\":\"SUBSCRIBE\"", StringComparison.Ordinal)
               && frame.Contains("\"" + name + "@ticker\"", StringComparison.Ordinal)
               && frame.Contains("\"" + name + "@depth" + bookDepth.ToString(CultureInfo.InvariantCulture) + "@100ms\"", StringComparison.Ordinal);
    }

    /// <summary>The "params" string array of a SUBSCRIBE/UNSUBSCRIBE request frame.</summary>
    private static IReadOnlyList<string> StreamParamsOf(string frame)
    {
        using var json = JsonDocument.Parse(frame);
        return json.RootElement
            .GetProperty("params")
            .EnumerateArray()
            .Select(item => item.GetString()!)
            .ToList();
    }

    /// <summary>
    /// Pin one whole request frame: a known method, a positive id and exactly
    /// three distinct stream params (interleaved sends corrupt this shape).
    /// </summary>
    private static void AssertRequestFrame(string frame)
    {
        using var json = JsonDocument.Parse(frame);
        var root = json.RootElement;

        var method = root.GetProperty("method").GetString();
        method.Should().BeOneOf("SUBSCRIBE", "UNSUBSCRIBE");
        root.GetProperty("id").GetInt32().Should().BePositive();

        var streamParams = root.GetProperty("params")
            .EnumerateArray()
            .Select(item => item.GetString()!)
            .ToList();
        streamParams.Should().HaveCount(3);
        streamParams.Should().OnlyHaveUniqueItems();
    }

    /// <summary>A session ticker frame (combined envelope, number strings).</summary>
    private static string TickerFrame(Symbol symbol, decimal lastPrice, decimal changePercent, long eventTime)
    {
        var name = symbol.GetExchangeFormat(BinanceId).ToLowerInvariant();
        return "{\"stream\":\"" + name + "@ticker\",\"data\":{\"E\":" + eventTime +
               ",\"c\":\"" + Invariant(lastPrice) +
               "\",\"b\":\"" + Invariant(lastPrice - 1m) +
               "\",\"a\":\"" + Invariant(lastPrice + 1m) +
               "\",\"v\":\"123.456\",\"P\":\"" + Invariant(changePercent) + "\"}}";
    }

    /// <summary>A partial book depth snapshot frame at the given depth stream.</summary>
    private static string DepthFrame(
        Symbol symbol,
        int bookDepth,
        long lastUpdateId,
        string bidPrice,
        string bidQuantity,
        string askPrice,
        string askQuantity)
    {
        var name = symbol.GetExchangeFormat(BinanceId).ToLowerInvariant();
        var id = lastUpdateId.ToString(CultureInfo.InvariantCulture);
        return "{\"stream\":\"" + name + "@depth" + bookDepth.ToString(CultureInfo.InvariantCulture) +
               "@100ms\",\"data\":{\"lastUpdateId\":" + id +
               ",\"bids\":[[\"" + bidPrice + "\",\"" + bidQuantity + "\"]]" +
               ",\"asks\":[[\"" + askPrice + "\",\"" + askQuantity + "\"]]}}";
    }

    private static string Invariant(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The session-distinguishing last price: per-index spaced and +1 on the
    /// second session, so a feed mixing sessions is caught by the prices.
    /// </summary>
    private static decimal SessionLastPrice(int index, int session) =>
        50000.5m + (index * 10m) + (session - 1m);

    private static decimal SessionChangePercent(int session) => session == 1 ? 2.5m : 3.5m;

    private static long SessionEventTime(int index, int session) =>
        1_900_000_001_000L + ((session - 1L) * 1_000L) + index;

    /// <summary>The depth level names used by the scenarios' default (pinned) depth.</summary>
    private static class DepthLevelCount
    {
        /// <summary>The Binance adapter's pinned default number of levels.</summary>
        public const int Default = 20;
    }

    #endregion

    #region Helpers: stack

    /// <summary>
    /// The recording logger: collects level + message pairs, so the tests can
    /// pin the honest no-cancellation property on the pump's own logs.
    /// </summary>
    private sealed class RecordingLogger : ILogger<BinanceAdapter>
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

    /// <summary>
    /// The adapter, its recording logger, the service provider and the
    /// loopback server disposed in dependency order (adapter first, host last).
    /// </summary>
    private sealed class ResubscribeStack : IAsyncDisposable
    {
        #region Fields

        private readonly ServiceProvider _provider;

        #endregion

        #region Constructor / Properties

        private ResubscribeStack(
            LoopbackWebSocketServer server,
            RecordingLogger logger,
            BinanceAdapter adapter,
            ServiceProvider provider)
        {
            Server = server;
            Logger = logger;
            Adapter = adapter;
            _provider = provider;
        }

        public LoopbackWebSocketServer Server { get; }

        public RecordingLogger Logger { get; }

        public BinanceAdapter Adapter { get; }

        #endregion

        #region Methods: Factory

        public static async Task<ResubscribeStack> StartAsync()
        {
            var server = await LoopbackWebSocketServer.StartAsync();

            var services = new ServiceCollection();
            services.AddHttpClient(BinanceAdapter.HttpClientName);
            var provider = services.BuildServiceProvider();

            var logger = new RecordingLogger();
            var adapter = new BinanceAdapter(
                logger,
                provider.GetRequiredService<IHttpClientFactory>(),
                new BinanceEndpoints(
                    RestBaseUrl: $"http://127.0.0.1:{server.Port}",
                    WsUrl: server.CombinedStreamUrl));

            return new ResubscribeStack(server, logger, adapter, provider);
        }

        #endregion

        #region IAsyncDisposable

        public async ValueTask DisposeAsync()
        {
            await Adapter.DisposeAsync();
            await Server.DisposeAsync();
            _provider.Dispose();
        }

        #endregion
    }

    #endregion
}
