namespace ZettaTrade.Infrastructure.Tests.Exchange;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using Core.Entities;
using Core.Enums;
using Core.ValueObjects;
using FluentAssertions;
using Infrastructure.Exchange;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

/// <summary>
/// The resubscribe pipeline's serialization (BUG-003): reconnect bursts and
/// gap frames all ride ONE FIFO queue served by a single consumer, so with 2
/// or 3 tracked symbols every re-subscribe send runs to completion — an
/// in-flight send is never cancelled by a newer request (a cancelled send
/// aborts the whole socket and churns the reconnect with no market data
/// until a restart). Exercised over the in-process loopback Kestrel server.
/// </summary>
public sealed class BybitAdapterResubscribeTests
{
    #region Constants

    /// <summary>Bounded wait for any expected event or connection.</summary>
    private static readonly TimeSpan EventWait = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The bake after settlement: a reconnect churn (the pre-fix behavior)
    /// piles up further sessions every few seconds — this window catches that
    /// first pile-up, while a healthy adapter stays pinned on its sessions.
    /// </summary>
    private static readonly TimeSpan ConnectionStabilityWait = TimeSpan.FromSeconds(2);

    /// <summary>The order book depth the adapter subscribes (its spot depth topics: 1 and 50 — the depth contract is explicit since BUG-032).</summary>
    private const int DepthLevelCount = 50;

    /// <summary>The exchange identity the adapter reports.</summary>
    private const string BybitId = "bybit";

    private static readonly Symbol BtcUsdt = new("btc", "usdt", AssetClass.Crypto);
    private static readonly Symbol EthUsdt = new("eth", "usdt", AssetClass.Crypto);
    private static readonly Symbol SolUsdt = new("sol", "usdt", AssetClass.Crypto);

    #endregion

    #region Tests: reconnect resubscribes

    /// <summary>Three tracked symbols surviving a socket drop: every re-subscribe rides the wire to completion.</summary>
    [Fact]
    public async Task A_three_symbol_reconnect_resubscribes_every_symbol_to_completion()
    {
        await RunReconnectScenarioAsync(BtcUsdt, EthUsdt, SolUsdt);
    }

    /// <summary>The two-symbol shape of the pre-fix churn: the first symbol's send used to be cancelled.</summary>
    [Fact]
    public async Task A_two_symbol_reconnect_resubscribes_every_symbol_and_stays_on_one_socket()
    {
        await RunReconnectScenarioAsync(BtcUsdt, EthUsdt);
    }

    #endregion

    #region Tests: gap during resubscribe

