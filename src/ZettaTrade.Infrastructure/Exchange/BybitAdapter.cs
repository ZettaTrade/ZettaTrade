namespace ZettaTrade.Infrastructure.Exchange;

using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.Infrastructure.RateLimiting;

/// <summary>
/// Bybit v5 spot exchange adapter implementation (read-only market data).
/// Mirrors the Binance structure: a named HTTP client for REST, one shared
/// WebSocket for all market data streams, endpoints from the
/// <see cref="ExchangeEndpointRegistry"/>. Wire facts measured live on
/// 2026-11-02 (public, key-less endpoints): the spot catalog is one page of
/// ~520 server-typed instruments (<c>baseCoin</c>/<c>quoteCoin</c>/<c>status</c>),
/// klines arrive newest-first with all numbers as strings, and the stream is
/// ONE multiplexed socket answered in <c>{"topic",...,"data"}</c> frames
/// where spot tickers are snapshot-typed, <c>orderbook.50</c> deltas carry a
/// monotonic <c>u</c> (quantity <c>"0"</c> deletes a level) and Bybit
/// dislikes silence — hence the adapter-owned keepalive ping.
/// </summary>
public sealed class BybitAdapter : ExchangeAdapterBase, IMarketDataAdapter
{
    #region Constants

    /// <summary>Named HttpClientFactory client used for REST requests.</summary>
    public const string HttpClientName = "bybit";

    /// <summary>Timeout for REST requests, in seconds.</summary>
    public const int RequestTimeoutSeconds = 30;

    /// <summary>Production REST API base URL.</summary>
    public const string DefaultRestBaseUrl = "https://api.bybit.com";

    /// <summary>Production WebSocket URL of the v5 public spot stream.</summary>
    public const string DefaultWsUrl = "wss://stream.bybit.com/v5/public/spot";

    /// <summary>REST path of the symbol catalog.</summary>
    private const string CatalogRequestPath = "/v5/market/instruments-info";

    /// <summary>REST path of the kline history endpoint.</summary>
    private const string KlineRequestPath = "/v5/market/kline";

    /// <summary>The per-second request budget the venue publishes for its public market REST endpoints, as shipped.</summary>
    private const int PublicRequestsPerSecond = 10;

    /// <summary>The refill cadence of Bybit's per-endpoint request budgets.</summary>
    private static readonly TimeSpan RestRefillInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The shipped REST budget manifest (T-106): the venue caps each public
    /// market endpoint with its own requests-per-second count, so each
    /// family carries its own bucket in that dialect, rescalable live
    /// through <c>Limiter.Bybit.RequestsPerSecond</c>.
    /// </summary>
    public static RestRateLimiterOptions DefaultRestLimits { get; } = new()
    {
        SettingsKey = "Bybit",
        ScaleKind = RestBudgetScaleKind.RequestsPerSecond,
        Endpoints =
        [
            new RestEndpointBudget(KlineRequestPath, PublicRequestsPerSecond, RestRefillInterval),
            new RestEndpointBudget(CatalogRequestPath, PublicRequestsPerSecond, RestRefillInterval),
        ],
    };

    /// <summary>Page size requested for the spot catalog (the server's own cap).</summary>
    private const int CatalogPageSize = 1000;

    /// <summary>The request weight a public Bybit market REST call spends from its bucket (one per request).</summary>
    private const int CatalogPageWeight = 1;

    /// <summary>
    /// Upper bound of catalog pages per load: protects the cursor loop from a
    /// pathological (or hostile) server response that never stops paginating.
    /// The blended real-world catalog fits ONE page; if the cap ever bites it
    /// is one aggregated warning, never a silent truncation.
    /// </summary>
    private const int MaxCatalogPages = 5;

    /// <summary>Order book depth level count the adapter subscribes and publishes.</summary>
    private const int DepthLevelCount = 50;

    /// <summary>The smallest order book depth Bybit spot serves as a topic.</summary>
    private const int MinBookDepthCount = 1;

    /// <summary>The order book depths Bybit spot serves (<c>orderbook.1</c>, <c>orderbook.50</c>); everything else is a rejected request, never a silent default.</summary>
    private static readonly IReadOnlySet<int> SupportedOrderBookDepths =
        new HashSet<int> { MinBookDepthCount, DepthLevelCount };

    /// <summary>Interval of the adapter-owned WebSocket keepalive ping, in seconds.</summary>
    private const int KeepaliveIntervalSeconds = 20;

