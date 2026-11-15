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
/// Bybit v5 spot and linear-perp exchange adapter implementation (read-only
/// market data, T-197). Mirrors the Binance structure: a named HTTP client
/// for REST, one shared WebSocket PER CATEGORY — the v5 spot and linear
/// streams live on distinct hosts, so the adapter owns a two-socket
/// category context pair and routes every symbol by its settled category —
/// endpoints from the <see cref="ExchangeEndpointRegistry"/>. Wire facts
/// measured live on 2026-11-02 (public, key-less endpoints): the spot
/// catalog is one page of ~520 server-typed instruments
/// (<c>baseCoin</c>/<c>quoteCoin</c>/<c>status</c>), klines arrive
/// newest-first with all numbers as strings, and the stream is ONE
/// multiplexed socket answered in <c>{"topic",...,"data"}</c> frames where
/// spot tickers are snapshot-typed, <c>orderbook.50</c> deltas carry a
/// monotonic <c>u</c> (quantity <c>"0"</c> deletes a level) and Bybit
/// dislikes silence — hence the adapter-owned keepalive ping. The linear
/// ticker topic is DELTA-typed (the first snapshot, then field-scoped
/// deltas), so the linear context merges each frame into the symbol's last
/// known ticker state — funding rate and open interest included — and
/// publishes the merged result; a perp frame for an unsubscribed symbol is
/// dropped with a warning, because the wire text cannot carry the category
/// and a guessed category is never honest.
/// </summary>
public sealed class BybitAdapter : ExchangeAdapterBase, IMarketDataAdapter, IFundingHistoryProvider
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

    /// <summary>Production WebSocket URL of the v5 public linear-perp stream (T-197: Bybit keeps the categories on distinct hosts).</summary>
    public const string DefaultLinearWsUrl = "wss://stream.bybit.com/v5/public/linear";

    /// <summary>REST path of the symbol catalog.</summary>
    private const string CatalogRequestPath = "/v5/market/instruments-info";

    /// <summary>REST path of the kline history endpoint.</summary>
    private const string KlineRequestPath = "/v5/market/kline";

    /// <summary>REST path of the settled funding-rate history endpoint (T-240).</summary>
    private const string FundingHistoryRequestPath = "/v5/market/funding/history";

    /// <summary>The v5 REST category of the spot instruments (T-197's category-aware requests).</summary>
    private const string SpotCategory = "spot";

    /// <summary>The v5 REST category of the linear-perp instruments.</summary>
    private const string LinearCategory = "linear";

    /// <summary>The linear contract type the catalog serves (T-197: linear PERPETUALS only — dated futures stay out).</summary>
    private const string LinearPerpetualContractType = "LinearPerpetual";

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
            new RestEndpointBudget(FundingHistoryRequestPath, PublicRequestsPerSecond, RestRefillInterval),
        ],
    };

    /// <summary>Page size requested for the spot catalog (the server's own cap).</summary>
    private const int CatalogPageSize = 1000;

    /// <summary>The smallest funding-history page the endpoint accepts (the venue's documented bound).</summary>
    private const int FundingHistoryMinLimit = 1;

    /// <summary>The largest funding-history page the endpoint serves (the venue's documented cap; T-240).</summary>
    private const int FundingHistoryMaxLimit = 200;

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
    private readonly WebSocketClient _linearSocket;
    private readonly string _baseUrl;
    private readonly string _wsUrl;
    private readonly string _linearWsUrl;
    private readonly IRestRateLimiter _restLimiter;

    /// <summary>
    /// The spot category context: the spot socket, its subscribed symbols
    /// and its per-symbol book state machines (T-197: every category owns
    /// its context — the wires are distinct hosts and the exchange-format
    /// symbol text cannot tell the two apart).
    /// </summary>
    private readonly CategoryContext _spot;

    /// <summary>The linear-perp category context: the linear socket, its subscribed symbols, book state machines and the delta-merge ticker states.</summary>
    private readonly CategoryContext _linear;

    /// <summary>Serializes every outbound frame across concurrent stream starts, reconnect re-subscribes and the keepalive (both sockets).</summary>
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    /// <summary>
    /// Topic re-subscribe requests (a book gap or a fresh socket): each one
    /// lands in this queue and the ONE consumer task serves them FIFO,
    /// serialized through <see cref="_sendGate"/> and never fire-and-forget.
    /// </summary>
    private readonly Channel<Symbol> _resubscribeQueue = Channel.CreateUnbounded<Symbol>(
        new UnboundedChannelOptions { SingleReader = true });

    private readonly IDisposable _messageSubscription;
    private readonly IDisposable _connectionSubscription;
    private readonly IDisposable _linearMessageSubscription;
    private readonly IDisposable _linearConnectionSubscription;

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
        _linearWsUrl = ResolveLinearWsUrl(endpoints, _wsUrl);

        if (string.IsNullOrWhiteSpace(_baseUrl) || string.IsNullOrWhiteSpace(_wsUrl))
        {
            throw new ArgumentException(
                "Configured Bybit endpoints must carry non-empty RestBaseUrl and WsUrl",
                nameof(endpoints));
        }

        _httpClient = httpClientFactory.CreateClient(HttpClientName);
        _restLimiter = restLimiter ?? UnthrottledRestRateLimiter.Instance;

        // One venue health lane: both category sockets report through the
        // same adapter-id sink (the latest transition wins — the venue's
        // health is one honest surface, not two).
        var reconnectSink = reconnectSupervisor?.For(Id, DefaultReconnectPolicy);
        _wsClient = new WebSocketClient(_wsUrl, logger, DefaultReconnectPolicy, reconnectSink);
        _linearSocket = new WebSocketClient(_linearWsUrl, logger, DefaultReconnectPolicy, reconnectSink);
        _spot = new CategoryContext(_wsClient);
        _linear = new CategoryContext(_linearSocket);

        _messageSubscription = _wsClient.MessageStream.Subscribe(
            message => HandleWebSocketMessage(message, _spot));
        _connectionSubscription = _wsClient.ConnectionStateStream.Subscribe(
            isConnected => HandleConnectionStateChanged(isConnected, _spot, _wsUrl));
        _linearMessageSubscription = _linearSocket.MessageStream.Subscribe(
            message => HandleWebSocketMessage(message, _linear));
        _linearConnectionSubscription = _linearSocket.ConnectionStateStream.Subscribe(
            isConnected => HandleConnectionStateChanged(isConnected, _linear, _linearWsUrl));

        _keepaliveLoop = RunKeepaliveLoopAsync(_lifetimeCts.Token);
        _resubscribeLoop = RunResubscribeLoopAsync(_lifetimeCts.Token);
    }

    #endregion

    #region Public Methods

    /// <inheritdoc/>
    public override async Task ConnectAsync(CancellationToken ct = default)
    {
        await _wsClient.ConnectAsync(ct);
        await _linearSocket.ConnectAsync(ct);
        IsConnected = _wsClient.IsConnected || _linearSocket.IsConnected;
    }

    /// <inheritdoc/>
    public override async Task DisconnectAsync(CancellationToken ct = default)
    {
        await _wsClient.DisconnectAsync();
        await _linearSocket.DisconnectAsync();
        IsConnected = false;
    }

    /// <summary>
    /// Start the streams for one symbol on demand (called by the first
    /// subscriber of any feed): connect the symbol's CATEGORY socket once
    /// when not connected (T-197 — the spot and linear wires are distinct
    /// hosts), then subscribe the symbol's ticker, order book and trade
    /// topics on it. Serialized with the send gate so concurrent starts
    /// cannot interleave frames on either shared WebSocket. The caller's
    /// token covers the gate wait and the connect only: the SEND rides the
    /// adapter-owned lifetime — cancelling it mid-send would abort the
    /// shared socket for every feed (BUG-004).
    /// </summary>
    protected override async Task StartSymbolStreamsAsync(
        Symbol symbol,
        int? bookDepth,
        CancellationToken ct)
    {
        var context = ContextFor(symbol);
        await _sendGate.WaitAsync(ct);
        try
        {
            if (!context.Socket.IsConnected)
            {
                await context.Socket.ConnectAsync(ct);
            }

            await context.Socket.SendJsonAsync(
                new SubscriptionFrame(FrameOps.Subscribe, BuildTopics(symbol, bookDepth)),
                _lifetimeCts.Token);
            context.Subscribed[symbol.GetExchangeFormat(Id)] = symbol;
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <inheritdoc/>
    public override async Task<IReadOnlyList<Symbol>> GetSymbolsAsync(CancellationToken ct = default)
    {
        // The catalog carries BOTH categories (T-197): the spot pairs keep
        // their symbols and the linear-perp contracts join with their
        // settlement asset, so the palette and the panel surface can route
        // a perpetual on the same venue.
        var spot = await LoadCatalogAsync(SpotCategory, MapSpotCatalogRow, ct);
        var linear = await LoadCatalogAsync(LinearCategory, MapLinearCatalogRow, ct);
        return [.. spot, .. linear];
    }

    /// <summary>
    /// Load one category's instrument catalog: the cursor-paged
    /// instruments-info walk (bounded by the shipped page cap) over a
    /// category-specific row mapping. The mapping separates the two honest
    /// outcomes: a MALFORMED entry (blank server-served metadata) counts
    /// into the aggregated warning, an out-of-scope one (non-trading, a
    /// non-perpetual contract) is filtered silently — never guessed; the
    /// page cap stops a pathological (or hostile) pagination with one
    /// aggregated warning, never a silent truncation.
    /// </summary>
    private async Task<IReadOnlyList<Symbol>> LoadCatalogAsync(
        string category,
        Func<JsonElement, (Symbol? Symbol, bool Malformed)> mapRow,
        CancellationToken ct)
    {
        var symbols = new List<Symbol>();
        var dropped = 0;
        var pages = 0;
        var cursor = string.Empty;
        var truncated = false;

        do
        {
            var url = $"{_baseUrl}{CatalogRequestPath}?category={category}&limit={CatalogPageSize}";
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
                var (symbol, malformed) = mapRow(item);
                if (symbol is not null)
                {
                    symbols.Add(symbol);
                }
                else if (malformed)
                {
                    dropped++;
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
                "The Bybit {Category} catalog exceeded the shipped page cap ({MaxPages} pages of {PageSize}); the load stops with a partial catalog",
                category,
                MaxCatalogPages,
                CatalogPageSize);
        }

        if (dropped > 0)
        {
            Logger.LogWarning(
                "{Count} Bybit {Category} catalog entries arrived malformed and are dropped",
                dropped,
                category);
        }

        return symbols;
    }

    /// <summary>
    /// Map one spot instruments row: the split is server-served metadata —
    /// a blank split is the malformed outcome (counted), a non-trading
    /// status is the silent filter (the entry exists, it just does not
    /// serve).
    /// </summary>
    private static (Symbol? Symbol, bool Malformed) MapSpotCatalogRow(JsonElement item)
    {
        var baseCoin = TryGetString(item, "baseCoin");
        var quoteCoin = TryGetString(item, "quoteCoin");
        var status = TryGetString(item, "status");

        if (string.IsNullOrWhiteSpace(baseCoin) || string.IsNullOrWhiteSpace(quoteCoin))
        {
            return (null, true);
        }

        return status == "Trading"
            ? (new Symbol(baseCoin, quoteCoin, AssetClass.Crypto), false)
            : (null, false);
    }

    /// <summary>
    /// Map one linear instruments row: ONLY <c>LinearPerpetual</c> contracts
    /// serve (the dated linear futures stay out of scope, silently), the
    /// settlement coin names the category, and the same server-served split
    /// discipline applies — a blank split is the malformed outcome.
    /// </summary>
    private static (Symbol? Symbol, bool Malformed) MapLinearCatalogRow(JsonElement item)
    {
        var contractType = TryGetString(item, "contractType");
        if (contractType != LinearPerpetualContractType)
        {
            return (null, false);
        }

        var baseCoin = TryGetString(item, "baseCoin");
        var quoteCoin = TryGetString(item, "quoteCoin");
        var settleCoin = TryGetString(item, "settleCoin");
        var status = TryGetString(item, "status");

        if (string.IsNullOrWhiteSpace(baseCoin) ||
            string.IsNullOrWhiteSpace(quoteCoin) ||
            string.IsNullOrWhiteSpace(settleCoin))
        {
            return (null, true);
        }

        return status == "Trading"
            ? (new Symbol(baseCoin, quoteCoin, AssetClass.Crypto, settleCoin), false)
            : (null, false);
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
        var category = symbol.IsPerpetual ? LinearCategory : SpotCategory;

        var url = $"{_baseUrl}{KlineRequestPath}" +
                  $"?category={category}" +
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

    /// <inheritdoc/>
    public async Task<IReadOnlyList<FundingRatePoint>> GetFundingRateHistoryAsync(
        Symbol symbol,
        DateTimeOffset from,
        DateTimeOffset? to = null,
        int limit = 200,
        CancellationToken ct = default)
    {
        // Funding history is a derivatives wire: a spot symbol is a request
        // contract violation, refused — never guessed into another category.
        if (!symbol.IsPerpetual)
        {
            throw new ArgumentException(
                $"Funding-rate history serves perpetual contracts only; {symbol.Normalized} is not one.",
                nameof(symbol));
        }

        if (limit is < FundingHistoryMinLimit or > FundingHistoryMaxLimit)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limit),
                limit,
                $"The funding-history limit rides the venue's page bounds [{FundingHistoryMinLimit}..{FundingHistoryMaxLimit}].");
        }

        var startTime = from.ToUnixTimeMilliseconds();
        var endTime = to?.ToUnixTimeMilliseconds() ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var url = $"{_baseUrl}{FundingHistoryRequestPath}" +
                  $"?category={LinearCategory}" +
                  $"&symbol={symbol.GetExchangeFormat(Id)}" +
                  $"&startTime={startTime}" +
                  $"&endTime={endTime}" +
                  $"&limit={limit}";

        var result = await GetRestResultAsync(
            RestLane.Background,
            FundingHistoryRequestPath,
            CatalogPageWeight,
            url,
            ct);
        var rows = result.GetProperty("list");

        // Bybit v5 funding rows: {"symbol","fundingRate","fundingRateTimestamp"}
        // with BOTH data fields as strings (the timestamp in milliseconds),
        // NEWEST FIRST — same as the kline wire.
        var points = new List<FundingRatePoint>(rows.GetArrayLength());
        foreach (var row in rows.EnumerateArray())
        {
            points.Add(new FundingRatePoint(
                DateTimeOffset.FromUnixTimeMilliseconds(BinanceJsonNumbers.ParseInt64(row.GetProperty("fundingRateTimestamp"))),
                BinanceJsonNumbers.ParseDecimal(row.GetProperty("fundingRate"))));
        }

        // The series contract is ascending; reverse the newest-first order
        // in place.
        points.Reverse();

        return points;
    }

    /// <summary>
    /// Subscribe to ticker, 50-level order book and public trades for a
    /// symbol (one multiplexed frame; the server answers one ack per frame)
    /// on the symbol's category socket.
    /// </summary>
    public async Task SubscribeSymbolAsync(Symbol symbol, CancellationToken ct = default)
    {
        var context = ContextFor(symbol);
        await _sendGate.WaitAsync(ct);
        try
        {
            await context.Socket.SendJsonAsync(
                new SubscriptionFrame(FrameOps.Subscribe, BuildTopics(symbol)),
                ct);
            context.Subscribed[symbol.GetExchangeFormat(Id)] = symbol;
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <summary>
    /// Unsubscribe from all topics for a symbol on its category socket and
    /// drop its book and ticker-merge state.
    /// </summary>
    public async Task UnsubscribeSymbolAsync(Symbol symbol, CancellationToken ct = default)
    {
        var context = ContextFor(symbol);
        await _sendGate.WaitAsync(ct);
        try
        {
            await context.Socket.SendJsonAsync(
                new SubscriptionFrame(FrameOps.Unsubscribe, BuildTopics(symbol)),
                ct);
            context.Subscribed.TryRemove(symbol.GetExchangeFormat(Id), out _);
            context.Books.TryRemove(symbol.Normalized, out _);
            context.TickerStates.TryRemove(symbol.Normalized, out _);
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
    /// Re-send the topic subscription frames for one symbol on its category
    /// socket (gap recovery or a fresh socket). The set membership is kept
    /// by the regular subscribe paths, so this send-only path cannot
    /// resurrect a stale entry.
    /// </summary>
    private async Task ResubscribeStreamsAsync(Symbol symbol, CancellationToken ct)
    {
        var context = ContextFor(symbol);
        await _sendGate.WaitAsync(ct);
        try
        {
            await context.Socket.SendJsonAsync(
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
    /// Re-subscribe every tracked symbol of ONE category after that
    /// category's reconnection: the server holds no session state across
    /// sockets, so a silent reconnection would otherwise keep subscriptions
    /// "live" while no data flows (the first connect skips this path — the
    /// tracked set is still empty and the per-symbol start subscribes on
    /// its own). The venue's connected flag is the EITHER-state: either
    /// category socket live keeps the adapter connected.
    /// </summary>
    private void HandleConnectionStateChanged(bool isConnected, CategoryContext context, string socketUrl)
    {
        IsConnected = _wsClient.IsConnected || _linearSocket.IsConnected;
        if (isConnected != context.LastLoggedConnectionState)
        {
            context.LastLoggedConnectionState = isConnected;
            Logger.LogInformation(
                "Bybit WebSocket ({Category}) connection state: {State} at {Url}",
                ReferenceEquals(context, _spot) ? SpotCategory : LinearCategory,
                isConnected ? "Connected" : "Disconnected",
                socketUrl);
        }

        if (isConnected && context.Subscribed.IsEmpty)
        {
            return;
        }

        if (!isConnected)
        {
            return;
        }

        foreach (var symbol in context.Subscribed.Values)
        {
            _resubscribeQueue.Writer.TryWrite(symbol);
        }
    }

    /// <summary>
    /// The adapter-owned keepalive: Bybit disconnects idle public sockets,
    /// while the infrastructure has no app-level ping — so the adapter sends
    /// an <c>"op":"ping"</c> frame every <see cref="KeepaliveIntervalSeconds"/>
    /// to each connected category socket on a PeriodicTimer owned by
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
                foreach (var (context, label) in new[] { (_spot, SpotCategory), (_linear, LinearCategory) })
                {
                    if (!context.Socket.IsConnected)
                    {
                        continue;
                    }

                    try
                    {
                        await _sendGate.WaitAsync(ct);
                        try
                        {
                            await context.Socket.SendAsync(KeepalivePingFrame, ct);
                        }
                        finally
                        {
                            _sendGate.Release();
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // One failed ping never kills the heartbeat: the
                        // socket's own error handling drives the reconnection.
                        Logger.LogWarning(ex, "Bybit keepalive ping failed ({Category})", label);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Disposal: the only exit of the keepalive loop.
        }
    }

    /// <summary>
    /// Bybit v5 frame dispatch for ONE category socket: data frames carry a
    /// "topic", control frames (subscribe/unsubscribe/ping acks) do not —
    /// errors from one message never leave the handler.
    /// </summary>
    private void HandleWebSocketMessage(string message, CategoryContext context)
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

            // The book stamp is the frame's local receive moment (T-142):
            // captured once at the ingestion point, never the exchange's
            // server ts — the WS-latency histogram measures transport, not
            // clock offset. The ticker keeps the exchange ts (an event fact).
            var receivedAt = DateTimeOffset.UtcNow;

            var timestamp = root.TryGetProperty("ts", out var tsProp) &&
                            tsProp.ValueKind == JsonValueKind.Number
                ? DateTimeOffset.FromUnixTimeMilliseconds(tsProp.GetInt64())
                : receivedAt;

            if (topic.StartsWith(OrderBookTopicPrefix, StringComparison.Ordinal))
            {
                HandleOrderBookFrame(topic, type, data, receivedAt, context);
            }
            else if (topic.StartsWith(TickerTopicPrefix, StringComparison.Ordinal))
            {
                // The spot tickers are snapshot-typed (live-verified; the
                // full payload is parsed as-is) while the linear tickers are
                // delta-typed (the first snapshot, then field-scoped deltas)
                // — each category merges through its own path.
                HandleTickerFrame(topic, data, timestamp, context);
            }
            else if (topic.StartsWith(PublicTradeTopicPrefix, StringComparison.Ordinal))
            {
                HandlePublicTradeFrame(topic, data, context);
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
    /// Map a ticker frame on its category's terms: the SPOT wire is
    /// snapshot-typed — the price journey is lastPrice only (the v5 spot
    /// tickers carry no bid/ask fields, live-verified) and
    /// <c>price24hPcnt</c> is a fraction mapped into the percent basis the
    /// Binance ticker already ships. The LINEAR wire is delta-typed: each
    /// frame merges into the symbol's last known state (funding rate, open
    /// interest and the next funding moment included) and the merged result
    /// publishes — an absent field keeps its previous value, never a
    /// fabricated zero, and nothing publishes before the wire ever said a
    /// last price.
    /// </summary>
    private void HandleTickerFrame(string topic, JsonElement data, DateTimeOffset timestamp, CategoryContext context)
    {
        var symbol = ResolveFrameSymbol(GetTopicSymbol(topic), context);
        if (symbol is null)
        {
            Logger.LogWarning("Bybit tickers topic {Topic}: unrecognised symbol format", topic);
            return;
        }

        if (!ReferenceEquals(context, _spot))
        {
            PublishLinearTicker(symbol, data, timestamp);
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
    /// Merge one linear ticker frame into the symbol's delta state and
    /// publish the merged ticker. A present-and-parseable field updates the
    /// state; a present-but-blank or absent field keeps the previous value;
    /// nothing publishes until the wire has said a last price (a frame
    /// before the first snapshot is not a ticker yet).
    /// </summary>
    private void PublishLinearTicker(Symbol symbol, JsonElement data, DateTimeOffset timestamp)
    {
        var state = _linear.TickerStates.GetOrAdd(symbol.Normalized, _ => new LinearTickerState());

        if (TryGetDecimal(data, "lastPrice") is { } lastPrice)
        {
            state.LastPrice = lastPrice;
            state.HasLastPrice = true;
        }

        if (TryGetDecimal(data, "volume24h") is { } volume)
        {
            state.Volume24h = volume;
        }

        if (TryGetDecimal(data, "price24hPcnt") is { } fraction)
        {
            state.PriceChange24hPercent = fraction * 100m;
        }

        if (TryGetDecimal(data, "fundingRate") is { } fundingRate)
        {
            state.FundingRate = fundingRate;
        }

        if (TryGetDecimal(data, "openInterest") is { } openInterest)
        {
            state.OpenInterest = openInterest;
        }

        if (TryGetUnixMs(data, "nextFundingTime") is { } nextFunding)
        {
            state.NextFundingTime = nextFunding;
        }

        if (!state.HasLastPrice)
        {
            Logger.LogWarning(
                "Bybit linear ticker for {Symbol}: no last price has arrived yet, the frame is not published",
                symbol.Normalized);
            return;
        }

        PublishTicker(new Ticker(
            symbol,
            state.LastPrice,
            0m,
            0m,
            state.Volume24h,
            state.PriceChange24hPercent,
            timestamp,
            state.FundingRate,
            state.OpenInterest,
            state.NextFundingTime));
    }

    /// <summary>
    /// Read one optional decimal wire field: a present-and-parseable value
    /// returns it; an absent, non-string or blank field returns null (the
    /// merge keeps the previous value — never a fabricated zero).
    /// </summary>
    private static decimal? TryGetDecimal(JsonElement data, string property)
    {
        if (!data.TryGetProperty(property, out var value) ||
            value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text) ||
            !decimal.TryParse(text, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            return null;
        }

        return parsed;
    }

    /// <summary>
    /// Read one optional epoch-milliseconds wire field: a present-and-
    /// parseable value returns its moment; an absent, non-string or blank
    /// field returns null (the merge keeps the previous value).
    /// </summary>
    private static DateTimeOffset? TryGetUnixMs(JsonElement data, string property)
    {
        if (!data.TryGetProperty(property, out var value) ||
            value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text) || !long.TryParse(text, out var unixMs))
        {
            return null;
        }

        return DateTimeOffset.FromUnixTimeMilliseconds(unixMs);
    }

    /// <summary>
    /// Route an <c>orderbook.{depth}</c> frame into the per-symbol state
    /// machine: snapshots rebuild, deltas continue the sequence, a gap
    /// discards the state and re-subscribes the topic (via the serialized
    /// re-subscribe queue, never fire-and-forget). Stale deltas are the
    /// server's own races and are skipped silently — logging them would
    /// drown the real warnings at the 100 ms stream rate. The rebuilt book
    /// carries the frame's local receive moment (T-142's one clock
    /// discipline), never the exchange's server ts.
    /// </summary>
    private void HandleOrderBookFrame(string topic, string? type, JsonElement data, DateTimeOffset receivedAt, CategoryContext context)
    {
        var symbol = ResolveFrameSymbol(GetTopicSymbol(topic), context);
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

        var assembler = context.Books.GetOrAdd(
            symbol.Normalized,
            _ => new SequencedOrderBookAssembler(symbol, DepthLevelCount));

        var apply = type == FrameTypes.Snapshot
            ? assembler.ApplySnapshot(sequence, bids, asks, receivedAt)
            : assembler.ApplyDelta(sequence, bids, asks, receivedAt);

        switch (apply.Outcome)
        {
            case SequencedBookOutcome.Applied:
                PublishOrderBook(apply.Book!);
                break;

            case SequencedBookOutcome.StaleSkipped:
                // A benign server race; the state is untouched.
                break;

            case SequencedBookOutcome.GapDetected:
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
    private void HandlePublicTradeFrame(string topic, JsonElement data, CategoryContext context)
    {
        var symbol = ResolveFrameSymbol(GetTopicSymbol(topic), context);
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

    /// <summary>
    /// The category context a symbol rides: a settled (perpetual) symbol
    /// lives on the linear wires, everything else on the spot ones (T-197).
    /// </summary>
    private CategoryContext ContextFor(Symbol symbol) =>
        symbol.IsPerpetual ? _linear : _spot;

    /// <summary>
    /// Resolve a frame's exchange-format symbol text through its socket's
    /// subscribed set. The spot wires' text parses on its own (the legacy
    /// fallback), but the derivatives text CANNOT carry the category — a
    /// linear frame for an unsubscribed symbol is dropped with a warning,
    /// never guessed into a spot symbol.
    /// </summary>
    private Symbol? ResolveFrameSymbol(string exchangeText, CategoryContext context)
    {
        if (context.Subscribed.TryGetValue(exchangeText, out var subscribed))
        {
            return subscribed;
        }

        return ReferenceEquals(context, _spot) ? ParseExchangeSymbol(exchangeText) : null;
    }

    /// <summary>
    /// The linear socket URL (T-197): an explicit configuration wins; a
    /// blank one derives from the spot URL's <c>/spot</c> tail (the testnet
    /// lane keeps its host), and a URL without that tail falls back to the
    /// shipped production linear host.
    /// </summary>
    private static string ResolveLinearWsUrl(BybitEndpoints? endpoints, string spotWsUrl)
    {
        var configured = endpoints?.LinearWsUrl;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        return spotWsUrl.EndsWith("/spot", StringComparison.OrdinalIgnoreCase)
            ? $"{spotWsUrl[..^5]}/linear"
            : DefaultLinearWsUrl;
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
        _linearMessageSubscription.Dispose();
        _linearConnectionSubscription.Dispose();

        await base.DisposeAsync();
        await _wsClient.DisposeAsync();
        await _linearSocket.DisposeAsync();
        _sendGate.Dispose();
        // HttpClient comes from IHttpClientFactory; its lifetime is managed there.
    }

    #endregion

    #region Nested Types

    /// <summary>
    /// One category's shared wire state (T-197): the category's socket, the
    /// symbols subscribed on it (keyed by the exchange-format text — a
    /// category cannot hold two symbols with the same wire text), the
    /// per-symbol book state machines (keyed by the normalized symbol, which
    /// DOES tell the categories apart) and the linear delta-merge ticker
    /// states (unused on the spot wire, whose tickers are snapshot-typed).
    /// </summary>
    private sealed class CategoryContext(WebSocketClient socket)
    {
        /// <summary>The category's multiplexed public socket.</summary>
        public WebSocketClient Socket { get; } = socket;

        /// <summary>Currently subscribed symbols keyed by the exchange-format name (the reconnect re-subscribe set).</summary>
        public ConcurrentDictionary<string, Symbol> Subscribed { get; } = new(StringComparer.Ordinal);

        /// <summary>Order book state machines keyed by the normalized symbol.</summary>
        public ConcurrentDictionary<string, SequencedOrderBookAssembler> Books { get; } = new();

        /// <summary>The linear tickers' delta-merge states keyed by the normalized symbol.</summary>
        public ConcurrentDictionary<string, LinearTickerState> TickerStates { get; } = new();

        /// <summary>
        /// The category's last logged connection state: a connection-state line is a TRANSITION
        /// (T-233) — the WS client's subject seeds the pre-dial <c>false</c> at subscription, and
        /// logging that sync would print this category's real endpoint into a session that never
        /// dialed (the T-141 isolation census's property).
        /// </summary>
        public bool LastLoggedConnectionState;
    }

    /// <summary>
    /// One linear symbol's last known ticker facts (T-197): the delta-typed
    /// wire sends field-scoped updates, so the merge keeps the previous
    /// value of every field a frame does not carry. Mutable by design — the
    /// merge owns it; the published <see cref="Ticker"/> stays immutable.
    /// </summary>
    private sealed class LinearTickerState
    {
        /// <summary>The last seen last price (the publish gate rides it: nothing publishes before the wire said a price).</summary>
        public decimal LastPrice;

        /// <summary>Whether the wire has said a last price yet.</summary>
        public bool HasLastPrice;

        public decimal Volume24h;

        public decimal PriceChange24hPercent;

        /// <summary>The last seen funding rate, or null while the wire has not said one.</summary>
        public decimal? FundingRate;

        /// <summary>The last seen open interest, or null while the wire has not said one.</summary>
        public decimal? OpenInterest;

        /// <summary>The last seen next-funding moment, or null while the wire has not said one.</summary>
        public DateTimeOffset? NextFundingTime;
    }

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
