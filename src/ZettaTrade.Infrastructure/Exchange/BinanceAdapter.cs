namespace ZettaTrade.Infrastructure.Exchange;

using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Reactive.Linq;
using System.Reactive.Subjects;
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
/// Binance exchange adapter implementation (read-only market data).
/// Creates its own WebSocket transport; HTTP clients come from IHttpClientFactory.
/// <para>
/// The T-242 liquidation feed rides a SECOND, dedicated socket on the
/// futures combined-stream host: the venue wires forced liquidations only on
/// its derivatives wire, the all-market <c>!forceOrder@arr</c> stream is one
/// subscription for every contract, and its lazy lifecycle is the tracked
/// contracts' own — the socket dials on the first liquidation subscriber and
/// drops when the last one releases, while <see cref="ConnectAsync"/> and
/// <see cref="DisconnectAsync"/> keep governing only the market-data wire.
/// </para>
/// </summary>
public sealed class BinanceAdapter : ExchangeAdapterBase, IMarketDataAdapter, ILiquidationFeedProvider, ILeverageBracketsProvider
{
    #region Properties

    /// <summary>Named HttpClientFactory client used for REST requests.</summary>
    public const string HttpClientName = "binance";

    /// <summary>Timeout for REST requests, in seconds.</summary>
    public const int RequestTimeoutSeconds = 30;

    /// <summary>Production REST API base URL.</summary>
    public const string DefaultRestBaseUrl = "https://api.binance.com";

    // The combined endpoint wraps every frame as {"stream":...,"data":...};
    // the message handler expects that envelope (raw /ws frames lack it).
    /// <summary>Production WebSocket URL of the combined stream endpoint.</summary>
    public const string DefaultWsUrl = "wss://stream.binance.com:9443/stream";

    /// <summary>
    /// Production WebSocket URL of the futures combined-stream host: the
    /// derivatives wire the forced-liquidation stream rides (T-242). The
    /// spot wire carries no liquidation data at any price — a liquidation
    /// lane pointing at the spot host is a configuration error, so it owns
    /// its own default.
    /// </summary>
    public const string DefaultFuturesWsUrl = "wss://fstream.binance.com:443/stream";

    /// <summary>
    /// Production REST base of the futures host: the derivatives REST the
    /// leverage-bracket table rides (T-243). The spot REST carries no risk
    /// table at any price — a bracket lane pointing at the spot REST is a
    /// configuration error, so it owns its own default.
    /// </summary>
    public const string DefaultFuturesRestUrl = "https://fapi.binance.com";

    /// <summary>The v5-era all-market forced-liquidation stream name (one subscription serves every contract; the events carry their own symbol).</summary>
    private const string LiquidationStreamName = "!forceOrder@arr";

    /// <summary>The futures REST path of the public leverage-bracket table (T-243).</summary>
    private const string LeverageBracketRequestPath = "/fapi/v1/leverageBracket";

    /// <summary>Number of levels in the partial book depth snapshot.</summary>
    private const int DepthLevelCount = 20;

    /// <summary>Partial book depth update period in milliseconds.</summary>
    private const int DepthUpdatePeriodMs = 100;

    /// <summary>
    /// The partial book depth counts the exchange serves as streams
    /// (<c>@depth5</c>, <c>@depth10</c>, <c>@depth20</c>); any other request
    /// is rejected by the base subscription path, never silently defaulted.
    /// </summary>
    private static readonly IReadOnlySet<int> SupportedOrderBookDepths = new HashSet<int> { 5, 10, 20 };

    /// <summary>
    /// REST path of the symbol catalog: the priced-symbols list (~160 KB) instead
    /// of the full <c>exchangeInfo</c> document (~17 MB) — the multi-megabyte body
    /// delayed the first symbols apply by seconds on cold start (T-021 measurement),
    /// while the full per-symbol metadata (status, filters) is not needed anywhere.
    /// Priced spot symbols approximate "status == TRADING" closely (an unlisted
    /// pair has no price tick and is absent).
    /// </summary>
    private const string SymbolsRequestPath = "/api/v3/ticker/price";

    /// <summary>REST path of the kline (candle) series, the T-086 backfill's segment fetcher.</summary>
    private const string KlinesRequestPath = "/api/v3/klines";

    /// <summary>The request weight the venue documents for an unsymbol'd priced-symbols REST call.</summary>
    private const int SymbolsRequestWeight = 4;

    /// <summary>The request weight the venue documents for the symbol-filtered leverage-bracket call (T-243).</summary>
    private const int LeverageBracketRequestWeight = 1;

    /// <summary>The request weight the venue documents for the lightest kline slice (limit up to 100 bars).</summary>
    private const int KlinesBaseWeight = 2;

    /// <summary>
    /// Quote assets Binance spot symbols are denominated in. The static array is
    /// sorted LONGEST FIRST at construction, so suffix collisions split by the
    /// suffix-first rule (USDC before USD) without anyone keeping literals in
    /// order. The table lives in the adapter, not <c>Symbol</c> (whose list is
    /// cross-exchange conservative): it is Binance-specific and rotates when
    /// Binance rotates quote currencies — and a missed quote only drops that
    /// pair from the catalog (counted and logged once, never a wrong split).
    /// </summary>
    private static readonly string[] BinanceQuoteAssets =
        SortBinanceQuoteAssets(
        [
            "USDT", "FDUSD", "USDC", "BUSD", "TUSD", "USDP", "USDS",
            "USD1", "BTC", "ETH", "BNB", "TRX", "XRP", "DOGE", "SOL", "SUI",
            "TRY", "EUR", "GBP", "AED", "KZT", "USD", "AUD", "BRL", "JPY", "KRW",
            "RUB", "PLN", "RON", "UAH", "ARS", "MXN", "CLP", "COP",
            "CZK", "DOP", "IDR", "ILS", "INR", "NGN", "NZD", "PHP",
            "SAR", "SEK", "SYP", "THB", "TZS", "UGX", "VND", "XAF",
            "XOF", "ZAR", "DAI", "U",
        ]);

    /// <summary>
    /// Put the quote table in match order: LONGEST entries first, so the
    /// suffix scan never lets a shorter prefix win over a longer literal.
    /// </summary>
    private static string[] SortBinanceQuoteAssets(string[] assets) =>
        assets
            .OrderByDescending(asset => asset.Length)
            .ThenBy(asset => asset, StringComparer.Ordinal)
            .ToArray();