    /// <summary>The literal keepalive ping frame (Bybit answers with an <c>"op":"pong"</c> control frame).</summary>
    private const string KeepalivePingFrame = """{"op":"ping"}""";

    private const string TickerTopicPrefix = "tickers.";
    private const string OrderBookTopicPrefix = "orderbook.";
    private const string PublicTradeTopicPrefix = "publicTrade.";

    #endregion

    #region Properties

    /// <inheritdoc/>
    public override string Id => "bybit";

    /// <inheritdoc/>
    public override string Name => "Bybit";

    /// <inheritdoc/>
    public override ExchangeCapabilities Capabilities =>
        ExchangeCapabilities.MarketData | ExchangeCapabilities.OrderBook | ExchangeCapabilities.HistoricalData;

    /// <summary>Configured REST base URL (internal: the registration tests assert it, no network).</summary>
    internal string RestBaseUrl => _baseUrl;

    /// <summary>Configured WS URL (internal: the registration tests assert it).</summary>
    internal string WsUrl => _wsUrl;

    /// <inheritdoc/>
    protected override IReadOnlySet<int> SupportedBookDepths => SupportedOrderBookDepths;

    /// <summary>
    /// The adapter's explicit reconnect policy (T-105): the shipped production
    /// timing — the same shape <see cref="ReconnectPolicy.Default"/> carries.
    /// A visible per-adapter object, not hidden constants: a future
    /// settings-driven or per-venue divergence stays a one-line change.
    /// </summary>
    public static ReconnectPolicy DefaultReconnectPolicy { get; } = new()
    {
        InitialDelay = TimeSpan.FromSeconds(5),
        MaxDelay = TimeSpan.FromSeconds(60),
        BackoffMultiplier = 2.0,
        JitterFraction = 0.25,
        MaxAttempts = 5,
    };

    private readonly HttpClient _httpClient;
    private readonly WebSocketClient _wsClient;
    private readonly string _baseUrl;
    private readonly string _wsUrl;
    private readonly IRestRateLimiter _restLimiter;

    /// <summary>Serializes every outbound frame across concurrent stream starts, reconnect re-subscribes and the keepalive.</summary>
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    /// <summary>Order book state machines keyed by the normalized symbol.</summary>
    private readonly ConcurrentDictionary<string, BybitOrderBookAssembler> _bookAssemblers = new();

    /// <summary>Currently subscribed symbols keyed by the exchange-format name (the reconnect re-subscribe set).</summary>
    private readonly ConcurrentDictionary<string, Symbol> _subscribedSymbols = new();

    /// <summary>
    /// Topic re-subscribe requests (a book gap or a fresh socket): each one
    /// lands in this queue and the ONE consumer task serves them FIFO,
    /// serialized through <see cref="_sendGate"/> and never fire-and-forget.
    /// </summary>
    private readonly Channel<Symbol> _resubscribeQueue = Channel.CreateUnbounded<Symbol>(
        new UnboundedChannelOptions { SingleReader = true });

    private readonly IDisposable _messageSubscription;
    private readonly IDisposable _connectionSubscription;

    /// <summary>Adapter-owned keepalive loop lifetime; the loop task is awaited on disposal, never abandoned.</summary>
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly Task _keepaliveLoop;

    /// <summary>
    /// The queue's one consumer task: entries are served strictly one at a
    /// time, so an in-flight re-subscribe send is NEVER cancelled by a newer
    /// request — a cancelled send aborts the whole socket and churns the
    /// reconnect with no market data (BUG-003). Awaited on disposal, never
    /// abandoned.
    /// </summary>
    private readonly Task _resubscribeLoop;

    private int _disposed;

    #endregion

    #region Constructor