    /// <summary>
    /// A gap frame right after a reconnect rides the SAME queue as the burst:
    /// the gap's resubscribe and the burst resubscribes all complete to the
    /// exact frame counts — none is cancelled mid-send (the two gap deltas
    /// land back to back, the second while the first's send is in flight),
    /// and both books rebuild from the fresh snapshots.
    /// </summary>
    [Fact]
    public async Task A_gap_frame_into_a_reconnect_burst_serializes_beside_it()
    {
        await using var stack = await ResubscribeStack.StartAsync();
        var server = stack.Server;

        var btcBooksTask = stack.Adapter
            .SubscribeOrderBook(BtcUsdt, DepthLevelCount)
            .Take(4)
            .ToList()
            .ToTask(TaskCreationOptions.RunContinuationsAsynchronously)
            .WaitAsync(EventWait);
        var ethBooksTask = stack.Adapter
            .SubscribeOrderBook(EthUsdt, DepthLevelCount)
            .Take(2)
            .ToList()
            .ToTask(TaskCreationOptions.RunContinuationsAsynchronously)
            .WaitAsync(EventWait);

        var first = await server.WaitForConnectionAsync(1, EventWait);
        await server.WaitForFrameAsync(f => IsSubscribeFrameFor(f, BtcUsdt), EventWait);
        await server.WaitForFrameAsync(f => IsSubscribeFrameFor(f, EthUsdt), EventWait);

        // The books are established on the first session.
        await first.SendTextAsync(BookFrame(BtcUsdt, "snapshot", 100, Level("49999", "12.5"), Level("50000", "2.5")));
        await first.SendTextAsync(BookFrame(EthUsdt, "snapshot", 100, Level("3999", "5"), Level("4001", "5")));

        // The socket drop: the reconnect burst re-subscribes both symbols.
        await first.CloseGracefullyAsync();
        await TestPoll.PollUntilAsync(() => server.ConnectionCount >= 2, EventWait);
        await TestPoll.PollUntilAsync(
            () => server.SnapshotFrames().Count(f => IsSubscribeFrameFor(f, BtcUsdt)) >= 2
                  && server.SnapshotFrames().Count(f => IsSubscribeFrameFor(f, EthUsdt)) >= 2,
            EventWait);

        var second = await server.WaitForConnectionAsync(2, EventWait);

        // On the LIVE second session the BTC book machine is refreshed, an
        // in-sequence delta pushes the sequence to 901, then TWO gap deltas
        // land back to back: the first discards the state and queues its
        // resubscribe; the second is detected while that send may still be in
        // flight and must queue beside it — never cancel it (the pre-fix
        // pipeline's cancellation aborted the whole socket here).
        await second.SendTextAsync(BookFrame(BtcUsdt, "snapshot", 900, Level("48999", "4"), Level("50000", "3")));
        await second.SendTextAsync(BookFrame(BtcUsdt, "delta", 901, Level("48999.5", "1"), "[]"));
        await second.SendTextAsync(BookFrame(BtcUsdt, "delta", 903, Level("49999", "1"), "[]"));
        await second.SendTextAsync(BookFrame(BtcUsdt, "delta", 905, Level("49999", "1"), "[]"));

        // Exactly the expected resubscribes reach the wire: BTC initial(1) +
        // burst(1) + two gap recoveries(2) = 4; ETH initial + burst = 2. A
        // cancelled in-flight send aborts the socket: the counts drift AND the
        // connection count grows (a churned session restarts the resubscribes).
        await TestPoll.PollUntilAsync(
            () => server.SnapshotFrames().Count(f => IsSubscribeFrameFor(f, BtcUsdt)) >= 4,
            EventWait);
        await Task.Delay(ConnectionStabilityWait);
        server.ConnectionCount.Should().Be(2, "the gap recovery must not churn the socket either");
        var frames = server.SnapshotFrames().ToList();
        frames.Count(f => IsSubscribeFrameFor(f, BtcUsdt)).Should().Be(4);
        frames.Count(f => IsSubscribeFrameFor(f, EthUsdt)).Should().Be(2);

        // The recovery: fresh snapshots rebuild both books.
        await second.SendTextAsync(BookFrame(BtcUsdt, "snapshot", 1000, Level("48888", "2"), Level("50001", "1")));
        await second.SendTextAsync(BookFrame(EthUsdt, "snapshot", 999, Level("3955", "3"), Level("4008", "2")));

        var btcBooks = await btcBooksTask;
        var ethBooks = await ethBooksTask;

        btcBooks[0].Bids.Should().Equal(new[] { new PriceLevel(49999m, 12.5m) });
        btcBooks[0].Asks.Should().Equal(new[] { new PriceLevel(50000m, 2.5m) });
        btcBooks[1].Bids.Should().Equal(
            new[] { new PriceLevel(48999m, 4m) },
            "the refreshed snapshot rebuilds the book on the new session");
        btcBooks[1].Asks.Should().Equal(new[] { new PriceLevel(50000m, 3m) });
        btcBooks[2].Bids.Should().Equal(new[]
        {
            new PriceLevel(48999.5m, 1m),
            new PriceLevel(48999m, 4m),
        });
        btcBooks[3].Bids.Should().Equal(
            new[] { new PriceLevel(48888m, 2m) },
            "the gap recovery rebuilds the book from the recovery snapshot");
        btcBooks[3].Asks.Should().Equal(new[] { new PriceLevel(50001m, 1m) });

        ethBooks[0].Bids.Should().Equal(new[] { new PriceLevel(3999m, 5m) });
        ethBooks[1].Bids.Should().Equal(
            new[] { new PriceLevel(3955m, 3m) },
            "the reconnect resubscribed the ETH topic too");
        ethBooks[1].Asks.Should().Equal(new[] { new PriceLevel(4008m, 2m) });

        // The honest pump cancelled nothing: zero re-subscribe failures logged.
        stack.Logger.Entries.Should().NotContain(
            entry => entry.Message.StartsWith("Bybit topic re-subscribe", StringComparison.Ordinal),
            "an in-flight resubscribe send was never cancelled");
    }

