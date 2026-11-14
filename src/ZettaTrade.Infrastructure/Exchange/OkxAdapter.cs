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
/// OKX v5 spot exchange adapter implementation (read-only market data).
/// Mirrors the Bybit/Binance structure: a named HTTP client for REST, one
/// shared WebSocket for all market data streams, endpoints from the
/// <see cref="ExchangeEndpointRegistry"/>. Wire facts measured on the public
/// v5 surface (public, key-less endpoints): the spot catalog answers
/// <c>instId</c>/<c>baseCcy</c>/<c>quoteCcy</c>/<c>state</c> in ONE page (no
/// cursor), klines arrive newest-first with all numbers as strings, the
/// stream is ONE multiplexed socket answered in <c>{"arg",...,"data"}</c>
/// frames with subscribe/unsubscribe/error acks on the <c>event</c> member,
/// OKX pings its clients every 30 s (the adapter answers the literal
/// "pong" text), the adapter pings first when idle, and the depth rides the
/// continuously-maintained <c>books</c> channel (snapshot-then-update with a
/// monotonic <c>seq</c> — the sequenced assembler applies it; the venue
/// maintains up to <see cref="BookCapacity"/> levels per side).
/// The OKX wire instId format ("BTC-USDT") is mapped ADAPTER-LOCALLY: the
/// normalized pair's separator swaps to the hyphen on the way out and the
/// instId splits back on the hyphen on the way in — Core's <c>Symbol</c>
/// exchange-format table stays untouched (T-194 scope), so the adapter never
/// rides <c>GetExchangeFormat</c> for this venue's wire.
/// </summary>
public sealed class OkxAdapter : ExchangeAdapterBase, IMarketDataAdapter
{
    #region Constants

    /// <summary>Named HttpClientFactory client used for REST requests.</summary>
    public const string HttpClientName = "okx";

    /// <summary>Timeout for REST requests, in seconds.</summary>
    public const int RequestTimeoutSeconds = 30;

    /// <summary>Production REST API base URL.</summary>
    public const string DefaultRestBaseUrl = "https://www.okx.com";

    /// <summary>Production WebSocket URL of the v5 public channel stream.</summary>
    public const string DefaultWsUrl = "wss://ws.okx.com:8443/ws/v5/public";

    /// <summary>REST path of the symbol catalog.</summary>
    private const string CatalogRequestPath = "/api/v5/public/instruments";

    /// <summary>REST path of the candle history endpoint.</summary>
    private const string KlineRequestPath = "/api/v5/market/candles";

    /// <summary>The per-second request budget the venue publishes for the instruments catalog, as shipped (20 requests per 2 s).</summary>
    private const int CatalogRequestsPerSecond = 10;

    /// <summary>The per-second request budget the venue publishes for the market candles endpoint, as shipped (40 requests per 2 s).</summary>
    private const int KlineRequestsPerSecond = 20;

    /// <summary>The refill cadence of OKX's per-endpoint request budgets.</summary>
    private static readonly TimeSpan RestRefillInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The shipped REST budget manifest (T-106): the venue caps each public
    /// market endpoint with its own requests-per-2-seconds count, so each
    /// family carries its own bucket in that dialect, rescalable live
    /// through <c>Limiter.Okx.RequestsPerSecond</c>.
    /// </summary>
    public static RestRateLimiterOptions DefaultRestLimits { get; } = new()
    {
        SettingsKey = "Okx",
        ScaleKind = RestBudgetScaleKind.RequestsPerSecond,
        Endpoints =
        [
            new RestEndpointBudget(KlineRequestPath, KlineRequestsPerSecond, RestRefillInterval),
            new RestEndpointBudget(CatalogRequestPath, CatalogRequestsPerSecond, RestRefillInterval),
        ],
    };

    /// <summary>The candle page size the venue serves per request (its own cap).</summary>
    private const int KlinePageSize = 300;

    /// <summary>The request weight a public OKX market REST call spends from its bucket (one per request).</summary>
    private const int RestRequestWeight = 1;

    /// <summary>
    /// Upper bound of candle pages per load: protects the walk from a
    /// pathological (or hostile) server response that never advances. A
    /// typical workspace load (500 bars) fits two pages; if the cap ever
    /// bites it is one aggregated warning, never a silent truncation.
    /// </summary>
    private const int MaxKlinePages = 5;