    /// <summary>
    /// Create a new BybitAdapter instance. Endpoints come from the
    /// registration configuration (see <see cref="ExchangeEndpointRegistry"/>)
    /// or fall back to the production hosts; providing a pair with blank
    /// members is a configuration error and throws immediately (the
    /// tolerant missing-entry case is the registry's job, not the adapter's).
    /// The reconnect supervisor is bound when provided (T-105): the adapter
    /// registers its policy and hands the transport a state sink; direct
    /// constructions without one keep compiling and report nothing.
    /// The REST rate limiter is wired when provided (T-106): the
    /// registration seam builds it from the shipped manifest and the
    /// settings service; unwired constructions (tests, plugin hosts) run
    /// unshaped through the pass-through limiter.
    /// </summary>
    public BybitAdapter(
        ILogger<BybitAdapter> logger,
        IHttpClientFactory httpClientFactory,
        BybitEndpoints? endpoints = null,
        ReconnectSupervisor? reconnectSupervisor = null,
        IRestRateLimiter? restLimiter = null)
        : base(logger)
    {
        _baseUrl = endpoints?.RestBaseUrl ?? DefaultRestBaseUrl;
        _wsUrl = endpoints?.WsUrl ?? DefaultWsUrl;

        if (string.IsNullOrWhiteSpace(_baseUrl) || string.IsNullOrWhiteSpace(_wsUrl))
        {
            throw new ArgumentException(
                "Configured Bybit endpoints must carry non-empty RestBaseUrl and WsUrl",
                nameof(endpoints));
        }

        _httpClient = httpClientFactory.CreateClient(HttpClientName);
        _restLimiter = restLimiter ?? UnthrottledRestRateLimiter.Instance;
        _wsClient = new WebSocketClient(
            _wsUrl,
            logger,
            DefaultReconnectPolicy,
            reconnectSupervisor?.For(Id, DefaultReconnectPolicy));

        _messageSubscription = _wsClient.MessageStream.Subscribe(HandleWebSocketMessage);
        _connectionSubscription = _wsClient.ConnectionStateStream.Subscribe(HandleConnectionStateChanged);

        _keepaliveLoop = RunKeepaliveLoopAsync(_lifetimeCts.Token);
        _resubscribeLoop = RunResubscribeLoopAsync(_lifetimeCts.Token);
    }

    #endregion

    #region Public Methods

    /// <inheritdoc/>
    public override async Task ConnectAsync(CancellationToken ct = default)
    {
        await _wsClient.ConnectAsync(ct);
        IsConnected = _wsClient.IsConnected;
    }

    /// <inheritdoc/>
    public override async Task DisconnectAsync(CancellationToken ct = default)
    {
        await _wsClient.DisconnectAsync();
        IsConnected = false;
    }