    #endregion

    #region Helpers: scenario

    /// <summary>
    /// The reconnect scenario body: subscribe every symbol's ticker feed,
    /// deliver its session-1 price, drop the socket, and prove on the SECOND
    /// session that every tracked symbol's topics were re-subscribed to
    /// completion — exactly one resubscribe frame per symbol, one connection
    /// only, zero cancelled sends (nothing logs a re-subscribe failure).
    /// </summary>
    private async Task RunReconnectScenarioAsync(params Symbol[] symbols)
    {
        await using var stack = await ResubscribeStack.StartAsync();
        var server = stack.Server;

        // Each feed collects exactly one ticker per session: the session-1
        // price, then the session-2 price — the data path survives the
        // reconnect only if the topics were re-subscribed.
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
            await server.WaitForFrameAsync(f => IsSubscribeFrameFor(f, symbol), EventWait);
        }

        for (var i = 0; i < symbols.Length; i++)
        {
            await first.SendTextAsync(TickerFrame(symbols[i], SessionPrice(i, 1), 1, 1900000001000L));
        }

        // The server drops the session; the client reconnects on its own.
        await first.CloseGracefullyAsync();
        await TestPoll.PollUntilAsync(() => server.ConnectionCount >= 2, EventWait);

        // The reconnect burst re-subscribes EVERY tracked symbol: one
        // additional subscribe frame per symbol lands on the new session.
        await TestPoll.PollUntilAsync(
            () => symbols.All(symbol =>
                server.SnapshotFrames().Count(f => IsSubscribeFrameFor(f, symbol)) >= 2),
            EventWait);

        var second = await server.WaitForConnectionAsync(2, EventWait);
        for (var i = 0; i < symbols.Length; i++)
        {
            await second.SendTextAsync(TickerFrame(symbols[i], SessionPrice(i, 2), 2, 1900000002000L));
        }

        var feeds = await Task.WhenAll(feedTasks);
        for (var i = 0; i < symbols.Length; i++)
        {
            feeds[i].Should().HaveCount(2, "one ticker per session proves the topic survived the reconnect");
            feeds[i][0].LastPrice.Should().Be(SessionPriceValue(i, 1));
            feeds[i][1].LastPrice.Should().Be(SessionPriceValue(i, 2));
        }

        // No churn: exactly one re-subscribe per symbol (plus its original
        // subscription), and the connection count stays pinned on two.
        await Task.Delay(ConnectionStabilityWait);
        server.ConnectionCount.Should().Be(2, "a healthy adapter stays; a churned one grows sessions");
        foreach (var symbol in symbols)
        {
            server.SnapshotFrames()
                .Count(f => IsSubscribeFrameFor(f, symbol))
                .Should().Be(2);
        }