    /// <summary>
    /// The depth level count the venue's <c>books</c> channel maintains per
    /// side — the only continuously-maintained depth channel (the
    /// snapshot-only <c>books5</c> serves 5 levels and cannot serve a
    /// workspace default request), so it is the one the adapter rides. The
    /// declaration is the venue-truthful served set: the maintained book
    /// publishes at exactly this depth, a deeper request is rejected, and
    /// shallower requests resolve up to it through the T-072 resolver.
    /// </summary>
    public const int BookCapacity = 400;

    /// <summary>Interval of the adapter-owned WebSocket keepalive ping, in seconds (the venue's own disconnect idle cycle runs 30 s).</summary>
    private const int KeepaliveIntervalSeconds = 25;

    /// <summary>The literal keepalive ping frame (OKX answers with the literal "pong" text).</summary>
    private const string KeepalivePingFrame = "ping";

    /// <summary>The literal pong reply the venue expects for its own 30 s pings.</summary>
    private const string KeepalivePongFrame = "pong";

    private const string TickersChannel = "tickers";
    private const string BooksChannel = "books";
    private const string TradesChannel = "trades";

    /// <summary>The catalog state value a tradable spot instrument carries.</summary>
    private const string InstrumentLiveState = "live";

    #endregion

    #region Properties

    /// <inheritdoc/>
    public override string Id => "okx";

    /// <inheritdoc/>
    public override string Name => "OKX";

    /// <inheritdoc/>
    public override ExchangeCapabilities Capabilities =>
        ExchangeCapabilities.MarketData | ExchangeCapabilities.OrderBook | ExchangeCapabilities.HistoricalData;

    /// <summary>Configured REST base URL (internal: the registration tests assert it, no network).</summary>
    internal string RestBaseUrl => _baseUrl;

    /// <summary>Configured WS URL (internal: the registration tests assert it).</summary>
    internal string WsUrl => _wsUrl;

    /// <inheritdoc/>
    protected override IReadOnlySet<int> SupportedBookDepths { get; } =
        new HashSet<int> { BookCapacity };

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

    /// <summary>Serializes every outbound frame across concurrent stream starts, reconnect re-subscribes, the keepalive and the venue's ping replies.</summary>
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    /// <summary>Order book state machines keyed by the normalized symbol.</summary>
    private readonly ConcurrentDictionary<string, SequencedOrderBookAssembler> _bookAssemblers = new();

    /// <summary>Currently subscribed symbols keyed by the exchange-format name (the reconnect re-subscribe set).</summary>
    private readonly ConcurrentDictionary<string, Symbol> _subscribedSymbols = new();

    /// <summary>
    /// Topic re-subscribe requests (a book gap or a fresh socket): each one
    /// lands in this queue and the ONE consumer task serves them FIFO,
    /// serialized through <see cref="_sendGate"/> and never fire-and-forget.
    /// </summary>
    private readonly Channel<Symbol> _resubscribeQueue = Channel.CreateUnbounded<Symbol>(
        new UnboundedChannelOptions { SingleReader = true });