    /// <inheritdoc/>
    public override string Id => "binance";

    /// <inheritdoc/>
    public override string Name => "Binance";

    /// <inheritdoc/>
    public override ExchangeCapabilities Capabilities =>
        ExchangeCapabilities.MarketData | ExchangeCapabilities.OrderBook | ExchangeCapabilities.HistoricalData;

    /// <summary>Configured REST base URL (internal: the registration tests assert it, no network).</summary>
    internal string RestBaseUrl => _baseUrl;

    /// <summary>Configured combined WS URL (internal: the registration tests assert it).</summary>
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

    /// <summary>
    /// The kline weight tiers the limiter meters (the venue documents the
    /// schedule per limit window). The mapping delegates confidence to the
    /// venue's own published schedule as of the shipped alpha cut and stays
    /// rescalable through <c>Limiter.Binance.RequestWeightMinute</c> at
    /// runtime.
    /// </summary>
    private const int KlinesTierLimit = 100;

    /// <summary>The second tier's window bound (bars per request).</summary>
    private const int KlinesMidTierLimit = 500;

    /// <summary>The venue's kline request cap; wider requests overestimate to the top tier (the safe metering direction).</summary>
    private const int KlinesTopTierLimit = 1000;

    /// <summary>The kline slice of the venue's per-minute allowance (weight tokens).</summary>
    private const int KlinesMinuteBudget = 3600;

    /// <summary>The priced-symbols slice of the venue's per-minute allowance.</summary>
    private const int SymbolsMinuteBudget = 2400;

    /// <summary>
    /// The kline request weight by limit window: the venue's published tier
    /// schedule, each heavier slice costing more of the bucket. A limit
    /// beyond the venue's cap overestimates to the top tier — the safe
    /// metering direction for an out-of-range ask.
    /// </summary>
    /// <param name="limit">The requested bar count.</param>
    /// <returns>The weight the request spends from its bucket.</returns>
    private static int KlineWeightFor(int limit) => limit switch
    {
        <= KlinesTierLimit => KlinesBaseWeight,
        <= KlinesMidTierLimit => 2 * KlinesBaseWeight,
        <= KlinesTopTierLimit => 3 * KlinesBaseWeight,
        _ => 3 * KlinesBaseWeight,
    };

    /// <summary>
    /// The shipped REST budget manifest (T-106): the endpoint families this
    /// adapter serves, budgeted from the venue's published per-IP, per-minute
    /// weight allowance (6,000) and split per family so one hot family cannot
    /// eat another's budget. It rides the <c>Limiter.Binance.*</c> settings
    /// keys (see <see cref="RestRateLimiterOptions"/>' remarks) live.
    /// </summary>
    public static RestRateLimiterOptions DefaultRestLimits { get; } = new()
    {
        SettingsKey = "Binance",
        ScaleKind = RestBudgetScaleKind.WeightPerMinute,
        Endpoints =
        [
            new RestEndpointBudget(KlinesRequestPath, KlinesMinuteBudget, TimeSpan.FromMinutes(1)),
            new RestEndpointBudget(SymbolsRequestPath, SymbolsMinuteBudget, TimeSpan.FromMinutes(1)),
        ],
    };

    private readonly HttpClient _httpClient;
    private readonly WebSocketClient _wsClient;
    private readonly string _baseUrl;
    private readonly string _wsUrl;
    private readonly string _futuresWsUrl;

    /// <summary>The futures REST base the leverage-bracket table rides (T-243).</summary>
    private readonly string _futuresRestUrl;
    private readonly IRestRateLimiter _restLimiter;
    private readonly IDisposable _messageSubscription;
    private readonly IDisposable _connectionSubscription;

    /// <summary>
    /// The dedicated liquidation socket (T-242): the forced-liquidation
    /// stream rides the futures combined-stream host on a wire of its own —
    /// the market-data wire's subscription state stays honest and an idle
    /// market-data session never shares a socket with the liquidation lane.
    /// LIFECYCLE: the socket is lazy and ref-count OWNED — it dials on the
    /// first liquidation subscriber and disconnects when the last one
    /// releases; <see cref="ConnectAsync"/> and <see cref="DisconnectAsync"/>
    /// govern only the market-data wire.
    /// </summary>
    private readonly WebSocketClient _liquidationSocket;

    /// <summary>The liquidation lane's tracked contracts, keyed by the wire text.</summary>
    private readonly ConcurrentDictionary<string, TrackedLiquidation> _liquidationTracked = new(StringComparer.Ordinal);

    private readonly IDisposable _liquidationMessageSubscription;
    private readonly IDisposable _liquidationConnectionSubscription;

    /// <summary>
    /// The liquidation wire's last logged connection state: a
    /// connection-state line is a TRANSITION (T-233) — the WS client's
    /// subject seeds the pre-dial <c>false</c> at subscription, and logging
    /// that sync would print the real endpoint into a session that never
    /// dialed (the T-141 isolation census's property).
    /// </summary>
    private bool _liquidationLastLoggedConnectionState;

    /// <summary>
    /// Serializes on-demand stream starts, the public subscribe/unsubscribe
    /// API and the reconnect re-subscribes across concurrent callers: it is
    /// THE one send gate of the shared socket, since a second overlapping
    /// <c>ClientWebSocket.SendAsync</c> throws (BUG-028).
    /// </summary>
    private readonly SemaphoreSlim _streamStartGate = new(1, 1);

    /// <summary>
    /// Currently tracked symbols with the book depth their streams were
    /// requested at: the reconnect re-subscribe set (the server holds no
    /// session state across sockets).
    /// </summary>
    private readonly ConcurrentDictionary<string, TrackedSubscription> _subscribedSymbols = new();

    /// <summary>
    /// Wire requests (a fresh socket, a liquidation lane's lifecycle): each
    /// one lands in this queue and the ONE consumer task serves them FIFO,
    /// serialized through <see cref="_streamStartGate"/> and never
    /// fire-and-forget. The request discriminates the KIND — a market-data
    /// re-subscribe, a liquidation re-subscribe or a liquidation release —
    /// so one queue serves both wires' lifecycles.
    /// </summary>
    private readonly Channel<ResubscribeRequest> _resubscribeQueue = Channel.CreateUnbounded<ResubscribeRequest>(
        new UnboundedChannelOptions { SingleReader = true });