        // The honest pump cancelled nothing: zero re-subscribe failures logged.
        stack.Logger.Entries.Should().NotContain(
            entry => entry.Message.StartsWith("Bybit topic re-subscribe", StringComparison.Ordinal),
            "an in-flight resubscribe send was never cancelled");
    }

    #endregion

    #region Helpers: frames

    /// <summary>Whether the received frame is a subscribe request carrying this symbol's topics.</summary>
    private static bool IsSubscribeFrameFor(string frame, Symbol symbol)
    {
        var exchangeSymbol = symbol.GetExchangeFormat(BybitId);
        return frame.Contains("\"op\":\"subscribe\"", StringComparison.Ordinal)
               && frame.Contains("tickers." + exchangeSymbol, StringComparison.Ordinal);
    }

    /// <summary>A snapshot ticker frame for any symbol with the given price and session tag.</summary>
    private static string TickerFrame(Symbol symbol, string lastPrice, int session, long ts)
    {
        var exchangeSymbol = symbol.GetExchangeFormat(BybitId);
        return "{\"topic\":\"tickers." + exchangeSymbol +
               "\",\"ts\":" + ts.ToString(CultureInfo.InvariantCulture) +
               ",\"type\":\"snapshot\",\"data\":{\"symbol\":\"" + exchangeSymbol +
               "\",\"lastPrice\":\"" + lastPrice +
               "\",\"volume24h\":\"12.5\",\"price24hPcnt\":\"0.0500\"}}";
    }

    /// <summary>
    /// The session-distinguishing last price: per-index spaced and +1 on the
    /// second session, so a feed mixing sessions is caught by the prices.
    /// </summary>
    private static string SessionPrice(int index, int session) =>
        SessionPriceValue(index, session).ToString(CultureInfo.InvariantCulture);

    private static decimal SessionPriceValue(int index, int session) =>
        50000.5m + (index * 10m) + (session - 1m);

    /// <summary>One level pair wire fragment: [["price","quantity"]].</summary>
    private static string Level(string price, string quantity) =>
        "[[\"" + price + "\",\"" + quantity + "\"]]";

    /// <summary>An order book frame for any symbol/type/sequence with the given level arrays.</summary>
    private static string BookFrame(
        Symbol symbol,
        string type,
        long sequence,
        string bids,
        string asks)
    {
        var exchangeSymbol = symbol.GetExchangeFormat(BybitId);
        var ts = sequence * 10;
        return "{\"topic\":\"orderbook.50." + exchangeSymbol +
               "\",\"ts\":" + ts.ToString(CultureInfo.InvariantCulture) +
               ",\"type\":\"" + type +
               "\",\"data\":{\"s\":\"" + exchangeSymbol +
               "\",\"b\":" + bids +
               ",\"a\":" + asks +
               ",\"u\":" + sequence.ToString(CultureInfo.InvariantCulture) +
               ",\"seq\":" + sequence.ToString(CultureInfo.InvariantCulture) + "}}";
    }

    #endregion

    #region Helpers: stack

    /// <summary>
    /// The recording logger: collects level + message pairs, so the tests can
    /// pin the honest no-cancellation property on the pump's own logs.
    /// </summary>
    private sealed class RecordingLogger : ILogger<BybitAdapter>
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

        private ResubscribeStack(LoopbackWebSocketServer server, RecordingLogger logger, BybitAdapter adapter, ServiceProvider provider)
        {
            Server = server;
            Logger = logger;
            Adapter = adapter;
            _provider = provider;
        }

        public LoopbackWebSocketServer Server { get; }

        public RecordingLogger Logger { get; }

        public BybitAdapter Adapter { get; }

        #endregion

        #region Methods: Factory

        public static async Task<ResubscribeStack> StartAsync()
        {
            var server = await LoopbackWebSocketServer.StartAsync();

            var services = new ServiceCollection();
            services.AddHttpClient(BybitAdapter.HttpClientName);
            var provider = services.BuildServiceProvider();

            var logger = new RecordingLogger();
            var adapter = new BybitAdapter(
                logger,
                provider.GetRequiredService<IHttpClientFactory>(),
                new BybitEndpoints(
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