    /// <summary>
    /// Start the streams for one symbol on demand (called by the first
    /// subscriber of any feed): connect once when not connected, then
    /// subscribe the symbol's ticker, order book and trade topics. Serialized
    /// with the send gate so concurrent starts cannot interleave frames on
    /// the shared WebSocket. The caller's token covers the gate wait and the
    /// connect only: the SEND rides the adapter-owned lifetime — cancelling
    /// it mid-send would abort the shared socket for every feed (BUG-004).
    /// </summary>
    protected override async Task StartSymbolStreamsAsync(
        Symbol symbol,
        int? bookDepth,
        CancellationToken ct)
    {
        await _sendGate.WaitAsync(ct);
        try
        {
            if (!_wsClient.IsConnected)
            {
                await ConnectAsync(ct);
            }

            await _wsClient.SendJsonAsync(
                new SubscriptionFrame(FrameOps.Subscribe, BuildTopics(symbol, bookDepth)),
                _lifetimeCts.Token);
            _subscribedSymbols[symbol.GetExchangeFormat(Id)] = symbol;
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <inheritdoc/>
    public override async Task<IReadOnlyList<Symbol>> GetSymbolsAsync(CancellationToken ct = default)
    {
        var symbols = new List<Symbol>();
        var dropped = 0;
        var pages = 0;
        var cursor = string.Empty;
        var truncated = false;

        do
        {
            var url = $"{_baseUrl}{CatalogRequestPath}?category=spot&limit={CatalogPageSize}";
            if (!string.IsNullOrEmpty(cursor))
            {
                url += $"&cursor={Uri.EscapeDataString(cursor)}";
            }

            var result = await GetRestResultAsync(
                RestLane.Background,
                CatalogRequestPath,
                CatalogPageWeight,
                url,
                ct);
            foreach (var item in result.GetProperty("list").EnumerateArray())
            {
                var baseCoin = TryGetString(item, "baseCoin");
                var quoteCoin = TryGetString(item, "quoteCoin");
                var status = TryGetString(item, "status");

                if (baseCoin is null || quoteCoin is null ||
                    string.IsNullOrWhiteSpace(baseCoin) ||
                    string.IsNullOrWhiteSpace(quoteCoin))
                {
                    // The split is server-served metadata; a malformed entry
                    // is counted and dropped — never guessed.
                    dropped++;
                    continue;
                }

                if (status == "Trading")
                {
                    symbols.Add(new Symbol(baseCoin, quoteCoin, AssetClass.Crypto));
                }
            }

            cursor = result.TryGetProperty("nextPageCursor", out var cursorProp)
                ? cursorProp.GetString() ?? string.Empty
                : string.Empty;
            pages++;
            truncated = pages >= MaxCatalogPages && !string.IsNullOrEmpty(cursor);
        }
        while (!truncated && !string.IsNullOrEmpty(cursor));

        if (truncated)
        {
            Logger.LogWarning(
                "The Bybit spot catalog exceeded the shipped page cap ({MaxPages} pages of {PageSize}); the load stops with a partial catalog",
                MaxCatalogPages,
                CatalogPageSize);
        }

        if (dropped > 0)
        {
            Logger.LogWarning(
                "{Count} Bybit catalog entries arrived malformed and are dropped",
                dropped);
        }

        return symbols;
    }

    /// <inheritdoc/>
    public override async Task<IReadOnlyList<Bar>> GetBarsAsync(
        Symbol symbol,
        TimeFrame timeFrame,
        DateTimeOffset from,
        DateTimeOffset? to = null,
        int limit = 500,
        CancellationToken ct = default)
    {
        var interval = ConvertTimeFrame(timeFrame);
        var startTime = from.ToUnixTimeMilliseconds();
        var endTime = to?.ToUnixTimeMilliseconds() ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var url = $"{_baseUrl}{KlineRequestPath}" +
                  $"?category=spot" +
                  $"&symbol={symbol.GetExchangeFormat(Id)}" +
                  $"&interval={interval}" +
                  $"&start={startTime}" +
                  $"&end={endTime}" +
                  $"&limit={limit}";

        var result = await GetRestResultAsync(
            RestLaneResolver.LaneForBarRange(to, DateTimeOffset.UtcNow),
            KlineRequestPath,
            CatalogPageWeight,
            url,
            ct);
        var rows = result.GetProperty("list");

        // Bybit v5 kline rows: [startMs, open, high, low, close, volume,
        // turnover], ALL numeric fields as strings, NEWEST FIRST.
        var bars = new List<Bar>(rows.GetArrayLength());
        foreach (var row in rows.EnumerateArray())
        {
            bars.Add(new Bar(
                DateTimeOffset.FromUnixTimeMilliseconds(BinanceJsonNumbers.ParseInt64(row[0])),
                BinanceJsonNumbers.ParseDecimal(row[1]),
                BinanceJsonNumbers.ParseDecimal(row[2]),
                BinanceJsonNumbers.ParseDecimal(row[3]),
                BinanceJsonNumbers.ParseDecimal(row[4]),
                BinanceJsonNumbers.ParseDecimal(row[5])));
        }

        // The adapter bar contract is ascending (Binance's order); reverse the
        // newest-first Bybit order in place.
        bars.Reverse();

        return bars;
    }

    /// <summary>
    /// Subscribe to ticker, 50-level order book and public trades for a
    /// symbol (one multiplexed frame; the server answers one ack per frame).
    /// </summary>
    public async Task SubscribeSymbolAsync(Symbol symbol, CancellationToken ct = default)
    {
        await _sendGate.WaitAsync(ct);
        try
        {
            await _wsClient.SendJsonAsync(
                new SubscriptionFrame(FrameOps.Subscribe, BuildTopics(symbol)),
                ct);
            _subscribedSymbols[symbol.GetExchangeFormat(Id)] = symbol;
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <summary>
    /// Unsubscribe from all topics for a symbol and drop its book state.
    /// </summary>
    public async Task UnsubscribeSymbolAsync(Symbol symbol, CancellationToken ct = default)
    {
        await _sendGate.WaitAsync(ct);
        try
        {
            await _wsClient.SendJsonAsync(
                new SubscriptionFrame(FrameOps.Unsubscribe, BuildTopics(symbol)),
                ct);
            _subscribedSymbols.TryRemove(symbol.GetExchangeFormat(Id), out _);
            _bookAssemblers.TryRemove(symbol.Normalized, out _);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    #endregion

    #region Private Methods

    /// <summary>
    /// The exact topic names the adapter requests per symbol (deterministic
    /// order; the depth level count is pinned by <see cref="DepthLevelCount"/>
    /// unless the caller carries an explicit — already validated — request).
    /// </summary>
    private string[] BuildTopics(Symbol symbol, int? bookDepth = null)
    {
        var symbolStr = symbol.GetExchangeFormat(Id);
        return
        [
            $"{TickerTopicPrefix}{symbolStr}",
            $"{OrderBookTopicPrefix}{(bookDepth ?? DepthLevelCount)}.{symbolStr}",
            $"{PublicTradeTopicPrefix}{symbolStr}",
        ];
    }

    /// <summary>
    /// Re-send the topic subscription frames for one symbol (gap recovery or
    /// a fresh socket). The set membership is kept by the regular subscribe
    /// paths, so this send-only path cannot resurrect a stale entry.
    /// </summary>
    private async Task ResubscribeStreamsAsync(Symbol symbol, CancellationToken ct)
    {
        await _sendGate.WaitAsync(ct);
        try
        {
            await _wsClient.SendJsonAsync(
                new SubscriptionFrame(FrameOps.Subscribe, BuildTopics(symbol)),
                ct);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <summary>
    /// The ONE consumer of the re-subscribe queue: every entry is served FIFO
    /// and runs to completion — a newer request (a reconnect burst trailing
    /// several symbols, a gap frame) queues behind and never cancels an
    /// in-flight send, because a cancelled send aborts the whole socket and
    /// churns the reconnect. The only cancellation left on this path is
    /// disposal, which tears the transport down on its own.
    /// </summary>
    private async Task RunResubscribeLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var symbol in _resubscribeQueue.Reader.ReadAllAsync(ct))
            {
                try
                {
                    await ResubscribeStreamsAsync(symbol, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One failed re-subscribe must not kill the pump: the
                    // next book frame re-arms the recovery on its own.
                    Logger.LogWarning(
                        ex,
                        "Bybit topic re-subscribe for {Symbol} failed",
                        symbol.Normalized);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Disposal: the only exit of the re-subscribe pump.
        }
    }

    /// <summary>
    /// Re-subscribe every tracked symbol after a reconnection: the server
    /// holds no session state across sockets, so a silent reconnection would
    /// otherwise keep subscriptions "live" while no data flows (the first
    /// connect skips this path — the tracked set is still empty and the
    /// per-symbol start subscribes on its own).
    /// </summary>
    private void HandleConnectionStateChanged(bool isConnected)
    {
        IsConnected = isConnected;
        Logger.LogInformation(
            "Bybit WebSocket connection state: {State}",
            isConnected ? "Connected" : "Disconnected");

        if (isConnected && _subscribedSymbols.IsEmpty)
        {
            return;
        }

        if (!isConnected)
        {
            return;
        }

        foreach (var symbol in _subscribedSymbols.Values)
        {
            _resubscribeQueue.Writer.TryWrite(symbol);
        }
    }

    /// <summary>
    /// The adapter-owned keepalive: Bybit disconnects idle public sockets,
    /// while the infrastructure has no app-level ping — so the adapter sends
    /// an <c>"op":"ping"</c> frame every <see cref="KeepaliveIntervalSeconds"/>
    /// on a PeriodicTimer owned by <see cref="DisposeAsync"/> (the loop task
    /// is tracked and awaited there, never abandoned).
    /// </summary>
    private async Task RunKeepaliveLoopAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(KeepaliveIntervalSeconds));
            while (await timer.WaitForNextTickAsync(ct))
            {
                if (!_wsClient.IsConnected)
                {
                    continue;
                }

                try
                {
                    await _sendGate.WaitAsync(ct);
                    try
                    {
                        await _wsClient.SendAsync(KeepalivePingFrame, ct);
                    }
                    finally
                    {
                        _sendGate.Release();
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One failed ping never kills the heartbeat: the socket's
                    // own error handling drives the reconnection.
                    Logger.LogWarning(ex, "Bybit keepalive ping failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Disposal: the only exit of the keepalive loop.
        }
    }

    /// <summary>
    /// Bybit v5 frame dispatch: data frames carry a "topic", control frames
    /// (subscribe/unsubscribe/ping acks) do not — errors from one message
    /// never leave the handler.
    /// </summary>
    private void HandleWebSocketMessage(string message)
    {
        try
        {
            using var json = JsonDocument.Parse(message);
            var root = json.RootElement;

            if (!root.TryGetProperty("topic", out var topicProp))
            {
                HandleControlFrame(root);
                return;
            }

            var topic = topicProp.GetString();
            if (string.IsNullOrEmpty(topic))
            {
                return;
            }

            if (!root.TryGetProperty("data", out var data))
            {
                return;
            }

            var type = root.TryGetProperty("type", out var typeProp)
                ? typeProp.GetString()
                : null;

            var timestamp = root.TryGetProperty("ts", out var tsProp) &&
                            tsProp.ValueKind == JsonValueKind.Number
                ? DateTimeOffset.FromUnixTimeMilliseconds(tsProp.GetInt64())
                : DateTimeOffset.UtcNow;

            if (topic.StartsWith(OrderBookTopicPrefix, StringComparison.Ordinal))
            {
                HandleOrderBookFrame(topic, type, data, timestamp);
            }
            else if (topic.StartsWith(TickerTopicPrefix, StringComparison.Ordinal))
            {
                // Spot tickers are snapshot-typed (live-verified; the delta
                // type exists only on the linear/options tickers) — the full
                // payload is parsed as-is.
                HandleTickerFrame(topic, data, timestamp);
            }
            else if (topic.StartsWith(PublicTradeTopicPrefix, StringComparison.Ordinal))
            {
                HandlePublicTradeFrame(topic, data);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error handling Bybit WebSocket message");
        }
    }

    /// <summary>
    /// Control frames without a "topic": a failing op (most usefully a
    /// <c>"success":false</c> subscribe ack, e.g. against a restarted host)
    /// logs loudly; successful acks and pongs stay quiet.
    /// </summary>
    private void HandleControlFrame(JsonElement root)
    {
        var op = root.TryGetProperty("op", out var opProp) ? opProp.GetString() : null;
        if (op is null)
        {
            // Not a control frame at all (an unknown bare payload) — ignored.
            return;
        }

        if (root.TryGetProperty("success", out var success) && !success.GetBoolean())
        {
            var retMsg = root.TryGetProperty("ret_msg", out var msgProp)
                ? msgProp.GetString()
                : null;
            Logger.LogWarning(
                "Bybit WebSocket operation {Op} failed: {RetMsg}",
                op,
                retMsg);
        }
    }

    /// <summary>
    /// Map a spot ticker frame: the price journey is lastPrice only — the v5
    /// SPOT tickers carry no bid/ask fields (live-verified; the mapped ticker
    /// keeps the honest absent value instead of a guessed substitute), and
    /// <c>price24hPcnt</c> is a fraction, so it maps into the percent basis
    /// the Binance ticker already ships (<c>"P"</c> is a percent there).
    /// </summary>
    private void HandleTickerFrame(string topic, JsonElement data, DateTimeOffset timestamp)
    {
        var symbol = ParseExchangeSymbol(GetTopicSymbol(topic));
        if (symbol is null)
        {
            Logger.LogWarning("Bybit tickers topic {Topic}: unrecognised symbol format", topic);
            return;
        }

        var ticker = new Ticker(
            symbol,
            BinanceJsonNumbers.ParseDecimal(data.GetProperty("lastPrice")),
            0m,
            0m,
            BinanceJsonNumbers.ParseDecimal(data.GetProperty("volume24h")),
            BinanceJsonNumbers.ParseDecimal(data.GetProperty("price24hPcnt")) * 100m,
            timestamp);

        PublishTicker(ticker);
    }

    /// <summary>
    /// Route an <c>orderbook.{depth}</c> frame into the per-symbol state
    /// machine: snapshots rebuild, deltas continue the sequence, a gap
    /// discards the state and re-subscribes the topic (via the serialized
    /// re-subscribe queue, never fire-and-forget). Stale deltas are the
    /// server's own races and are skipped silently — logging them would
    /// drown the real warnings at the 100 ms stream rate.
    /// </summary>
    private void HandleOrderBookFrame(string topic, string? type, JsonElement data, DateTimeOffset timestamp)
    {
        var symbol = ParseExchangeSymbol(GetTopicSymbol(topic));
        if (symbol is null)
        {
            Logger.LogWarning("Bybit order book topic {Topic}: unrecognised symbol format", topic);
            return;
        }

        if (type is not (FrameTypes.Snapshot or FrameTypes.Delta))
        {
            Logger.LogWarning(
                "Bybit order book topic {Topic}: unknown frame type {Type}, the frame is dropped",
                topic,
                type);
            return;
        }

        if (!TryParseSequencedLevels(data, out var sequence, out var bids, out var asks))
        {
            Logger.LogWarning(
                "Bybit order book topic {Topic}: frame without a parseable sequence or level arrays, dropped",
                topic);
            return;
        }

        var assembler = _bookAssemblers.GetOrAdd(
            symbol.Normalized,
            _ => new BybitOrderBookAssembler(symbol, DepthLevelCount));

        var apply = type == FrameTypes.Snapshot
            ? assembler.ApplySnapshot(sequence, bids, asks, timestamp)
            : assembler.ApplyDelta(sequence, bids, asks, timestamp);

        switch (apply.Outcome)
        {
            case BybitOrderBookOutcome.Applied:
                PublishOrderBook(apply.Book!);
                break;

            case BybitOrderBookOutcome.StaleSkipped:
                // A benign server race; the state is untouched.
                break;

            case BybitOrderBookOutcome.GapDetected:
                Logger.LogWarning(
                    "Bybit order book gap for {Symbol} at u={Sequence}; the state is discarded and the topic re-subscribed",
                    symbol.Normalized,
                    sequence);
                _resubscribeQueue.Writer.TryWrite(symbol);
                break;
        }
    }

    /// <summary>
    /// Parse a public trade frame (an ARRAY of trade events): "S" is the
    /// aggressor side, "p"/"v" the (string) numbers, "T" the ms epoch, "i"
    /// the trade id. Malformed entries are counted per frame and dropped —
    /// an unparsable side is an error, never a defaulted guess.
    /// </summary>
    private void HandlePublicTradeFrame(string topic, JsonElement data)
    {
        var symbol = ParseExchangeSymbol(GetTopicSymbol(topic));
        if (symbol is null)
        {
            Logger.LogWarning("Bybit public trade topic {Topic}: unrecognised symbol format", topic);
            return;
        }

        if (data.ValueKind != JsonValueKind.Array)
        {
            Logger.LogWarning(
                "Bybit public trade topic {Topic}: data is not an event array, the frame is dropped",
                topic);
            return;
        }

        var published = 0;
        var dropped = 0;
        foreach (var item in data.EnumerateArray())
        {
            // Object check FIRST: a non-object entry has no properties, and
            // TryGetProperty would throw on it and abort every remaining
            // trade of the frame (BUG-027) instead of being counted.
            if (item.ValueKind != JsonValueKind.Object)
            {
                dropped++;
                continue;
            }

            if (!item.TryGetProperty("S", out var sideProp) ||
                sideProp.GetString() is not (FrameSides.Buy or FrameSides.Sell) ||
                !item.TryGetProperty("p", out var priceProp) ||
                !item.TryGetProperty("v", out var quantityProp) ||
                !item.TryGetProperty("T", out var timeProp) ||
                timeProp.ValueKind != JsonValueKind.Number)
            {
                dropped++;
                continue;
            }

            var side = sideProp.GetString();

            published++;
            PublishTrade(new MarketTrade
            {
                Id = item.TryGetProperty("i", out var idProp) &&
                     idProp.ValueKind == JsonValueKind.String
                    ? idProp.GetString() ?? string.Empty
                    : string.Empty,
                Symbol = symbol,
                Side = side == FrameSides.Sell ? OrderSide.Sell : OrderSide.Buy,
                Price = new Price(BinanceJsonNumbers.ParseDecimal(priceProp)),
                Quantity = new Quantity(BinanceJsonNumbers.ParseDecimal(quantityProp)),
                Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(timeProp.GetInt64()),
            });
        }

        if (dropped > 0)
        {
            Logger.LogWarning(
                "{Dropped} of {Total} Bybit public trades for {Symbol} are malformed and are dropped",
                dropped,
                published + dropped,
                symbol.Normalized);
        }
    }

    /// <summary>
    /// Parse a sequenced order book payload: the monotonic "u" plus the two
    /// level arrays. Bybit always serves both sides; an absent side maps to
    /// an empty list ("no levels on that side this frame") — the no-op they
    /// are, never invented levels.
    /// </summary>
    private static bool TryParseSequencedLevels(
        JsonElement data,
        out long sequence,
        out List<PriceLevel> bids,
        out List<PriceLevel> asks)
    {
        bids = [];
        asks = [];
        sequence = 0;

        if (data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("u", out var sequenceProp) ||
            sequenceProp.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        sequence = sequenceProp.GetInt64();

        if (data.TryGetProperty("b", out var bidsProp) && bidsProp.ValueKind == JsonValueKind.Array)
        {
            bids = ParsePriceLevels(bidsProp);
        }

        if (data.TryGetProperty("a", out var asksProp) && asksProp.ValueKind == JsonValueKind.Array)
        {
            asks = ParsePriceLevels(asksProp);
        }

        return true;
    }

    private static List<PriceLevel> ParsePriceLevels(JsonElement array)
    {
        var levels = new List<PriceLevel>(array.GetArrayLength());
        foreach (var level in array.EnumerateArray())
        {
            levels.Add(new PriceLevel(
                BinanceJsonNumbers.ParseDecimal(level[0]),
                BinanceJsonNumbers.ParseDecimal(level[1])));
        }

        return levels;
    }

    private Symbol? ParseExchangeSymbol(string exchangeSymbol)
    {
        return Symbol.TryParseExchangeFormat(exchangeSymbol, Id, out var symbol) && symbol != null
            ? symbol
            : null;
    }

    /// <summary>Extract the symbol part after the LAST dot of a topic ("orderbook.50.BTCUSDT").</summary>
    private static string GetTopicSymbol(string topic) => topic[(topic.LastIndexOf('.') + 1)..];

    private static string? TryGetString(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// Bybit v5 REST responses wrap the payload in {"retCode","retMsg","result"}:
    /// a non-zero retCode is a protocol-level failure (HTTP may still be 200)
    /// and throws — never a defaulted result. The "result" member is the value.
    /// The request rides the rate limiter first (T-106): the acquire waits out
    /// the bucket, and a 429 response with a <c>Retry-After</c> feeds the
    /// venue's park guidance back into the limiter before the failure throws.
    /// </summary>
    private async Task<JsonElement> GetRestResultAsync(
        RestLane lane,
        string endpointKey,
        int weight,
        string url,
        CancellationToken ct)
    {
        using var response = await SendRateLimitedGetAsync(lane, endpointKey, weight, url, ct);
        response.EnsureSuccessStatusCode();

        using var document = await response.Content.ReadFromJsonAsync<JsonDocument>(options: null, cancellationToken: ct);
        var root = document?.RootElement
            ?? throw new InvalidOperationException($"Bybit REST request returned an empty body: {url}");

        if (root.TryGetProperty("retCode", out var retCode) &&
            retCode.ValueKind == JsonValueKind.Number &&
            retCode.GetInt64() != 0)
        {
            var message = root.TryGetProperty("retMsg", out var msgProp) ? msgProp.GetString() : null;
            throw new InvalidOperationException(
                $"Bybit REST request failed: retCode={retCode.GetInt64()}, retMsg={message} ({url})");
        }

        // The result element outlives this method: clone it out of the
        // document's pooled memory before the document is disposed.
        return root.GetProperty("result").Clone();
    }

    /// <summary>
    /// One outbound REST GET through the rate limiter (T-106): the acquire
    /// waits before the send, and a 429 feeds the venue's retry guidance
    /// back into the limiter so every lane parks for the span.
    /// </summary>
    /// <param name="lane">The traffic lane of the call.</param>
    /// <param name="endpointKey">The endpoint family (the adapter's path-template constant).</param>
    /// <param name="weight">The request's documented token cost.</param>
    /// <param name="url">The fully built request URL.</param>
    /// <param name="ct">The caller's cancellation (covers the acquire wait and the send).</param>
    private async Task<HttpResponseMessage> SendRateLimitedGetAsync(
        RestLane lane,
        string endpointKey,
        int weight,
        string url,
        CancellationToken ct)
    {
        await _restLimiter.AcquireAsync(lane, endpointKey, weight, ct);
        var response = await _httpClient.GetAsync(url, ct);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            _restLimiter.ReportBlocked(endpointKey, RestRetryAfterParser.Parse(response.Headers));
        }

        return response;
    }

    private static string ConvertTimeFrame(TimeFrame timeFrame)
    {
        return timeFrame switch
        {
            TimeFrame.M1 => "1",
            TimeFrame.M5 => "5",
            TimeFrame.M15 => "15",
            TimeFrame.M30 => "30",
            TimeFrame.H1 => "60",
            TimeFrame.H4 => "240",
            TimeFrame.D1 => "D",
            TimeFrame.W1 => "W",
            TimeFrame.MN1 => "M",
            _ => throw new ArgumentOutOfRangeException(nameof(timeFrame))
        };
    }

    #endregion

    #region IAsyncDisposable

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _lifetimeCts.Cancel();
        await _keepaliveLoop;
        await _resubscribeLoop;
        _lifetimeCts.Dispose();

        _messageSubscription.Dispose();
        _connectionSubscription.Dispose();

        await base.DisposeAsync();
        await _wsClient.DisposeAsync();
        _sendGate.Dispose();
        // HttpClient comes from IHttpClientFactory; its lifetime is managed there.
    }

    #endregion

    #region Nested Types

    private sealed record SubscriptionFrame(
        [property: JsonPropertyName("op")] string Op,
        [property: JsonPropertyName("args")] string[] Args);

    private static class FrameOps
    {
        public const string Subscribe = "subscribe";
        public const string Unsubscribe = "unsubscribe";
    }

    private static class FrameTypes
    {
        public const string Snapshot = "snapshot";
        public const string Delta = "delta";
    }

    private static class FrameSides
    {
        public const string Buy = "Buy";
        public const string Sell = "Sell";
    }

    #endregion
}
