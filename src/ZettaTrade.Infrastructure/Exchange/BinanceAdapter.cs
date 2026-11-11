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
/// Binance exchange adapter implementation (read-only market data).
/// Creates its own WebSocket transport; HTTP clients come from IHttpClientFactory.
/// </summary>
public sealed class BinanceAdapter : ExchangeAdapterBase, IMarketDataAdapter
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
    private readonly IRestRateLimiter _restLimiter;
    private readonly IDisposable _messageSubscription;
    private readonly IDisposable _connectionSubscription;

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
    /// Topic re-subscribe requests (a fresh socket): each one lands in this
    /// queue and the ONE consumer task serves them FIFO, serialized through
    /// <see cref="_streamStartGate"/> and never fire-and-forget.
    /// </summary>
    private readonly Channel<Symbol> _resubscribeQueue = Channel.CreateUnbounded<Symbol>(
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

        _messageSubscription = _wsClient.MessageStream.Subscribe(HandleWebSocketMessage);
        _connectionSubscription = _wsClient.ConnectionStateStream.Subscribe(HandleConnectionStateChanged);

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
                Logger.LogWarning(
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
    /// The ONE consumer of the re-subscribe queue: after every reconnect the
    /// tracked set is re-subscribed FIFO, each send running to completion — a
    /// newer request queues behind and never cancels an in-flight send,
    /// because a cancelled send aborts the whole socket and silences every
    /// feed behind it (BUG-004). Runs on the adapter's owned lifetime; the
    /// only exit is disposal.
    /// </summary>
    private async Task RunResubscribeLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var symbol in _resubscribeQueue.Reader.ReadAllAsync(ct))
            {
                try
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
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One failed re-subscribe must not kill the pump: the
                    // tracked set survives and the NEXT reconnect re-serves it.
                    Logger.LogWarning(ex, "Binance stream re-subscribe for {Symbol} failed", symbol.Normalized);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Disposal: the only exit of the re-subscribe pump.
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

    private void HandleConnectionStateChanged(bool isConnected)
    {
        IsConnected = isConnected;
        Logger.LogInformation("Binance WebSocket connection state: {State}", isConnected ? "Connected" : "Disconnected");

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
            _resubscribeQueue.Writer.TryWrite(tracked.Symbol);
        }
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
        await _wsClient.DisposeAsync();
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

    private static class MethodNames
    {
        public const string Subscribe = "SUBSCRIBE";
        public const string Unsubscribe = "UNSUBSCRIBE";
    }

    #endregion
}