    /// <summary>
    /// The queue's one consumer task: entries are served strictly one at a
    /// time, so an in-flight re-subscribe send is NEVER cancelled by a newer
    /// request — a cancelled send aborts the whole socket and silences every
    /// feed behind the swap (BUG-004). Awaited on disposal, never abandoned.
    /// </summary>
    private readonly Task _resubscribeLoop;

    /// <summary>Adapter-owned lifetime for the re-subscribe consumer and the socket sends it runs.</summary>
    private readonly CancellationTokenSource _lifetimeCts = new();
    private int _disposed;

    /// <summary>Last WS request id (the WS API echoes it back per request).</summary>
    private int _lastRequestId;

    #endregion

    #region Constructor

    /// <summary>
    /// Create a new BinanceAdapter instance. Endpoints come from the
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
    public BinanceAdapter(
        ILogger<BinanceAdapter> logger,
        IHttpClientFactory httpClientFactory,
        BinanceEndpoints? endpoints = null,
        ReconnectSupervisor? reconnectSupervisor = null,
        IRestRateLimiter? restLimiter = null)
        : base(logger)
    {
        _baseUrl = endpoints?.RestBaseUrl ?? DefaultRestBaseUrl;
        _wsUrl = endpoints?.WsUrl ?? DefaultWsUrl;
        _futuresWsUrl = ResolveFuturesWsUrl(endpoints);
        _futuresRestUrl = ResolveFuturesRestUrl(endpoints);

        if (string.IsNullOrWhiteSpace(_baseUrl) || string.IsNullOrWhiteSpace(_wsUrl))
        {
            throw new ArgumentException(
                "Configured Binance endpoints must carry non-empty RestBaseUrl and WsUrl",
                nameof(endpoints));
        }

        _httpClient = httpClientFactory.CreateClient(HttpClientName);
        _restLimiter = restLimiter ?? UnthrottledRestRateLimiter.Instance;
        _wsClient = new WebSocketClient(
            _wsUrl,
            logger,
            DefaultReconnectPolicy,
            reconnectSupervisor?.For(Id, DefaultReconnectPolicy));
        _liquidationSocket = new WebSocketClient(
            _futuresWsUrl,
            logger,
            DefaultReconnectPolicy,
            reconnectSupervisor?.For($"{Id}-liquidation", DefaultReconnectPolicy));

        _messageSubscription = _wsClient.MessageStream.Subscribe(HandleWebSocketMessage);
        _connectionSubscription = _wsClient.ConnectionStateStream.Subscribe(HandleConnectionStateChanged);
        _liquidationMessageSubscription = _liquidationSocket.MessageStream.Subscribe(HandleLiquidationSocketMessage);
        _liquidationConnectionSubscription = _liquidationSocket.ConnectionStateStream.Subscribe(
            HandleLiquidationConnectionStateChanged);

        _resubscribeLoop = RunResubscribeLoopAsync(_lifetimeCts.Token);
    }

    #endregion

    #region Public Methods

    /// <inheritdoc/>
    public override async Task ConnectAsync(CancellationToken ct = default)
    {
        await _wsClient.ConnectAsync(ct);
        IsConnected = _wsClient.IsConnected || _liquidationSocket.IsConnected;
    }

    /// <inheritdoc/>
    public override async Task DisconnectAsync(CancellationToken ct = default)
    {
        await _wsClient.DisconnectAsync();
        // The liquidation lane's socket is ref-count owned (its subscribers
        // dial and release it): this call disconnects only the market-data
        // wire, and the aggregate reads the truth — a live liquidation
        // session keeps the honest connected state.
        IsConnected = _wsClient.IsConnected || _liquidationSocket.IsConnected;
    }

    /// <summary>
    /// Start the streams for one symbol on demand (called by the first
    /// subscriber of any feed): connect once when not connected, then
    /// subscribe the symbol's ticker, partial order book and trade streams.
    /// Serialized with the gate so concurrent callers cannot interleave
    /// frames on the shared WebSocket. The caller's token covers the gate
    /// wait and the connect only: the SEND rides the adapter-owned lifetime —
    /// cancelling it mid-send would abort the shared socket and leave every
    /// feed silent behind the swap (BUG-004).
    /// </summary>
    protected override async Task StartSymbolStreamsAsync(
        Symbol symbol,
        int? bookDepth,
        CancellationToken ct)
    {
        await _streamStartGate.WaitAsync(ct);
        try
        {
            if (!_wsClient.IsConnected)
            {
                await ConnectAsync(ct);
            }

            var depth = bookDepth ?? CurrentBookDepth(symbol);
            await _wsClient.SendJsonAsync(
                new SubscriptionRequest(
                    MethodNames.Subscribe,
                    NextRequestId(),
                    BuildStreamNames(symbol, depth)),
                _lifetimeCts.Token);
            _subscribedSymbols[symbol.Normalized] = new TrackedSubscription(symbol, depth);
        }
        finally
        {
            _streamStartGate.Release();
        }
    }

    /// <inheritdoc/>
    public override async Task<IReadOnlyList<Symbol>> GetSymbolsAsync(CancellationToken ct = default)
    {
        var response = await SendRateLimitedGetAsync(
            RestLane.Background,
            SymbolsRequestPath,
            SymbolsRequestWeight,
            $"{_baseUrl}{SymbolsRequestPath}",
            ct);
        using (response)
        {
            response.EnsureSuccessStatusCode();
            var priced = await response.Content.ReadFromJsonAsync<JsonElement[]>(options: null, cancellationToken: ct) ?? [];

            var symbols = new List<Symbol>();
            var unrecognized = 0;

            foreach (var item in priced)
            {
                var name = item.GetProperty("symbol").GetString()!;
                var split = SplitBinanceName(name);

                if (split is { } pair)
                {
                    symbols.Add(new Symbol(pair.Base, pair.Quote, AssetClass.Crypto));
                }
                else
                {
                    unrecognized++;
                }
            }

            if (unrecognized > 0)
            {
                // The routine data-shape cargo (T-231): the venue's full
                // spot list always carries quote assets this terminal does
                // not track (a stable standing fact — the same count every
                // launch), so the drop rides the debug level. The load's
                // symbol summary stays the catalog's only production trace.
                Logger.LogDebug(
                    "{Count} Binance symbols arrived with an unrecognized quote asset and are dropped",
                    unrecognized);
            }

            return symbols;
        }
    }