    /// <summary>
    /// Pong replies to the venue's own pings: the synchronous message pump
    /// writes a token and the ONE tracked reply-loop task serves the literal
    /// "pong" through the send gate — the resubscribe-queue discipline, so no
    /// reply task is ever abandoned mid-send.
    /// </summary>
    private readonly Channel<bool> _pongQueue = Channel.CreateUnbounded<bool>(
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
    /// reconnect with no market data (BUG-004). Awaited on disposal, never
    /// abandoned.
    /// </summary>
    private readonly Task _resubscribeLoop;

    /// <summary>The pong reply loop's tracked task: awaited on disposal, never abandoned.</summary>
    private readonly Task _pongLoop;

    private int _disposed;

    #endregion

    #region Constructor

    /// <summary>
    /// Create a new OkxAdapter instance. Endpoints come from the
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
    public OkxAdapter(
        ILogger<OkxAdapter> logger,
        IHttpClientFactory httpClientFactory,
        OkxEndpoints? endpoints = null,
        ReconnectSupervisor? reconnectSupervisor = null,
        IRestRateLimiter? restLimiter = null)
        : base(logger)
    {
        _baseUrl = endpoints?.RestBaseUrl ?? DefaultRestBaseUrl;
        _wsUrl = endpoints?.WsUrl ?? DefaultWsUrl;

        if (string.IsNullOrWhiteSpace(_baseUrl) || string.IsNullOrWhiteSpace(_wsUrl))
        {
            throw new ArgumentException(
                "Configured OKX endpoints must carry non-empty RestBaseUrl and WsUrl",
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
        _pongLoop = RunPongLoopAsync(_lifetimeCts.Token);
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
    /// subscribe the symbol's ticker, order book and trade channels. Serialized
    /// with the send gate so concurrent starts cannot interleave frames on
    /// the shared WebSocket. The caller's token covers the gate wait and the
    /// connect only: the SEND rides the adapter-owned lifetime — cancelling
    /// it mid-send would abort the shared socket for every feed (BUG-004).
    /// The book depth plays no wire role on OKX (the <c>books</c> channel is
    /// depth-free): any validated request rides the same channel.
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
                new SubscriptionFrame(FrameOps.Subscribe, BuildArgs(symbol)),
                _lifetimeCts.Token);
            _subscribedSymbols[ToWireSymbol(symbol)] = symbol;
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <inheritdoc/>
    public override async Task<IReadOnlyList<Symbol>> GetSymbolsAsync(CancellationToken ct = default)
    {
        var url = $"{_baseUrl}{CatalogRequestPath}?instType=SPOT";
        var result = await GetRestResultAsync(
            RestLane.Background,
            CatalogRequestPath,
            RestRequestWeight,
            url,
            ct);

        var symbols = new List<Symbol>();
        var dropped = 0;

        // OKX answers the whole spot catalog in ONE array (no cursor): each
        // entry carries server-served baseCcy/quoteCcy metadata; a malformed
        // entry is counted and dropped — never guessed.
        foreach (var item in result.EnumerateArray())
        {
            var baseCcy = TryGetString(item, "baseCcy");
            var quoteCcy = TryGetString(item, "quoteCcy");
            var state = TryGetString(item, "state");

            if (baseCcy is null || quoteCcy is null ||
                string.IsNullOrWhiteSpace(baseCcy) ||
                string.IsNullOrWhiteSpace(quoteCcy))
            {
                dropped++;
                continue;
            }

            if (state == InstrumentLiveState)
            {
                symbols.Add(new Symbol(baseCcy, quoteCcy, AssetClass.Crypto));
            }
        }

        if (dropped > 0)
        {
            Logger.LogWarning(
                "{Count} OKX catalog entries arrived malformed and are dropped",
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
        // A non-positive request is a client-side error, never an empty answer.
        if (limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "The bar limit must be positive.");
        }

        var bar = ConvertTimeFrame(timeFrame);
        var startTime = from.ToUnixTimeMilliseconds();
        var endTime = to?.ToUnixTimeMilliseconds() ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // OKX caps every candles request at KlinePageSize rows (newest-first),
        // so a wide window walks back page by page: each page's oldest open
        // becomes the next page's `after` cursor, the walk stops at `from`,
        // the requested count or the shipped page cap — a capped walk is one
        // aggregated warning, never a silent truncation. OKX serves the
        // recent window on this endpoint; deeper history rides the venue's
        // history-candles endpoint and is out of this adapter's shipped scope.
        var bars = new List<Bar>();
        var seenOpens = new HashSet<long>();
        var pages = 0;
        var cursor = endTime;
        var truncated = false;
        var lane = RestLaneResolver.LaneForBarRange(to, DateTimeOffset.UtcNow);

        while (bars.Count < limit)
        {
            var pageLimit = Math.Min(limit - bars.Count, KlinePageSize);
            var url = $"{_baseUrl}{KlineRequestPath}" +
                      $"?instId={ToWireSymbol(symbol)}" +
                      $"&bar={bar}" +
                      $"&after={cursor}" +
                      $"&limit={pageLimit}";

            var rows = await GetRestResultAsync(lane, KlineRequestPath, RestRequestWeight, url, ct);

            if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() == 0)
            {
                break;
            }

            foreach (var row in rows.EnumerateArray())
            {
                // OKX v5 candle rows: [ts, open, high, low, close, vol,
                // volCcy, volCcyQuote, confirm] — ALL numeric fields as
                // strings, NEWEST FIRST; for spot, vol is the base-currency
                // volume. Confirmed rows only: the forming head candle would
                // repaint under the closed history.
                var openMs = BinanceJsonNumbers.ParseInt64(row[0]);
                var confirm = row.GetArrayLength() > 8 && row[8].ValueKind == JsonValueKind.String
                    ? row[8].GetString()
                    : null;
                if (openMs < startTime || !seenOpens.Add(openMs) || confirm is not ("1" or null))
                {
                    continue;
                }

                bars.Add(new Bar(
                    DateTimeOffset.FromUnixTimeMilliseconds(openMs),
                    BinanceJsonNumbers.ParseDecimal(row[1]),
                    BinanceJsonNumbers.ParseDecimal(row[2]),
                    BinanceJsonNumbers.ParseDecimal(row[3]),
                    BinanceJsonNumbers.ParseDecimal(row[4]),
                    BinanceJsonNumbers.ParseDecimal(row[5])));
            }

            var oldestOpen = rows.GetArrayLength() > 0
                ? BinanceJsonNumbers.ParseInt64(rows[rows.GetArrayLength() - 1][0])
                : 0;
            pages++;
            if (oldestOpen <= startTime || pages >= MaxKlinePages)
            {
                truncated = pages >= MaxKlinePages && bars.Count < limit && oldestOpen > startTime;
                break;
            }

            cursor = oldestOpen;
        }

        if (truncated)
        {
            Logger.LogWarning(
                "The OKX candle walk for {Symbol} exceeded the shipped page cap ({MaxPages} pages of {PageSize}); the load stops with a partial range",
                symbol.Normalized,
                MaxKlinePages,
                KlinePageSize);
        }

        // The adapter bar contract is ascending (Binance's order); reverse the
        // newest-first OKX order in place.
        bars.Reverse();

        return bars;
    }

    /// <summary>
    /// Subscribe to ticker, order book and public trades for a symbol (one
    /// multiplexed frame; the server answers one ack per arg).
    /// </summary>
    public async Task SubscribeSymbolAsync(Symbol symbol, CancellationToken ct = default)
    {
        await _sendGate.WaitAsync(ct);
        try
        {
            await _wsClient.SendJsonAsync(
                new SubscriptionFrame(FrameOps.Subscribe, BuildArgs(symbol)),
                ct);
            _subscribedSymbols[ToWireSymbol(symbol)] = symbol;
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <summary>
    /// Unsubscribe from all channels for a symbol and drop its book state.
    /// </summary>
    public async Task UnsubscribeSymbolAsync(Symbol symbol, CancellationToken ct = default)
    {
        await _sendGate.WaitAsync(ct);
        try
        {
            await _wsClient.SendJsonAsync(
                new SubscriptionFrame(FrameOps.Unsubscribe, BuildArgs(symbol)),
                ct);
            _subscribedSymbols.TryRemove(ToWireSymbol(symbol), out _);
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
    /// The wire instId of a normalized pair: the hyphen IS OKX's separator
    /// ("BTC/USDT" -> "BTC-USDT"), mapped locally so Core's Symbol table
    /// stays untouched (T-194 scope).
    /// </summary>
    private static string ToWireSymbol(Symbol symbol) => symbol.Normalized.Replace('/', '-');

    /// <summary>
    /// Parse a wire instId back into a normalized pair ("BTC-USDT" ->
    /// "BTC/USDT"): exactly two non-empty hyphen-separated parts build the
    /// Symbol; anything else is unrecognised (null — the caller counts and
    /// drops it, never guesses).
    /// </summary>
    private static Symbol? ParseWireSymbol(string? instId)
    {
        if (string.IsNullOrWhiteSpace(instId))
        {
            return null;
        }

        var parts = instId.Split('-');
        return parts.Length == 2 &&
               !string.IsNullOrWhiteSpace(parts[0]) &&
               !string.IsNullOrWhiteSpace(parts[1])
            ? new Symbol(parts[0], parts[1], AssetClass.Crypto)
            : null;
    }

    /// <summary>
    /// The exact channel args the adapter requests per symbol (deterministic
    /// order; the depth channel carries no depth parameter — the venue's
    /// books channel is depth-free).
    /// </summary>
    private static ChannelArg[] BuildArgs(Symbol symbol)
    {
        var instId = ToWireSymbol(symbol);
        return
        [
            new ChannelArg(TickersChannel, instId),
            new ChannelArg(BooksChannel, instId),
            new ChannelArg(TradesChannel, instId),
        ];
    }

    /// <summary>
    /// Re-send the channel subscription frame for one symbol (gap recovery or
    /// a fresh socket). The set membership is kept by the regular subscribe
    /// paths, so this send-only path cannot resurrect a stale entry.
    /// </summary>
    private async Task ResubscribeStreamsAsync(Symbol symbol, CancellationToken ct)
    {
        await _sendGate.WaitAsync(ct);
        try
        {
            await _wsClient.SendJsonAsync(
                new SubscriptionFrame(FrameOps.Subscribe, BuildArgs(symbol)),
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
                        "OKX channel re-subscribe for {Symbol} failed",
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
    /// <summary>
    /// The last logged connection state: a connection-state line is a TRANSITION (T-233) —
    /// the WS client's subject seeds the pre-dial <c>false</c> at subscription, and logging
    /// that sync would print this adapter's real endpoint into a session that never dialed
    /// (the T-141 isolation census's property). The false-after-false repeats stay silent
    /// too: the transport's own per-attempt warnings already carry them.
    /// </summary>
    private bool _lastLoggedConnectionState;

    private void HandleConnectionStateChanged(bool isConnected)
    {
        IsConnected = isConnected;
        if (isConnected != _lastLoggedConnectionState)
        {
            _lastLoggedConnectionState = isConnected;
            Logger.LogInformation(
                "OKX WebSocket connection state: {State}",
                isConnected ? "Connected" : "Disconnected");
        }

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
    /// The adapter-owned keepalive: OKX disconnects idle public sockets (its
    /// own cycle runs 30 s), while the infrastructure has no app-level ping —
    /// so the adapter sends the literal "ping" text every
    /// <see cref="KeepaliveIntervalSeconds"/> on a PeriodicTimer owned by
    /// <see cref="DisposeAsync"/> (the loop task is tracked and awaited
    /// there, never abandoned).
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
                    Logger.LogWarning(ex, "OKX keepalive ping failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Disposal: the only exit of the keepalive loop.
        }
    }

    /// <summary>
    /// OKX v5 frame dispatch: the literal "ping"/"pong" keepalive texts are
    /// not JSON at all (the parser would throw on every server ping), data
    /// frames carry the <c>arg</c>/<c>data</c> envelope, and control frames
    /// (<c>event</c>) never publish data. Errors from one message never
    /// leave the handler.
    /// </summary>
    private void HandleWebSocketMessage(string message)
    {
        var trimmed = message.Trim();

        // The venue's keepalive texts: reply to the server's ping, absorb the
        // pong to our own. Never parsed as JSON.
        if (trimmed is KeepalivePingFrame)
        {
            // The tracked reply loop serves the answer (no task is ever
            // abandoned mid-send).
            _pongQueue.Writer.TryWrite(true);
            return;
        }

        if (trimmed is KeepalivePongFrame)
        {
            return;
        }

        try
        {
            using var json = JsonDocument.Parse(message);
            var root = json.RootElement;

            if (root.TryGetProperty("event", out var eventProp))
            {
                HandleControlFrame(eventProp.GetString(), root);
                return;
            }

            if (!root.TryGetProperty("arg", out var arg) ||
                !root.TryGetProperty("data", out var data))
            {
                return;
            }

            var channel = TryGetString(arg, "channel");
            if (string.IsNullOrEmpty(channel))
            {
                return;
            }

            if (data.ValueKind != JsonValueKind.Array)
            {
                Logger.LogWarning(
                    "OKX {Channel} frame: data is not an event array, the frame is dropped",
                    channel);
                return;
            }

            // The book stamp is the frame's local receive moment (T-142):
            // captured once at the ingestion point, never the exchange's
            // server ts — the WS-latency histogram measures transport, not
            // clock offset. The ticker keeps the exchange ts (an event fact).
            var receivedAt = DateTimeOffset.UtcNow;

            switch (channel)
            {
                case BooksChannel:
                    HandleOrderBookFrames(arg, data, receivedAt);
                    break;

                case TickersChannel:
                    HandleTickerFrames(data);
                    break;

                case TradesChannel:
                    HandleTradeFrames(data);
                    break;
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error handling OKX WebSocket message");
        }
    }

    /// <summary>
    /// The ONE consumer of the pong-reply queue: the venue pings its clients
    /// every 30 s and disconnects non-repliers, so each queued token answers
    /// with the literal "pong" through the send gate. Entries run one at a
    /// time and to completion — a cancelled send aborts the whole socket
    /// (BUG-004); one failed reply never kills the loop (the next ping
    /// re-arms the answer). The only cancellation left on this path is
    /// disposal.
    /// </summary>
    private async Task RunPongLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var _ in _pongQueue.Reader.ReadAllAsync(ct))
            {
                try
                {
                    await _sendGate.WaitAsync(ct);
                    try
                    {
                        await _wsClient.SendAsync(KeepalivePongFrame, ct);
                    }
                    finally
                    {
                        _sendGate.Release();
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Logger.LogWarning(ex, "OKX keepalive pong reply failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Disposal: the only exit of the pong reply loop.
        }
    }

    /// <summary>
    /// Control frames ("event"): a failing op (most usefully an
    /// <c>"event":"error"</c> ack, e.g. an unknown channel against a
    /// restarted host) logs loudly; successful acks stay quiet.
    /// </summary>
    private void HandleControlFrame(string? @event, JsonElement root)
    {
        if (@event != "error")
        {
            return;
        }

        var msg = root.TryGetProperty("msg", out var msgProp) && msgProp.ValueKind == JsonValueKind.String
            ? msgProp.GetString()
            : null;
        var code = root.TryGetProperty("code", out var codeProp) && codeProp.ValueKind == JsonValueKind.String
            ? codeProp.GetString()
            : null;
        Logger.LogWarning(
            "OKX WebSocket operation error: code={Code}, msg={Msg}",
            code,
            msg);
    }

    /// <summary>
    /// Map the <c>tickers</c> frames (an event array): the price journey is
    /// last/bidPx/askPx (OKX spot tickers carry the top-of-book), and the
    /// 24 h change is COMPUTED from the venue's own last/open24h — the two
    /// server facts the venue publishes (a zero open leaves the ratio
    /// undefined; the honest map is 0, never an invented direction).
    /// </summary>
    private void HandleTickerFrames(JsonElement data)
    {
        var published = 0;
        var dropped = 0;

        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                dropped++;
                continue;
            }

            var symbol = ParseWireSymbol(TryGetString(item, "instId"));
            if (symbol is null)
            {
                Logger.LogWarning("OKX tickers frame: unrecognised instId format");
                dropped++;
                continue;
            }

            if (!TryGetNumberElement(item, "last", out var last) ||
                !TryGetNumberElement(item, "open24h", out var open24h) ||
                !TryGetNumberElement(item, "vol24h", out var vol24h))
            {
                Logger.LogWarning(
                    "OKX tickers frame for {Symbol}: missing price or volume facts, the event is dropped",
                    symbol.Normalized);
                dropped++;
                continue;
            }

            var lastPrice = BinanceJsonNumbers.ParseDecimal(last);
            var openPrice = BinanceJsonNumbers.ParseDecimal(open24h);
            var changePercent = openPrice == 0m
                ? 0m
                : (lastPrice - openPrice) / openPrice * 100m;

            var hasBid = TryGetNumberElement(item, "bidPx", out var bidPx);
            var hasAsk = TryGetNumberElement(item, "askPx", out var askPx);

            var timestamp = TryGetNumberElement(item, "ts", out var ts)
                ? DateTimeOffset.FromUnixTimeMilliseconds(BinanceJsonNumbers.ParseInt64(ts))
                : DateTimeOffset.UtcNow;

            published++;
            PublishTicker(new Ticker(
                symbol,
                lastPrice,
                hasBid ? BinanceJsonNumbers.ParseDecimal(bidPx) : 0m,
                hasAsk ? BinanceJsonNumbers.ParseDecimal(askPx) : 0m,
                BinanceJsonNumbers.ParseDecimal(vol24h),
                changePercent,
                timestamp));
        }

        if (dropped > 0)
        {
            Logger.LogWarning(
                "{Dropped} of {Total} OKX ticker events are malformed and are dropped",
                dropped,
                published + dropped);
        }
    }

    /// <summary>
    /// Route the <c>books</c> frames into the per-symbol state machine:
    /// snapshots rebuild, updates continue the sequence, a gap discards the
    /// state and re-subscribes the channel (via the serialized re-subscribe
    /// queue, never fire-and-forget). Stale updates are the server's own
    /// races and are skipped silently — logging them would drown the real
    /// warnings at the stream rate. The rebuilt book carries the frame's
    /// local receive moment (T-142's one clock discipline), never the
    /// exchange's server ts.
    /// </summary>
    private void HandleOrderBookFrames(JsonElement arg, JsonElement data, DateTimeOffset receivedAt)
    {
        var symbol = ParseWireSymbol(TryGetString(arg, "instId"));
        if (symbol is null)
        {
            Logger.LogWarning("OKX order book frame: unrecognised instId format");
            return;
        }

        var published = 0;
        var dropped = 0;

        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                dropped++;
                continue;
            }

            var action = TryGetString(item, "action");
            if (action is not (FrameActions.Snapshot or FrameActions.Update))
            {
                Logger.LogWarning(
                    "OKX order book channel for {Symbol}: unknown action {Action}, the frame is dropped",
                    symbol.Normalized,
                    action);
                dropped++;
                continue;
            }

            if (!TryParseSequencedLevels(item, out var sequence, out var bids, out var asks))
            {
                Logger.LogWarning(
                    "OKX order book channel for {Symbol}: frame without a parseable sequence or level arrays, dropped",
                    symbol.Normalized);
                dropped++;
                continue;
            }

            var assembler = _bookAssemblers.GetOrAdd(
                symbol.Normalized,
                _ => new SequencedOrderBookAssembler(symbol, BookCapacity));

            var apply = action == FrameActions.Snapshot
                ? assembler.ApplySnapshot(sequence, bids, asks, receivedAt)
                : assembler.ApplyDelta(sequence, bids, asks, receivedAt);

            switch (apply.Outcome)
            {
                case SequencedBookOutcome.Applied:
                    PublishOrderBook(apply.Book!);
                    published++;
                    break;

                case SequencedBookOutcome.StaleSkipped:
                    // A benign server race; the state is untouched.
                    break;

                case SequencedBookOutcome.GapDetected:
                    Logger.LogWarning(
                        "OKX order book gap for {Symbol} at seq={Sequence}; the state is discarded and the channel re-subscribed",
                        symbol.Normalized,
                        sequence);
                    _resubscribeQueue.Writer.TryWrite(symbol);
                    break;
            }
        }

        if (dropped > 0)
        {
            Logger.LogWarning(
                "{Dropped} of {Total} OKX order book frames for {Symbol} are malformed and are dropped",
                dropped,
                published + dropped,
                symbol.Normalized);
        }
    }

    /// <summary>
    /// Parse a public trade frame (an ARRAY of trade events): "side" is the
    /// aggressor side ("buy"/"sell"), "px"/"sz" the (string) numbers, "ts"
    /// the ms epoch, "tradeId" the trade id. Malformed entries are counted
    /// per frame and dropped — an unparsable side is an error, never a
    /// defaulted guess.
    /// </summary>
    private void HandleTradeFrames(JsonElement data)
    {
        var published = 0;
        var dropped = 0;
        Symbol? lastSymbol = null;

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

            var symbol = ParseWireSymbol(TryGetString(item, "instId"));
            if (symbol is null)
            {
                dropped++;
                continue;
            }

            lastSymbol = symbol;

            if (!item.TryGetProperty("side", out var sideProp) ||
                sideProp.GetString() is not (FrameSides.Buy or FrameSides.Sell) ||
                !TryGetNumberElement(item, "px", out var priceProp) ||
                !TryGetNumberElement(item, "sz", out var quantityProp) ||
                !TryGetNumberElement(item, "ts", out var timeProp))
            {
                dropped++;
                continue;
            }

            var side = sideProp.GetString();

            published++;
            PublishTrade(new MarketTrade
            {
                Id = TryGetString(item, "tradeId") ?? string.Empty,
                Symbol = symbol,
                Side = side == FrameSides.Sell ? OrderSide.Sell : OrderSide.Buy,
                Price = new Price(BinanceJsonNumbers.ParseDecimal(priceProp)),
                Quantity = new Quantity(BinanceJsonNumbers.ParseDecimal(quantityProp)),
                Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(BinanceJsonNumbers.ParseInt64(timeProp)),
            });
        }

        if (dropped > 0)
        {
            Logger.LogWarning(
                "{Dropped} of {Total} OKX public trades for {Symbol} are malformed and are dropped",
                dropped,
                published + dropped,
                lastSymbol?.Normalized);
        }
    }

    /// <summary>
    /// Parse a sequenced order book payload: the monotonic "seq" plus the two
    /// level arrays (each level is [px, sz, liqOrders, numOrders]). OKX
    /// always serves both sides; an absent side maps to an empty list ("no
    /// levels on that side this frame") — the no-op they are, never invented
    /// levels.
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

        if (!TryGetNumberElement(data, "seq", out var seqElement))
        {
            return false;
        }

        sequence = BinanceJsonNumbers.ParseInt64(seqElement);

        if (data.TryGetProperty("bids", out var bidsProp) && bidsProp.ValueKind == JsonValueKind.Array)
        {
            bids = ParsePriceLevels(bidsProp);
        }

        if (data.TryGetProperty("asks", out var asksProp) && asksProp.ValueKind == JsonValueKind.Array)
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

    private static string? TryGetString(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// Fetch a member that carries a numeric value in either JSON dialect
    /// (OKX v5 serves ALL numbers as strings; a defensive number kind is
    /// tolerated): the caller parses through the tolerant numeric reader,
    /// so no value is ever defaulted here.
    /// </summary>
    private static bool TryGetNumberElement(JsonElement item, string property, out JsonElement value)
    {
        if (item.TryGetProperty(property, out var element) &&
            element.ValueKind is JsonValueKind.String or JsonValueKind.Number)
        {
            value = element;
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>
    /// OKX v5 REST responses wrap the payload in {"code","msg","data"} where
    /// the code is a STRING: a non-zero "0" is a protocol-level failure (HTTP
    /// may still be 200) and throws — never a defaulted result. The "data"
    /// member is the value. The request rides the rate limiter first (T-106):
    /// the acquire waits out the bucket, and a 429 response with a
    /// <c>Retry-After</c> feeds the venue's park guidance back into the
    /// limiter before the failure throws.
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
            ?? throw new InvalidOperationException($"OKX REST request returned an empty body: {url}");

        var code = root.TryGetProperty("code", out var codeProp) && codeProp.ValueKind == JsonValueKind.String
            ? codeProp.GetString()
            : null;
        if (code is not null && code != "0")
        {
            var message = root.TryGetProperty("msg", out var msgProp) && msgProp.ValueKind == JsonValueKind.String
                ? msgProp.GetString()
                : null;
            throw new InvalidOperationException(
                $"OKX REST request failed: code={code}, msg={message} ({url})");
        }

        // The data element outlives this method: clone it out of the
        // document's pooled memory before the document is disposed.
        return root.GetProperty("data").Clone();
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

    /// <summary>
    /// The OKX bar grammar: the case IS the unit (minute lowercase, hour and
    /// month capital).
    /// </summary>
    private static string ConvertTimeFrame(TimeFrame timeFrame)
    {
        return timeFrame switch
        {
            TimeFrame.M1 => "1m",
            TimeFrame.M5 => "5m",
            TimeFrame.M15 => "15m",
            TimeFrame.M30 => "30m",
            TimeFrame.H1 => "1H",
            TimeFrame.H4 => "4H",
            TimeFrame.D1 => "1D",
            TimeFrame.W1 => "1W",
            TimeFrame.MN1 => "1M",
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
        await _pongLoop;
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
        [property: JsonPropertyName("args")] ChannelArg[] Args);

    private sealed record ChannelArg(
        [property: JsonPropertyName("channel")] string Channel,
        [property: JsonPropertyName("instId")] string InstId);

    private static class FrameOps
    {
        public const string Subscribe = "subscribe";
        public const string Unsubscribe = "unsubscribe";
    }

    private static class FrameActions
    {
        public const string Snapshot = "snapshot";
        public const string Update = "update";
    }

    private static class FrameSides
    {
        public const string Buy = "buy";
        public const string Sell = "sell";
    }

    #endregion
}