    /// <summary>
    /// Split a concatenated Binance spot name ("ETHBTC") into base and quote
    /// assets by the longest matching quote suffix; returns null when no known
    /// quote matches (the name then belongs to a rotated quote and the catalog
    /// entry is dropped, not guessed).
    /// </summary>
    private static (string Base, string Quote)? SplitBinanceName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        foreach (var quote in BinanceQuoteAssets)
        {
            if (name.Length > quote.Length &&
                name.EndsWith(quote, StringComparison.OrdinalIgnoreCase))
            {
                return (name[..^quote.Length], quote);
            }
        }

        return null;
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

        var url = $"{_baseUrl}{KlinesRequestPath}" +
                  $"?symbol={symbol.GetExchangeFormat(Id)}" +
                  $"&interval={interval}" +
                  $"&startTime={startTime}" +
                  $"&endTime={endTime}" +
                  $"&limit={limit}";

        var response = await SendRateLimitedGetAsync(
            RestLaneResolver.LaneForBarRange(to, DateTimeOffset.UtcNow),
            KlinesRequestPath,
            KlineWeightFor(limit),
            url,
            ct);
        using (response)
        {
            response.EnsureSuccessStatusCode();
            var rows = await response.Content.ReadFromJsonAsync<JsonElement[]>(options: null, cancellationToken: ct);

            var bars = new List<Bar>();
            if (rows != null)
            {
                foreach (var item in rows)
                {
                    bars.Add(new Bar(
                        DateTimeOffset.FromUnixTimeMilliseconds(item[0].GetInt64()),
                        BinanceJsonNumbers.ParseDecimal(item[1]),
                        BinanceJsonNumbers.ParseDecimal(item[2]),
                        BinanceJsonNumbers.ParseDecimal(item[3]),
                        BinanceJsonNumbers.ParseDecimal(item[4]),
                        BinanceJsonNumbers.ParseDecimal(item[5])
                    ));
                }
            }

            return bars;
        }
    }

    /// <summary>
    /// Subscribe to ticker, partial order book, and trades for a symbol.
    /// The send is serialized behind the same gate as the stream starts, and
    /// it rides the adapter-owned lifetime: a caller's token only covers the
    /// gate wait (a cancelled send would abort the shared socket, BUG-004).
    /// </summary>
    public async Task SubscribeSymbolAsync(Symbol symbol, CancellationToken ct = default)
    {
        await _streamStartGate.WaitAsync(ct);
        try
        {
            var depth = CurrentBookDepth(symbol);
            await _wsClient.SendJsonAsync(
                new SubscriptionRequest(
                    MethodNames.Subscribe,
                    NextRequestId(),
                    BuildStreamNames(symbol, depth)),
                _lifetimeCts.Token);
            _subscribedSymbols[symbol.Normalized] = new TrackedSubscription(symbol, depth);
        }
        finally
        {
            _streamStartGate.Release();
        }
    }

    /// <summary>
    /// Unsubscribe from all streams for a symbol. Serialized behind the same
    /// gate as the stream starts (BUG-028); its send rides the adapter-owned
    /// lifetime like every other socket send.
    /// </summary>
    public async Task UnsubscribeSymbolAsync(Symbol symbol, CancellationToken ct = default)
    {
        await _streamStartGate.WaitAsync(ct);
        try
        {
            var depth = CurrentBookDepth(symbol);
            await _wsClient.SendJsonAsync(
                new SubscriptionRequest(
                    MethodNames.Unsubscribe,
                    NextRequestId(),
                    BuildStreamNames(symbol, depth)),
                _lifetimeCts.Token);
            _subscribedSymbols.TryRemove(symbol.Normalized, out _);
        }
        finally
        {
            _streamStartGate.Release();
        }
    }

    /// <summary>
    /// Sequential request id: the WS API requires an id per request and
    /// echoes it back in the response.
    /// </summary>
    private int NextRequestId() => Interlocked.Increment(ref _lastRequestId);

    /// <inheritdoc/>
    public IObservable<LiquidationEvent> SubscribeLiquidations(Symbol symbol, CancellationToken ct = default)
    {
        // The wire text of EITHER app form is the same contract (T-242: the
        // spot pair and the settle-marked perpetual name one concatenated
        // symbol), so the tracked set is keyed by the wire text and one
        // all-market subscription serves both subscribers; each subscriber
        // sees the events stamped with ITS app symbol.
        var wireText = symbol.GetExchangeFormat(Id);
        return Observable
            .FromAsync(token => StartLiquidationStreamsAsync(symbol, wireText, token))
            .Select(_ => _liquidationTracked.TryGetValue(wireText, out var tracked)
                ? tracked.Events.AsObservable()
                : Observable.Empty<LiquidationEvent>())
            .Switch()
            .Select(ev => ev with { Symbol = symbol })
            .Finally(() => _resubscribeQueue.Writer.TryWrite(
                new ResubscribeRequest(symbol, ResubscribeKind.LiquidationRelease)));
    }

    /// <summary>
    /// Start the liquidation feed for one wire contract on demand (called by
    /// the FIRST subscriber of the contract): connect the dedicated futures
    /// socket once when not connected, and subscribe the all-market forced
    /// liquidation stream once when the first contract joins the tracked
    /// set. The tracked-map mutation and every send share
    /// <see cref="_streamStartGate"/> with the market-data socket's sends, so
    /// concurrent starts, releases and re-subscribes can never interleave
    /// frames (the T-052 transport discipline). The caller's token covers the
    /// gate wait and the connect only: the SEND rides the adapter-owned
    /// lifetime — cancelling it mid-send would abort the shared socket for
    /// every feed (BUG-004). The tracked map mutates AFTER the connect and
    /// the send (the market-data lane's ordering): the connection-state
    /// handler reads the map on every fresh connect, and an entry published
    /// before the dial would make the FIRST connect queue a re-subscribe the
    /// start's own send already covered — the wire would see the all-market
    /// SUBSCRIBE twice per fresh lane.
    /// </summary>
    private async Task StartLiquidationStreamsAsync(Symbol symbol, string wireText, CancellationToken ct)
    {
        await _streamStartGate.WaitAsync(ct);
        try
        {
            var isNewContract = !_liquidationTracked.TryGetValue(wireText, out var tracked);
            tracked ??= new TrackedLiquidation(symbol);
            tracked.RefCount++;
            var firstContract = isNewContract && _liquidationTracked.IsEmpty;

            if (!_liquidationSocket.IsConnected)
            {
                await _liquidationSocket.ConnectAsync(ct);
            }

            if (firstContract)
            {
                await _liquidationSocket.SendJsonAsync(
                    new SubscriptionRequest(MethodNames.Subscribe, NextRequestId(), [LiquidationStreamName]),
                    _lifetimeCts.Token);
            }

            if (isNewContract)
            {
                _liquidationTracked[wireText] = tracked;
            }
        }
        finally
        {
            _streamStartGate.Release();
        }
    }

    /// <summary>
    /// Release one subscriber's hold on the contract's liquidation feed
    /// (queued by the subscription's <c>Finally</c>): the last release
    /// unsubscribes the all-market stream and disconnects the dedicated
    /// socket when NO contract remains tracked (the all-market stream has no
    /// per-contract unsubscribe — the wire drops only when the tracked set
    /// empties). Runs on the re-subscribe pump's adapter-owned lifetime,
    /// serialized through <see cref="_streamStartGate"/> like every send; a
    /// release for an already removed contract is a no-op, never a negative
    /// count.
    /// </summary>
    private async Task ReleaseLiquidationStreamsAsync(Symbol symbol, CancellationToken ct)
    {
        var wireText = symbol.GetExchangeFormat(Id);
        await _streamStartGate.WaitAsync(ct);
        try
        {
            if (!_liquidationTracked.TryGetValue(wireText, out var tracked))
            {
                return;
            }

            tracked.RefCount--;
            if (tracked.RefCount > 0)
            {
                return;
            }

            _liquidationTracked.TryRemove(wireText, out _);

            if (_liquidationTracked.IsEmpty)
            {
                if (_liquidationSocket.IsConnected)
                {
                    await _liquidationSocket.SendJsonAsync(
                        new SubscriptionRequest(MethodNames.Unsubscribe, NextRequestId(), [LiquidationStreamName]),
                        ct);
                }

                await _liquidationSocket.DisconnectAsync();
            }

            tracked.Events.Dispose();
        }
        finally
        {
            _streamStartGate.Release();
        }
    }

    /// <summary>
    /// Re-send the all-market liquidation subscription after the dedicated
    /// socket's reconnection (the server holds no session state across
    /// sockets). The tracked-set membership is kept by the subscribe/release
    /// paths, so this send-only path cannot resurrect a stale entry; a set
    /// that emptied while the request queued skips the send.
    /// </summary>
    private async Task ResubscribeLiquidationAsync(CancellationToken ct)
    {
        await _streamStartGate.WaitAsync(ct);
        try
        {
            if (!_liquidationTracked.IsEmpty && _liquidationSocket.IsConnected)
            {
                await _liquidationSocket.SendJsonAsync(
                    new SubscriptionRequest(MethodNames.Subscribe, NextRequestId(), [LiquidationStreamName]),
                    ct);
            }
        }
        finally
        {
            _streamStartGate.Release();
        }
    }

    /// <summary>
    /// Resolve the liquidation lane's socket host: the endpoints record's
    /// optional futures override, the shipped production default otherwise.
    /// </summary>
    private static string ResolveFuturesWsUrl(BinanceEndpoints? endpoints) =>
        string.IsNullOrWhiteSpace(endpoints?.FuturesWsUrl)
            ? DefaultFuturesWsUrl
            : endpoints.FuturesWsUrl;

    /// <summary>
    /// Resolve the bracket lane's REST host: the endpoints record's optional
    /// futures override, the shipped production default otherwise.
    /// </summary>
    private static string ResolveFuturesRestUrl(BinanceEndpoints? endpoints) =>
        string.IsNullOrWhiteSpace(endpoints?.FuturesRestBaseUrl)
            ? DefaultFuturesRestUrl
            : endpoints.FuturesRestBaseUrl;

    /// <inheritdoc/>
    public async Task<IReadOnlyList<LeverageBracket>> GetLeverageBracketsAsync(
        Symbol symbol,
        CancellationToken ct = default)
    {
        // A risk table exists for leveraged contracts only: a spot symbol is
        // a request-contract violation, refused — never guessed into
        // another category.
        if (!symbol.IsPerpetual)
        {
            throw new ArgumentException(
                $"The leverage brackets serve perpetual contracts only; {symbol.Normalized} is not one.",
                nameof(symbol));
        }

        var url = $"{_futuresRestUrl}{LeverageBracketRequestPath}" +
                  $"?symbol={symbol.GetExchangeFormat(Id)}";

        using var response = await SendRateLimitedGetAsync(
            RestLane.Background,
            LeverageBracketRequestPath,
            LeverageBracketRequestWeight,
            url,
            ct);
        response.EnsureSuccessStatusCode();

        // Binance futures leverageBracket wire (the real shape): the response
        // is an ARRAY holding the requested symbol's one entry —
        // [{"symbol":"BTCUSDT","brackets":[{"bracket":1,"initialLeverage":125,
        // "notionalCap":5000000,"notionalFloor":0,"maintMarginRatio":0.004,
        // "cum":0.0,...}]}] — every numeric member a NUMBER (no strings), the
        // tiers served ascending, notionalFloor/notionalCap in USDT.
        using var json = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(ct));
        var entries = json.RootElement;
        if (entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() == 0)
        {
            throw new InvalidOperationException(
                $"The Binance leverage-bracket response for {symbol.Normalized} carries no bracket entry.");
        }

        var entry = entries[0];
        if (!entry.TryGetProperty("brackets", out var bracketRows) ||
            bracketRows.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                $"The Binance leverage-bracket response for {symbol.Normalized} carries no brackets list.");
        }

        var brackets = new List<LeverageBracket>(bracketRows.GetArrayLength());
        foreach (var row in bracketRows.EnumerateArray())
        {
            var floor = row.GetProperty("notionalFloor").GetDecimal();
            var cap = row.GetProperty("notionalCap").GetDecimal();
            var maxLeverage = row.GetProperty("initialLeverage").GetDecimal();
            var mmr = row.GetProperty("maintMarginRatio").GetDecimal();
            if (brackets.Count > 0 && floor <= brackets[^1].NotionalFloor)
            {
                // The tiers serve ascending by notional; a served order that
                // breaks the chain is a wire fault, not a table to guess
                // into shape.
                throw new InvalidOperationException(
                    $"The Binance leverage-bracket tiers for {symbol.Normalized} serve out of the ascending order.");
            }

            brackets.Add(new LeverageBracket(floor, cap, maxLeverage, mmr));
        }

        return brackets;
    }

    #endregion

    #region Private Methods

    /// <summary>
    /// One outbound REST GET through the rate limiter (T-106): the acquire
    /// waits before the send (so the wait never busy-waits anywhere else),
    /// the response is returned for the caller's read, and a 429 feeds the
    /// venue's retry guidance back into the limiter so every lane parks for
    /// the span.
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

    private string[] BuildStreamNames(Symbol symbol, int bookDepth)
    {
        // Partial book depth snapshot; the payload carries "bids"/"asks" (no symbol field).
        var streamSuffixes = new[]
        {
            "ticker",
            $"depth{bookDepth}@{DepthUpdatePeriodMs}ms",
            "trade",
        };

        var symbolStr = symbol.GetExchangeFormat(Id).ToLowerInvariant();
        return streamSuffixes.Select(suffix => $"{symbolStr}@{suffix}").ToArray();
    }

    /// <summary>
    /// The book depth this symbol's streams are currently tracked at, or the
    /// adapter's pinned depth for a symbol that is not subscribed yet.
    /// </summary>
    private int CurrentBookDepth(Symbol symbol) =>
        _subscribedSymbols.TryGetValue(symbol.Normalized, out var tracked)
            ? tracked.BookDepth
            : DepthLevelCount;

    /// <summary>
    /// The ONE consumer of the wire-request queue: every entry is served FIFO
    /// and runs to completion — a newer request (a reconnect burst trailing
    /// several symbols, a gap frame, a release) queues behind and never
    /// cancels an in-flight send, because a cancelled send aborts the whole
    /// socket and silences every feed behind it (BUG-004). The only
    /// cancellation left on this path is disposal, which tears the transport
    /// down on its own.
    /// </summary>
    private async Task RunResubscribeLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var request in _resubscribeQueue.Reader.ReadAllAsync(ct))
            {
                try
                {
                    switch (request.Kind)
                    {
                        case ResubscribeKind.MarketData:
                            await ResubscribeMarketDataAsync(request.Symbol, ct);
                            break;
                        case ResubscribeKind.LiquidationSubscribe:
                            await ResubscribeLiquidationAsync(ct);
                            break;
                        case ResubscribeKind.LiquidationRelease:
                            await ReleaseLiquidationStreamsAsync(request.Symbol, ct);
                            break;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One failed request must not kill the pump: a failed
                    // market-data re-subscribe re-arms on the next book frame,
                    // a liquidation one on the next reconnect, and a release
                    // that failed leaves the ref-count honest (the next
                    // release or disposal reaps it).
                    Logger.LogWarning(
                        ex,
                        "Binance wire request {Kind} for {Symbol} failed",
                        request.Kind,
                        request.Symbol.Normalized);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Disposal: the only exit of the re-subscribe pump.
        }
    }

    /// <summary>
    /// Re-subscribe one tracked symbol's market-data streams after a
    /// reconnect (the pump's market-data leg, extracted as its own step).
    /// </summary>
    private async Task ResubscribeMarketDataAsync(Symbol symbol, CancellationToken ct)
    {
        await _streamStartGate.WaitAsync(ct);
        try
        {
            if (_subscribedSymbols.TryGetValue(symbol.Normalized, out var tracked)
                && _wsClient.IsConnected)
            {
                await _wsClient.SendJsonAsync(
                    new SubscriptionRequest(
                        MethodNames.Subscribe,
                        NextRequestId(),
                        BuildStreamNames(symbol, tracked.BookDepth)),
                    ct);
            }
        }
        finally
        {
            _streamStartGate.Release();
        }
    }

    /// <summary>Extract the symbol part ("btcusdt") from a stream name ("btcusdt@depth20@100ms").</summary>
    private static string GetStreamSymbol(string stream)
    {
        var separatorIndex = stream.IndexOf('@');
        return separatorIndex < 0 ? stream : stream[..separatorIndex];
    }

    private Symbol? ParseExchangeSymbol(string exchangeSymbol)
    {
        return Symbol.TryParseExchangeFormat(exchangeSymbol, Id, out var symbol) && symbol != null
            ? symbol
            : null;
    }

    private void HandleWebSocketMessage(string message)
    {
        try
        {
            // The book stamp is the frame's local receive moment (T-142's one
            // clock discipline): captured once at the ingestion point, never
            // an exchange clock — the WS-latency histogram measures
            // transport, not offset. Tickers and trades keep their exchange
            // event times (E / T).
            var receivedAt = DateTimeOffset.UtcNow;

            using var json = JsonDocument.Parse(message);
            var root = json.RootElement;

            // Combined-stream envelope: {"stream":"btcusdt@ticker","data":{...}}
            if (!root.TryGetProperty("stream", out var streamProp) ||
                !root.TryGetProperty("data", out var data))
            {
                return;
            }

            var stream = streamProp.GetString();
            if (string.IsNullOrEmpty(stream))
                return;

            var symbol = ParseExchangeSymbol(GetStreamSymbol(stream));

            // Unknown symbol format: skip the message rather than guessing a default.
            if (symbol is null)
            {
                Logger.LogWarning("WebSocket stream {Stream}: unrecognised symbol format", stream);
                return;
            }

            if (stream.EndsWith("@ticker"))
            {
                HandleTickerMessage(symbol, data);
            }
            else if (stream.Contains("@depth"))
            {
                HandleOrderBookMessage(symbol, data, receivedAt);
            }
            else if (stream.EndsWith("@trade"))
            {
                HandleTradeMessage(symbol, data);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error handling WebSocket message");
        }
    }

    private void HandleTickerMessage(Symbol symbol, JsonElement data)
    {
        var ticker = new Ticker(
            symbol,
            BinanceJsonNumbers.ParseDecimal(data.GetProperty("c")),
            BinanceJsonNumbers.ParseDecimal(data.GetProperty("b")),
            BinanceJsonNumbers.ParseDecimal(data.GetProperty("a")),
            BinanceJsonNumbers.ParseDecimal(data.GetProperty("v")),
            BinanceJsonNumbers.ParseDecimal(data.GetProperty("P")),
            DateTimeOffset.FromUnixTimeMilliseconds(data.GetProperty("E").GetInt64())
        );

        PublishTicker(ticker);
    }

    private void HandleOrderBookMessage(Symbol symbol, JsonElement data, DateTimeOffset receivedAt)
    {
        var bids = ParsePriceLevels(data.GetProperty("bids"));
        var asks = ParsePriceLevels(data.GetProperty("asks"));

        // Partial book depth snapshots carry no event timestamp, only
        // lastUpdateId — the stamp is the frame's local receive moment
        // (T-142's one clock discipline).
        var orderBook = new OrderBook
        {
            Symbol = symbol,
            Bids = bids,
            Asks = asks,
            Timestamp = receivedAt
        };

        PublishOrderBook(orderBook);
    }

    private static List<PriceLevel> ParsePriceLevels(JsonElement array)
    {
        var levels = new List<PriceLevel>();
        foreach (var level in array.EnumerateArray())
        {
            levels.Add(new PriceLevel(
                BinanceJsonNumbers.ParseDecimal(level[0]),
                BinanceJsonNumbers.ParseDecimal(level[1])));
        }

        return levels;
    }

    private void HandleTradeMessage(Symbol symbol, JsonElement data)
    {
        var trade = new MarketTrade
        {
            Id = data.GetProperty("t").GetInt64().ToString(),
            Symbol = symbol,
            // "m" is "is buyer the maker": a maker buy means the aggressor sold.
            Side = data.GetProperty("m").GetBoolean() ? OrderSide.Sell : OrderSide.Buy,
            Price = new Price(BinanceJsonNumbers.ParseDecimal(data.GetProperty("p"))),
            Quantity = new Quantity(BinanceJsonNumbers.ParseDecimal(data.GetProperty("q"))),
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(data.GetProperty("T").GetInt64())
        };

        PublishTrade(trade);
    }

    /// <summary>
    /// Frame dispatch for the dedicated liquidation socket: the only data
    /// frames it serves carry the all-market forced-liquidation stream's
    /// envelope; subscribe acks ({"result":null,"id":N}) and any other
    /// stream name are ignored — errors from one message never leave the
    /// handler.
    /// </summary>
    private void HandleLiquidationSocketMessage(string message)
    {
        try
        {
            using var json = JsonDocument.Parse(message);
            var root = json.RootElement;

            if (!root.TryGetProperty("stream", out var streamProp) ||
                !root.TryGetProperty("data", out var data))
            {
                // A control frame (the subscribe/unsubscribe result echo).
                return;
            }

            if (streamProp.GetString() != LiquidationStreamName)
            {
                return;
            }

            if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("o", out var order))
            {
                Logger.LogWarning(
                    "Binance liquidation frame: the data carries no forced-order object, the frame is dropped");
                return;
            }

            HandleLiquidationFrame(order);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error handling Binance liquidation WebSocket message");
        }
    }

    /// <summary>
    /// Parse one forced-liquidation order (the wire measured live,
    /// 2026-11-16): <c>{"e":"forceOrder","o":{"s":"BTCUSDT","S":"SELL",
    /// "o":"LIMIT","f":"IOC","q":"0.014","p":"9910","X":"FILLED",
    /// "ap":"9910.5","l":"0.011","z":"0.011","T":1763245800000}}</c> —
    /// <c>s</c> the contract's concatenated name, <c>S</c> the closing
    /// ORDER's side ("SELL" = a LONG position was force-closed), <c>q</c> the
    /// original size, <c>p</c> the bankruptcy price, <c>ap</c> the average
    /// forced fill, <c>z</c> the accumulated filled size and <c>T</c> the
    /// event's epoch-milliseconds NUMBER. The event carries the executed
    /// size (<c>z</c>, the honest total the venue served) and the average
    /// forced fill (<c>ap</c>) when both are served positive, the original
    /// order facts otherwise — never a fabricated zero. An untracked
    /// contract is dropped at debug (the all-market stream serves every
    /// contract; only the tracked ones fan out). A malformed order is
    /// counted and dropped — it never aborts the socket.
    /// </summary>
    private void HandleLiquidationFrame(JsonElement order)
    {
        if (order.ValueKind != JsonValueKind.Object)
        {
            Logger.LogWarning("Binance liquidation frame: the order is not an object, the frame is dropped");
            return;
        }

        if (!order.TryGetProperty("s", out var symbolProp) || symbolProp.ValueKind != JsonValueKind.String)
        {
            Logger.LogWarning("Binance liquidation frame: the order carries no contract name, the event is dropped");
            return;
        }

        var contract = symbolProp.GetString()!;
        if (!_liquidationTracked.TryGetValue(contract, out var tracked))
        {
            // The all-market stream serves every contract; only the tracked
            // ones fan out. An untracked frame is an in-flight unsubscribe
            // the server has not processed yet — the demand-driven wire's
            // routine cargo (debug, never a warning).
            Logger.LogDebug(
                "Binance liquidation event for {Contract}: untracked contract, the event is dropped",
                contract);
            return;
        }

        try
        {
            if (!order.TryGetProperty("S", out var sideProp) ||
                sideProp.GetString() is not (FrameSides.Buy or FrameSides.Sell) ||
                !order.TryGetProperty("T", out var timeProp) ||
                timeProp.ValueKind != JsonValueKind.Number)
            {
                Logger.LogWarning(
                    "Binance liquidation event for {Contract}: the order is malformed, the event is dropped",
                    tracked.Symbol.Normalized);
                return;
            }

            var side = sideProp.GetString();
            var price = ReadLiquidationNumber(order, "ap") ?? ReadLiquidationNumber(order, "p");
            var quantity = ReadLiquidationNumber(order, "z") ?? ReadLiquidationNumber(order, "q");
            if (price is not { } forcedPrice || quantity is not { } forcedQuantity)
            {
                Logger.LogWarning(
                    "Binance liquidation event for {Contract}: no parsable price or size, the event is dropped",
                    tracked.Symbol.Normalized);
                return;
            }

            tracked.Events.OnNext(new LiquidationEvent
            {
                Symbol = tracked.Symbol,
                LiquidatedSide = side == FrameSides.Sell ? LiquidationSide.Long : LiquidationSide.Short,
                Price = new Price(forcedPrice),
                Quantity = new Quantity(forcedQuantity),
                Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(timeProp.GetInt64()),
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // One unparsable order is dropped and counted; the socket keeps
            // flowing (the BUG-027 discipline — a bad row never aborts the
            // stream).
            Logger.LogWarning(
                ex,
                "Binance liquidation event for {Contract} is malformed and is dropped",
                tracked.Symbol.Normalized);
        }
    }

    /// <summary>
    /// Read one numeric-string order member (the venue wires sizes and
    /// prices as decimal STRINGS): a present, parsable, strictly positive
    /// value returns; anything else (absent, unparsable, zero — an average
    /// price before fills, an empty accumulation) returns null so the
    /// caller's fallback chain picks the venue's original order fact.
    /// </summary>
    private static decimal? ReadLiquidationNumber(JsonElement order, string name)
    {
        if (!order.TryGetProperty(name, out var prop))
        {
            return null;
        }

        try
        {
            var value = BinanceJsonNumbers.ParseDecimal(prop);
            return value > 0m ? value : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // An unparsable member is the fallback chain's signal, not a
            // socket fault: the caller drops to the next member.
            _ = ex;
            return null;
        }
    }

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
        IsConnected = _wsClient.IsConnected || _liquidationSocket.IsConnected;
        if (isConnected != _lastLoggedConnectionState)
        {
            _lastLoggedConnectionState = isConnected;
            Logger.LogInformation("Binance WebSocket connection state: {State}", isConnected ? "Connected" : "Disconnected");
        }

        if (!isConnected || _subscribedSymbols.IsEmpty)
        {
            return;
        }

        // The server holds no session state across sockets: re-subscribe the
        // whole tracked set through the queue (the first connect skips this
        // — the tracked set is still empty and the per-symbol start already
        // subscribed on its own).
        foreach (var tracked in _subscribedSymbols.Values)
        {
            _resubscribeQueue.Writer.TryWrite(new ResubscribeRequest(tracked.Symbol, ResubscribeKind.MarketData));
        }
    }

    /// <summary>
    /// The dedicated liquidation socket's connection transitions: the
    /// aggregate connected state and the deduped log line, then — on a fresh
    /// connect — one re-subscribe request for the all-market stream (the
    /// server holds no session state across sockets). A tracked set that
    /// emptied while the transition queued makes the re-subscribe a no-op.
    /// </summary>
    private void HandleLiquidationConnectionStateChanged(bool isConnected)
    {
        IsConnected = _wsClient.IsConnected || _liquidationSocket.IsConnected;
        if (isConnected != _liquidationLastLoggedConnectionState)
        {
            _liquidationLastLoggedConnectionState = isConnected;
            Logger.LogInformation(
                "Binance futures WebSocket connection state: {State}",
                isConnected ? "Connected" : "Disconnected");
        }

        if (!isConnected || _liquidationTracked.IsEmpty)
        {
            return;
        }

        _resubscribeQueue.Writer.TryWrite(
            new ResubscribeRequest(_liquidationTracked.Values.First().Symbol, ResubscribeKind.LiquidationSubscribe));
    }

    private static string ConvertTimeFrame(TimeFrame timeFrame)
    {
        return timeFrame switch
        {
            TimeFrame.M1 => "1m",
            TimeFrame.M5 => "5m",
            TimeFrame.M15 => "15m",
            TimeFrame.M30 => "30m",
            TimeFrame.H1 => "1h",
            TimeFrame.H4 => "4h",
            TimeFrame.D1 => "1d",
            TimeFrame.W1 => "1w",
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
        await _resubscribeLoop;
        _lifetimeCts.Dispose();

        await base.DisposeAsync();
        _messageSubscription.Dispose();
        _connectionSubscription.Dispose();
        _liquidationMessageSubscription.Dispose();
        _liquidationConnectionSubscription.Dispose();
        await _wsClient.DisposeAsync();
        await _liquidationSocket.DisposeAsync();
        foreach (var tracked in _liquidationTracked.Values)
        {
            tracked.Events.Dispose();
        }

        _liquidationTracked.Clear();
        _streamStartGate.Dispose();
        // HttpClient comes from IHttpClientFactory; its lifetime is managed there.
    }

    #endregion

    #region Nested Types

    private sealed record SubscriptionRequest(
        [property: JsonPropertyName("method")] string Method,
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("params")] string[] Params);

    /// <summary>A tracked symbol with the book depth its streams were requested at.</summary>
    private sealed record TrackedSubscription(Symbol Symbol, int BookDepth);

    /// <summary>
    /// One queued wire request for the FIFO pump: the symbol the request
    /// rides (the all-market liquidation re-subscribe carries any one
    /// tracked symbol — the symbol is a log detail there, the request is
    /// the subscription of the whole stream) and its KIND.
    /// </summary>
    private sealed record ResubscribeRequest(Symbol Symbol, ResubscribeKind Kind);

    /// <summary>The kinds of wire request the re-subscribe pump serves.</summary>
    private enum ResubscribeKind
    {
        /// <summary>Re-subscribe a symbol's market-data topic set on the combined socket.</summary>
        MarketData,

        /// <summary>Re-subscribe the all-market forced-liquidation stream on the dedicated socket.</summary>
        LiquidationSubscribe,

        /// <summary>Release one subscriber's hold on the liquidation lane.</summary>
        LiquidationRelease,
    }

    /// <summary>The venue's forced-order side literals (the closing ORDER's side: "SELL" closes a LONG).</summary>
    private static class FrameSides
    {
        public const string Buy = "BUY";
        public const string Sell = "SELL";
    }

    /// <summary>
    /// One contract's tracked liquidation feed: the app symbol it was first
    /// requested under (the log and the re-subscribe name it), its shared
    /// event subject and the open subscribers' ref-count (the last release
    /// unsubscribes the wire and drops the entry).
    /// </summary>
    private sealed class TrackedLiquidation(Symbol symbol)
    {
        /// <summary>The app symbol the feed was first requested under.</summary>
        public Symbol Symbol { get; } = symbol;

        /// <summary>The contract's shared event subject (every subscriber of the wire text sees it).</summary>
        public Subject<LiquidationEvent> Events { get; } = new();

        /// <summary>The open subscribers' count (the lifecycle's ownership count).</summary>
        public int RefCount;
    }

    private static class MethodNames
    {
        public const string Subscribe = "SUBSCRIBE";
        public const string Unsubscribe = "UNSUBSCRIBE";
    }

    #endregion
}
