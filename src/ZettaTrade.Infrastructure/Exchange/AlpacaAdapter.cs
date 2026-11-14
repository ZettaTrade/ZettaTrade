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
/// Alpaca v2 US-equity market-data adapter (T-198): the stock face over the
/// user-keyed provider — the free tier's IEX feed. Mirrors the Bybit
/// structure: a named HTTP client for REST, ONE authenticated WebSocket from
/// the <see cref="ExchangeEndpointRegistry"/> (the market-data REST base and
/// the assets-catalog base are distinct hosts, so the endpoint record carries
/// both). Wire facts, live-documented by the provider (2026-11):
/// EVERY stream message is a JSON ARRAY (control messages arrive as arrays
/// of one, data messages may batch several events); the stream must
/// authenticate within the venue's 10 s window
/// (<c>{"action":"auth","key":...,"secret":...}</c>) and answers a
/// <c>[{"T":"success","msg":"authenticated"}]</c> ack; trade events carry NO
/// aggressor side (a trade is a matched buy and a matched sell — the tape
/// publishes <see cref="OrderSide.None"/> and never a fabricated side);
/// quote events carry the venue's TOP OF BOOK only (a zero price means no
/// active quote on that side — the honest book is one level per served
/// side, never a padded ladder); the running session volume rides the
/// dailyBars channel. The v2 IEX timestamps carry nanosecond fractions, so
/// the parse trims the fraction to <see cref="DateTimeOffset"/> precision
/// instead of dropping whole events. The keys ride
/// <see cref="ISecretStore"/> only: fetched per use (per session auth, per
/// REST request), never logged, never defaulted — a missing pair fails the
/// call with the keys named.
/// </summary>
public sealed class AlpacaAdapter : ExchangeAdapterBase, IMarketDataAdapter
{
    #region Constants

    /// <summary>Named HttpClientFactory client used for REST requests.</summary>
    public const string HttpClientName = "alpaca";

    /// <summary>Timeout for REST requests, in seconds.</summary>
    public const int RequestTimeoutSeconds = 30;

    /// <summary>Production REST base URL of the market-data service (bars).</summary>
    public const string DefaultRestBaseUrl = "https://data.alpaca.markets";

    /// <summary>Production REST base URL of the assets catalog (a distinct host: Alpaca keeps the symbol catalog on its trading-API host).</summary>
    public const string DefaultCatalogRestBaseUrl = "https://api.alpaca.markets";

    /// <summary>Production WebSocket URL of the free tier's IEX market-data stream.</summary>
    public const string DefaultWsUrl = "wss://stream.data.alpaca.markets/v2/iex";

    /// <summary>REST path family of the bar history endpoint (the single-symbol form).</summary>
    private const string BarsRequestPath = "/v2/stocks";

    /// <summary>REST path family of the assets catalog.</summary>
    private const string CatalogRequestPath = "/v2/assets";

    /// <summary>
    /// The shared REST bucket key (T-198): the shipped free tier caps the
    /// MARKET-DATA API as one shared requests-per-minute budget — the
    /// catalog and the bars calls spend the SAME bucket, so the manifest
    /// carries one family and both call sites acquire from it. The label is
    /// the adapter's family name, not a path fragment.
    /// </summary>
    private const string MarketDataBudgetKey = "market-data";

    /// <summary>The market-data feed the free tier serves (the honest feed pin: the adapter never asks for the SIP feed it is not entitled to).</summary>
    private const string FreeFeedName = "iex";

    /// <summary>The per-minute shared request budget the free tier publishes for its market-data REST API, as shipped.</summary>
    private const int MarketDataRequestsPerMinute = 200;

    /// <summary>The refill cadence of the shared request budget.</summary>
    private static readonly TimeSpan RestRefillInterval = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The shipped REST budget manifest (T-106): the free tier caps the
    /// market-data API with one shared requests-per-minute count, so ONE
    /// family carries the bucket, rescalable live through
    /// <c>Limiter.Alpaca.RequestWeightMinute</c>.
    /// </summary>
    public static RestRateLimiterOptions DefaultRestLimits { get; } = new()
    {
        SettingsKey = "Alpaca",
        ScaleKind = RestBudgetScaleKind.WeightPerMinute,
        Endpoints =
        [
            new RestEndpointBudget(MarketDataBudgetKey, MarketDataRequestsPerMinute, RestRefillInterval),
        ],
    };

    /// <summary>The request weight a public Alpaca market-data REST call spends from its bucket (one per request).</summary>
    private const int RequestWeight = 1;

    /// <summary>
    /// Upper bound of bar pages per load: protects the pagination loop from a
    /// pathological (or hostile) server response that never stops handing
    /// out <c>next_page_token</c>s. A full free-tier request fits ONE page;
    /// if the cap ever bites it is one aggregated warning, never a silent
    /// truncation.
    /// </summary>
    private const int MaxBarsPages = 5;

    /// <summary>The principled wait for the auth ack before the subscribe rides (the venue's own auth window, in seconds).</summary>
    public const int AuthAckTimeoutSeconds = 10;

    /// <summary>ISecretStore key of the Alpaca API key.</summary>
    public const string ApiKeySecretKey = "alpaca.api-key";

    /// <summary>ISecretStore key of the Alpaca API secret.</summary>
    public const string ApiSecretSecretKey = "alpaca.api-secret";

    /// <summary>
    /// The standing no-credentials line's once-per-session gate (T-227):
    /// 0 = pending, 1 = fired. The absence is a standing fact, not an
    /// error per call — the repeated refusals ride silently.
    /// </summary>
    private int _absenceLogged;

    /// <summary>The order book depth the provider serves: top of book only (one level per active side).</summary>
    private const int TopOfBookDepth = 1;

    /// <summary>The order book depths the adapter serves (the honest subset: the IEX quote is a best bid/ask, never a ladder).</summary>
    private static readonly IReadOnlySet<int> SupportedOrderBookDepths =
        new HashSet<int> { TopOfBookDepth };

    /// <summary>The stream frame's message-type member.</summary>
    private const string TypeMember = "T";

    /// <summary>The stream event's symbol member.</summary>
    private const string SymbolMember = "S";

    private const string TradeMessageType = "t";
    private const string QuoteMessageType = "q";
    private const string DailyBarMessageType = "d";
    private const string MinuteBarMessageType = "b";
    private const string UpdatedBarMessageType = "u";
    private const string SuccessMessageType = "success";
    private const string ErrorMessageType = "error";
    private const string SubscriptionMessageType = "subscription";

    /// <summary>The auto-added companion channels of a trades subscription (corrections and cancel/errors): recorded, not applied — a recorded limitation of this wave's tape.</summary>
    private static readonly string[] AutoAddedMessageTypes = ["c", "x"];

    /// <summary>The channels the adapter subscribes per symbol (minute bars are never subscribed: the chart's candles ride the REST bars, not the stream).</summary>
    private static readonly string[] SubscribeChannels = ["trades", "quotes", "dailyBars"];

    #endregion

    #region Properties

    /// <inheritdoc/>
    public override string Id => "alpaca";

    /// <inheritdoc/>
    public override string Name => "Alpaca";

    /// <inheritdoc/>
    public override ExchangeCapabilities Capabilities =>
        ExchangeCapabilities.MarketData | ExchangeCapabilities.OrderBook | ExchangeCapabilities.HistoricalData;

    /// <summary>Configured market-data REST base URL (internal: the registration tests assert it, no network).</summary>
    internal string RestBaseUrl => _baseUrl;

    /// <summary>Configured catalog REST base URL (internal: the registration tests assert it).</summary>
    internal string CatalogRestBaseUrl => _catalogBaseUrl;

    /// <summary>Configured WS URL (internal: the registration tests assert it).</summary>
    internal string WsUrl => _wsUrl;

    /// <inheritdoc/>
    protected override IReadOnlySet<int> SupportedBookDepths => SupportedOrderBookDepths;

    /// <summary>
    /// The adapter's explicit reconnect policy (T-105): the shipped production
    /// timing — the same shape <see cref="ReconnectPolicy.Default"/> carries.
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
    private readonly string _catalogBaseUrl;
    private readonly string _wsUrl;
    private readonly IRestRateLimiter _restLimiter;
    private readonly ISecretStore _secrets;

    /// <summary>
    /// The symbols subscribed on the stream, keyed by the exchange-format
    /// text: the reconnect re-subscribe set AND the frame-router's honest
    /// gate — a data frame for a symbol this adapter never subscribed is a
    /// server race and is dropped with a warning, never guessed into a
    /// subscription.
    /// </summary>
    private readonly ConcurrentDictionary<string, Symbol> _subscribed = new(StringComparer.Ordinal);

    /// <summary>The per-symbol ticker merge states, keyed by the normalized symbol.</summary>
    private readonly ConcurrentDictionary<string, StockTickerState> _tickerStates = new();

    /// <summary>Serializes every session flow (connect, auth, subscribe, re-subscribe): one authenticated stream, one writer.</summary>
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    /// <summary>
    /// Re-subscribe requests (a fresh socket after a reconnect): each one
    /// lands in this queue and the ONE consumer task serves them FIFO,
    /// serialized through <see cref="_sendGate"/> and never fire-and-forget.
    /// </summary>
    private readonly Channel<Symbol> _resubscribeQueue = Channel.CreateUnbounded<Symbol>(
        new UnboundedChannelOptions { SingleReader = true });

    private readonly IDisposable _messageSubscription;
    private readonly IDisposable _connectionSubscription;

    /// <summary>Adapter-owned lifetime; the re-subscribe loop is awaited on disposal, never abandoned.</summary>
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly Task _resubscribeLoop;

    /// <summary>
    /// The pending auth ack (created BEFORE the auth frame is sent, so the handler can always find it — BUG-004's race rule).
    /// The read/write discipline rides <see cref="Volatile"/> (the repo's own
    /// pattern), never the field keyword.
    /// </summary>
    private TaskCompletionSource<bool>? _authAckSignal;

    /// <summary>Whether the CURRENT socket session is authenticated (reset on every disconnect; re-auth rides the next session flow).</summary>
    private volatile bool _authenticated;

    private int _disposed;

    #endregion

    #region Constructor

    /// <summary>
    /// Create a new AlpacaAdapter instance. Endpoints come from the
    /// registration configuration (see <see cref="ExchangeEndpointRegistry"/>)
    /// or fall back to the production hosts; providing a record with blank
    /// members is a configuration error and throws immediately (the tolerant
    /// missing-entry case is the registry's job, not the adapter's). The
    /// secret store is REQUIRED (the market-data provider is user-keyed on
    /// every face — the catalog, the bars and the stream all authenticate);
    /// the keys are fetched per use and never leave the store. The reconnect
    /// supervisor is bound when provided (T-105); the REST rate limiter is
    /// wired when provided (T-106): the registration seam builds it from the
    /// shipped manifest and the settings service; unwired constructions
    /// (tests, plugin hosts) run unshaped through the pass-through limiter.
    /// </summary>
    public AlpacaAdapter(
        ILogger<AlpacaAdapter> logger,
        IHttpClientFactory httpClientFactory,
        ISecretStore secrets,
        AlpacaEndpoints? endpoints = null,
        ReconnectSupervisor? reconnectSupervisor = null,
        IRestRateLimiter? restLimiter = null)
        : base(logger)
    {
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));

        _baseUrl = endpoints?.RestBaseUrl ?? DefaultRestBaseUrl;
        _catalogBaseUrl = endpoints?.CatalogRestBaseUrl ?? DefaultCatalogRestBaseUrl;
        _wsUrl = endpoints?.WsUrl ?? DefaultWsUrl;

        if (string.IsNullOrWhiteSpace(_baseUrl) ||
            string.IsNullOrWhiteSpace(_catalogBaseUrl) ||
            string.IsNullOrWhiteSpace(_wsUrl))
        {
            throw new ArgumentException(
                "Configured Alpaca endpoints must carry non-empty RestBaseUrl, CatalogRestBaseUrl and WsUrl",
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
        await _sendGate.WaitAsync(ct);
        try
        {
            await EnsureSessionAsync(ct);
        }
        finally
        {
            _sendGate.Release();
        }

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
    /// subscriber of any feed): ensure the authenticated session (connect
    /// once when not connected, authenticate once per socket session), then
    /// subscribe the symbol's ticker, order book and session-volume channels
    /// (ONE idempotent subscribe action; the server answers the full
    /// subscription echo). Serialized with the send gate so concurrent starts
    /// cannot interleave frames on the shared WebSocket. The caller's token
    /// covers the gate wait and the connect only: the SEND rides the
    /// adapter-owned lifetime — cancelling it mid-send would abort the
    /// shared socket for every feed (BUG-004).
    /// </summary>
    protected override async Task StartSymbolStreamsAsync(
        Symbol symbol,
        int? bookDepth,
        CancellationToken ct)
    {
        await _sendGate.WaitAsync(ct);
        try
        {
            await EnsureSessionAsync(ct);
            await SendSubscribeAsync(symbol, _lifetimeCts.Token);
            _subscribed[symbol.GetExchangeFormat(Id)] = symbol;
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <inheritdoc/>
    public override async Task<IReadOnlyList<Symbol>> GetSymbolsAsync(CancellationToken ct = default)
    {
        var url = $"{_catalogBaseUrl}{CatalogRequestPath}?status=active&asset_class=us_equity";
        var response = await SendRateLimitedGetAsync(RestLane.Background, MarketDataBudgetKey, RequestWeight, url, ct);
        response.EnsureSuccessStatusCode();

        using var document = await response.Content.ReadFromJsonAsync<JsonDocument>(options: null, cancellationToken: ct);
        var root = document?.RootElement
            ?? throw new InvalidOperationException($"Alpaca REST request returned an empty body: {url}");

        if (root.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                $"Alpaca assets catalog is not an asset array ({url})");
        }

        var symbols = new List<Symbol>();
        var dropped = 0;
        foreach (var item in root.EnumerateArray())
        {
            var ticker = TryGetString(item, "symbol");
            if (string.IsNullOrWhiteSpace(ticker))
            {
                dropped++;
                continue;
            }

            var status = TryGetString(item, "status");
            var tradable = item.TryGetProperty("tradable", out var tradableProp) && tradableProp.ValueKind == JsonValueKind.True;

            // The split is server-served metadata: a non-trading or
            // non-tradable asset is the silent filter (the entry exists, it
            // just does not serve), a blank ticker is the malformed outcome
            // (counted into the aggregated warning).
            if (status == "active" && tradable)
            {
                symbols.Add(new Symbol(ticker, null, AssetClass.Stock));
            }
        }

        if (dropped > 0)
        {
            Logger.LogWarning(
                "{Count} Alpaca assets catalog entries arrived malformed and are dropped",
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
        var timeframe = ConvertTimeFrame(timeFrame);
        var startTime = ConvertRestTimestamp(from);
        var endTime = ConvertRestTimestamp(to ?? DateTimeOffset.UtcNow);

        var bars = new List<Bar>();
        var pages = 0;
        var truncated = false;
        var pageToken = string.Empty;

        do
        {
            var url = $"{_baseUrl}{BarsRequestPath}/{Uri.EscapeDataString(symbol.GetExchangeFormat(Id))}/bars" +
                      $"?timeframe={timeframe}" +
                      $"&start={startTime}" +
                      $"&end={endTime}" +
                      $"&limit={limit}" +
                      $"&feed={FreeFeedName}" +
                      $"&sort=asc";
            if (!string.IsNullOrEmpty(pageToken))
            {
                url += $"&page_token={Uri.EscapeDataString(pageToken)}";
            }

            var result = await GetBarsPageAsync(RestLaneResolver.LaneForBarRange(to, DateTimeOffset.UtcNow), url, ct);
            if (result.Bars.TryGetProperty("bars", out var rows) && rows.ValueKind == JsonValueKind.Array)
            {
                foreach (var row in rows.EnumerateArray())
                {
                    bars.Add(new Bar(
                        ParseRfc3339Timestamp(row.GetProperty("t")),
                        GetDecimal(row.GetProperty("o")),
                        GetDecimal(row.GetProperty("h")),
                        GetDecimal(row.GetProperty("l")),
                        GetDecimal(row.GetProperty("c")),
                        GetDecimal(row.GetProperty("v"))));
                }
            }

            pageToken = result.NextPageToken;
            pages++;
            truncated = pages >= MaxBarsPages && !string.IsNullOrEmpty(pageToken);
        }
        while (!truncated && !string.IsNullOrEmpty(pageToken));

        if (truncated)
        {
            Logger.LogWarning(
                "The Alpaca bars load exceeded the shipped page cap ({MaxPages} pages); the load stops with a partial history",
                MaxBarsPages);
        }

        // The single-symbol endpoint serves ascending bars under sort=asc; the
        // adapter's bar contract is ascending — pinned defensively per page
        // order, never trusted silently.
        bars.Sort(static (left, right) => left.Time.CompareTo(right.Time));

        return bars;
    }

    #endregion

    #region Private Methods: session

    /// <summary>
    /// Ensure the authenticated stream session: connect once when not
    /// connected, authenticate once per socket session. Runs under
    /// <see cref="_sendGate"/>. The auth keys are fetched per session here —
    /// a pair saved AFTER a failed auth is picked up by the retry's next
    /// attempt, never cached past its own failure.
    /// </summary>
    private async Task EnsureSessionAsync(CancellationToken ct)
    {
        if (!_wsClient.IsConnected)
        {
            await _wsClient.ConnectAsync(ct);
        }

        if (_authenticated)
        {
            return;
        }

        await SendAuthAsync(ct);
    }

    /// <summary>
    /// Authenticate the current socket session: fetch the key pair from the
    /// secret store (never logged, never carried anywhere else), send the
    /// auth action and await the venue's ack — the venue drops a stream that
    /// does not authenticate within its window, so the ack wait is the
    /// connect's own contract, not politeness. A missing pair throws with
    /// the ISecretStore keys named (never a silent unauthenticated stream).
    /// </summary>
    private async Task SendAuthAsync(CancellationToken ct)
    {
        var apiKey = await _secrets.GetAsync(ApiKeySecretKey, ct);
        var apiSecret = await _secrets.GetAsync(ApiSecretSecretKey, ct);
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(apiSecret))
        {
            throw RefuseAbsentCredentials([ApiKeySecretKey, ApiSecretSecretKey]);
        }

        // The ack signal exists BEFORE the frame is sent: the handler must
        // always find it (Volatile discipline — BUG-004's race rule).
        var ack = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _authAckSignal, ack);

        await _wsClient.SendJsonAsync(
            new AuthAction(apiKey, apiSecret),
            _lifetimeCts.Token);

        bool authenticated;
        try
        {
            authenticated = await ack.Task.WaitAsync(
                TimeSpan.FromSeconds(AuthAckTimeoutSeconds),
                _lifetimeCts.Token);
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException(
                $"Alpaca authentication did not complete within {AuthAckTimeoutSeconds}s; the stream answered no ack");
        }

        if (!authenticated)
        {
            throw new InvalidOperationException(
                "Alpaca authentication failed: the stream answered an error (check the secret store keys)");
        }

        _authenticated = true;
        Logger.LogInformation("Alpaca market-data stream authenticated");
    }

    /// <summary>Send one symbol's subscribe action (all served channels in one idempotent frame).</summary>
    private async Task SendSubscribeAsync(Symbol symbol, CancellationToken ct)
    {
        var wireSymbol = symbol.GetExchangeFormat(Id);
        await _wsClient.SendJsonAsync(
            new SubscribeAction([wireSymbol], [wireSymbol], [wireSymbol]),
            ct);
    }

    /// <summary>
    /// The ONE consumer of the re-subscribe queue: every entry is served FIFO
    /// and runs to completion — a newer request never cancels an in-flight
    /// send, because a cancelled send aborts the whole socket and churns the
    /// reconnect. The only cancellation left on this path is disposal, which
    /// tears the transport down on its own.
    /// </summary>
    private async Task RunResubscribeLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var symbol in _resubscribeQueue.Reader.ReadAllAsync(ct))
            {
                try
                {
                    await ResendSubscribeAsync(symbol, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One failed re-subscribe must not kill the pump: the
                    // next data frame re-arms the recovery on its own.
                    Logger.LogWarning(
                        ex,
                        "Alpaca re-subscribe for {Symbol} failed",
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
    /// Re-send one tracked symbol's subscribe action on a fresh socket (the
    /// reconnect path): the venue holds no subscription state across
    /// sessions, so a silent reconnection would otherwise keep subscriptions
    /// "live" while no data flows. The session (connect + auth) is ensured
    /// here too — the queue consumer may run before the transport's own
    /// reconnect has finished or after the lazy start's connect.
    /// </summary>
    private async Task ResendSubscribeAsync(Symbol symbol, CancellationToken ct)
    {
        await _sendGate.WaitAsync(ct);
        try
        {
            await EnsureSessionAsync(ct);
            await SendSubscribeAsync(symbol, _lifetimeCts.Token);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private void HandleConnectionStateChanged(bool isConnected)
    {
        IsConnected = _wsClient.IsConnected;
        Logger.LogInformation(
            "Alpaca market-data stream connection state: {State} at {Url}",
            isConnected ? "Connected" : "Disconnected",
            _wsUrl);

        if (!isConnected)
        {
            // The venue holds no session state across sockets: the next
            // session re-authenticates (and every tracked symbol's
            // re-subscribe rides the queue below).
            _authenticated = false;
            return;
        }

        foreach (var symbol in _subscribed.Values)
        {
            _resubscribeQueue.Writer.TryWrite(symbol);
        }
    }

    #endregion

    #region Private Methods: frames

    /// <summary>
    /// Alpaca v2 stream dispatch: EVERY message is a JSON array — control
    /// frames arrive as arrays of one, data frames may batch several events.
    /// Errors from one message never leave the handler.
    /// </summary>
    private void HandleWebSocketMessage(string message)
    {
        try
        {
            using var json = JsonDocument.Parse(message);
            var root = json.RootElement;

            if (root.ValueKind != JsonValueKind.Array)
            {
                Logger.LogWarning(
                    "Alpaca stream message is not the protocol's event array, dropped: {MessageKind}",
                    root.ValueKind.ToString());
                return;
            }

            foreach (var element in root.EnumerateArray())
            {
                HandleStreamEvent(element);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error handling Alpaca WebSocket message");
        }
    }

    private void HandleStreamEvent(JsonElement element)
    {
        var type = TryGetString(element, TypeMember);
        switch (type)
        {
            case TradeMessageType:
                HandleTradeEvent(element);
                break;
            case QuoteMessageType:
                HandleQuoteEvent(element);
                break;
            case DailyBarMessageType:
                HandleDailyBarEvent(element);
                break;
            case SuccessMessageType:
                HandleSuccessEvent(element);
                break;
            case ErrorMessageType:
                HandleErrorEvent(element);
                break;
            case SubscriptionMessageType:
                // The server's subscription echo: quiet by design.
                break;
            case MinuteBarMessageType or UpdatedBarMessageType:
                // Never subscribed by this adapter (the chart's candles ride
                // the REST bars): an arrival is an anomaly and is named.
                Logger.LogWarning(
                    "Alpaca stream carried an unexpected {Type} event for {Symbol}, dropped",
                    type,
                    TryGetString(element, SymbolMember));
                break;
            case not null when AutoAddedMessageTypes.Contains(type):
                // A trades subscription auto-adds the corrections and
                // cancel/errors channels. Recorded, not applied (this
                // wave's tape serves the trade facts as they arrived; the
                // corrected view is a recorded future item).
                break;
            case null:
                Logger.LogWarning("Alpaca stream event carries no {Type} member, dropped", TypeMember);
                break;
            default:
                Logger.LogWarning(
                    "Alpaca stream carried an unknown event type {Type}, dropped",
                    type);
                break;
        }
    }

    /// <summary>Resolve the tracked symbol an event names: a frame for a symbol this adapter never subscribed is dropped with a warning (never guessed).</summary>
    private Symbol? ResolveTrackedSymbol(JsonElement element)
    {
        var wireSymbol = TryGetString(element, SymbolMember);
        if (wireSymbol is null)
        {
            return null;
        }

        return _subscribed.TryGetValue(wireSymbol, out var symbol) ? symbol : null;
    }

    /// <summary>
    /// Map one trade event into the tape AND the ticker merge: the wire
    /// serves NO aggressor side (a trade is a matched buy and a matched
    /// sell), so the tape carries <see cref="OrderSide.None"/> — the
    /// price, the size and the time are the honest facts, the side is never
    /// fabricated.
    /// </summary>
    private void HandleTradeEvent(JsonElement element)
    {
        var symbol = ResolveTrackedSymbol(element);
        if (symbol is null)
        {
            LogUntrackedFrame(TradeMessageType, element);
            return;
        }

        if (!TryGetDecimal(element, "p", out var price) ||
            !TryGetDecimal(element, "s", out var size) ||
            element.TryGetProperty("t", out var timeProp) == false ||
            timeProp.ValueKind != JsonValueKind.String)
        {
            Logger.LogWarning(
                "Alpaca trade event for {Symbol} carries no parseable price, size or time, dropped",
                symbol.Normalized);
            return;
        }

        var id = element.TryGetProperty("i", out var idProp) &&
                 idProp.ValueKind == JsonValueKind.Number
            ? idProp.GetRawText()
            : string.Empty;

        PublishTrade(new MarketTrade
        {
            Id = id,
            Symbol = symbol,
            Side = OrderSide.None,
            Price = new Price(price),
            Quantity = new Quantity(size),
            Timestamp = ParseRfc3339Timestamp(timeProp),
        });

        var state = _tickerStates.GetOrAdd(symbol.Normalized, _ => new StockTickerState());
        state.LastPrice = price;
        state.HasLastPrice = true;
        PublishTickerIfPriced(symbol, state, ParseRfc3339Timestamp(timeProp));
    }

    /// <summary>
    /// Map one quote event: the venue's TOP OF BOOK. A zero price means NO
    /// active quote on that side — that side is served EMPTY, never padded
    /// with a zero level. The book carries the frame's local receive moment
    /// (T-142); the merge feeds the ticker's bid/ask (the quote ts is the
    /// event fact).
    /// </summary>
    private void HandleQuoteEvent(JsonElement element)
    {
        var symbol = ResolveTrackedSymbol(element);
        if (symbol is null)
        {
            LogUntrackedFrame(QuoteMessageType, element);
            return;
        }

        var hasBid = TryGetDecimal(element, "bp", out var bidPrice) && bidPrice > 0m;
        var hasAsk = TryGetDecimal(element, "ap", out var askPrice) && askPrice > 0m;
        if ((!hasBid && !hasAsk) ||
            element.TryGetProperty("t", out var timeProp) == false ||
            timeProp.ValueKind != JsonValueKind.String)
        {
            Logger.LogWarning(
                "Alpaca quote event for {Symbol} carries no active quote or no parseable time, dropped",
                symbol.Normalized);
            return;
        }

        var bids = new List<PriceLevel>(hasBid ? 1 : 0);
        var asks = new List<PriceLevel>(hasAsk ? 1 : 0);
        if (hasBid && TryGetDecimal(element, "bs", out var bidSize))
        {
            bids.Add(new PriceLevel(bidPrice, bidSize));
        }

        if (hasAsk && TryGetDecimal(element, "as", out var askSize))
        {
            asks.Add(new PriceLevel(askPrice, askSize));
        }

        PublishOrderBook(new OrderBook
        {
            Symbol = symbol,
            Bids = bids,
            Asks = asks,
            // The book stamp is the frame's local receive moment (T-142):
            // the WS-latency histogram measures transport, not clock offset.
            Timestamp = DateTimeOffset.UtcNow,
        });

        var state = _tickerStates.GetOrAdd(symbol.Normalized, _ => new StockTickerState());
        if (hasBid)
        {
            state.Bid = bidPrice;
        }

        if (hasAsk)
        {
            state.Ask = askPrice;
        }

        // The quote only enriches the ticker state (bid/ask): the trade
        // event is this venue stream's only last-price source, so the
        // ticker's own publish rides the trade — a quote never republishes
        // a last price it did not carry.
    }

    /// <summary>
    /// Map one dailyBar event: the running session volume the ticker's
    /// volume member serves (the venue's own aggregate — the wire fact this
    /// adapter publishes; nothing is derived or extrapolated).
    /// </summary>
    private void HandleDailyBarEvent(JsonElement element)
    {
        var symbol = ResolveTrackedSymbol(element);
        if (symbol is null)
        {
            LogUntrackedFrame(DailyBarMessageType, element);
            return;
        }

        if (!TryGetDecimal(element, "v", out var volume) ||
            element.TryGetProperty("t", out var timeProp) == false ||
            timeProp.ValueKind != JsonValueKind.String)
        {
            Logger.LogWarning(
                "Alpaca dailyBar event for {Symbol} carries no parseable volume or time, dropped",
                symbol.Normalized);
            return;
        }

        var state = _tickerStates.GetOrAdd(symbol.Normalized, _ => new StockTickerState());
        state.Volume = volume;
        // The dailyBar only enriches the ticker state (the running session
        // volume): the ticker's own publish rides the trade event — the
        // stream's only last-price source.
    }

    /// <summary>
    /// Publish the symbol's merged ticker — gated on the wire having said a
    /// last price (never a fabricated one). Only the trade handler calls
    /// it: the trade is the stream's only last-price source, so the
    /// enriched bid/ask/volume ride out on the trade's own publish.
    /// </summary>
    private void PublishTickerIfPriced(Symbol symbol, StockTickerState state, DateTimeOffset timestamp)
    {
        if (!state.HasLastPrice)
        {
            return;
        }

        PublishTicker(new Ticker(
            symbol,
            state.LastPrice,
            state.Bid,
            state.Ask,
            state.Volume,
            // The v2 IEX stream serves no 24h-change basis (no previous
            // close rides the stream): the percent stays the honest zero —
            // the same served-fact discipline the Bybit spot tickers apply
            // to their unserved bid/ask.
            0m,
            timestamp));
    }

    private void LogUntrackedFrame(string type, JsonElement element)
    {
        Logger.LogWarning(
            "Alpaca stream carried a {Type} event for an untracked symbol {Symbol}, dropped",
            type,
            TryGetString(element, SymbolMember) ?? "(none)");
    }

    private void HandleSuccessEvent(JsonElement element)
    {
        var message = TryGetString(element, "msg");
        if (!string.Equals(message, "authenticated", StringComparison.Ordinal))
        {
            // The connect hello ("connected") and any other success text:
            // quiet by design.
            return;
        }

        Volatile.Read(ref _authAckSignal)?.TrySetResult(true);
    }

    private void HandleErrorEvent(JsonElement element)
    {
        var code = element.TryGetProperty("code", out var codeProp) && codeProp.ValueKind == JsonValueKind.Number
            ? codeProp.GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "(no code)";
        var message = TryGetString(element, "msg") ?? "(no message)";

        Volatile.Read(ref _authAckSignal)?.TrySetResult(false);
        Logger.LogError(
            "Alpaca stream answered an error: code {Code}, message {Message}",
            code,
            message);
    }

    #endregion

    #region Private Methods: REST

    /// <summary>One bars page: the rate-limited GET and the parsed payload (the bars array and the next-page token when the server promises one).</summary>
    private async Task<(JsonElement Bars, string NextPageToken)> GetBarsPageAsync(
        RestLane lane,
        string url,
        CancellationToken ct)
    {
        var response = await SendRateLimitedGetAsync(lane, MarketDataBudgetKey, RequestWeight, url, ct);
        response.EnsureSuccessStatusCode();

        using var document = await response.Content.ReadFromJsonAsync<JsonDocument>(options: null, cancellationToken: ct);
        var root = document?.RootElement
            ?? throw new InvalidOperationException($"Alpaca REST request returned an empty body: {url}");

        var nextToken = root.TryGetProperty("next_page_token", out var tokenProp) &&
                        tokenProp.ValueKind == JsonValueKind.String
            ? tokenProp.GetString() ?? string.Empty
            : string.Empty;

        return (root.Clone(), nextToken);
    }

    /// <summary>
    /// One outbound REST GET through the rate limiter (T-106): the acquire
    /// waits before the send, the request carries the secret-store key
    /// headers (fetched per request — the credentials never ride anything
    /// but the store), and a 429 feeds the venue's retry guidance back into
    /// the limiter so every lane parks for the span.
    /// </summary>
    private async Task<HttpResponseMessage> SendRateLimitedGetAsync(
        RestLane lane,
        string endpointKey,
        int weight,
        string url,
        CancellationToken ct)
    {
        await _restLimiter.AcquireAsync(lane, endpointKey, weight, ct);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("APCA-API-KEY-ID", await RequireApiKeyAsync(ct));
        request.Headers.Add("APCA-API-SECRET-KEY", await RequireApiSecretAsync(ct));
        var response = await _httpClient.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            _restLimiter.ReportBlocked(endpointKey, RestRetryAfterParser.Parse(response.Headers));
        }

        return response;
    }

    private async Task<string> RequireApiKeyAsync(CancellationToken ct)
    {
        var key = await _secrets.GetAsync(ApiKeySecretKey, ct);
        return string.IsNullOrWhiteSpace(key)
            ? throw RefuseAbsentCredentials([ApiKeySecretKey])
            : key;
    }

    private async Task<string> RequireApiSecretAsync(CancellationToken ct)
    {
        var secret = await _secrets.GetAsync(ApiSecretSecretKey, ct);
        return string.IsNullOrWhiteSpace(secret)
            ? throw RefuseAbsentCredentials([ApiKeySecretKey, ApiSecretSecretKey])
            : secret;
    }

    /// <summary>
    /// Refuse one authorized surface with the standing absence error
    /// (T-227): the typed exception lets the consumers tell the standing
    /// absence from a real failure, and the session's first absence fires
    /// the one quiet information line — the repeated refusals never log.
    /// </summary>
    /// <param name="missedKeys">The ISecretStore keys the store missed.</param>
    private AdapterCredentialsMissingException RefuseAbsentCredentials(IReadOnlyList<string> missedKeys)
    {
        if (Interlocked.Exchange(ref _absenceLogged, 1) == 0)
        {
            Logger.LogInformation(
                "Alpaca runs without market-data credentials; the data surface stays unavailable until the pair is saved through the settings' exchange-credentials section");
        }

        return new AdapterCredentialsMissingException(Id, [.. missedKeys]);
    }

    /// <summary>The bars request's timestamp text: RFC-3339 in UTC (the v2 API's own form).</summary>
    private static string ConvertRestTimestamp(DateTimeOffset moment) =>
        moment.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// The v2 timeframe text the chart's candles request rides (the
    /// multiplier-unit form the API publishes).
    /// </summary>
    private static string ConvertTimeFrame(TimeFrame timeFrame)
    {
        return timeFrame switch
        {
            TimeFrame.M1 => "1Min",
            TimeFrame.M5 => "5Min",
            TimeFrame.M15 => "15Min",
            TimeFrame.M30 => "30Min",
            TimeFrame.H1 => "1Hour",
            TimeFrame.H4 => "4Hour",
            TimeFrame.D1 => "1Day",
            TimeFrame.W1 => "1Week",
            TimeFrame.MN1 => "1Month",
            _ => throw new ArgumentOutOfRangeException(nameof(timeFrame)),
        };
    }

    /// <summary>
    /// Parse one RFC-3339 stream or bar timestamp: the v2 wire carries
    /// NANOSECOND fractions (<c>2021-02-22T15:51:45.335689322Z</c>) and a
    /// naive parse would drop every such event — the fraction is trimmed to
    /// <see cref="DateTimeOffset"/>'s 7-digit precision before the parse.
    /// </summary>
    private static DateTimeOffset ParseRfc3339Timestamp(JsonElement property)
    {
        var text = property.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new FormatException("An Alpaca timestamp member was blank");
        }

        var dot = text.IndexOf('.', StringComparison.Ordinal);
        if (dot >= 0)
        {
            var fractionStart = dot + 1;
            var fractionEnd = fractionStart;
            while (fractionEnd < text.Length && char.IsAsciiDigit(text[fractionEnd]))
            {
                fractionEnd++;
            }

            var fractionLength = fractionEnd - fractionStart;
            if (fractionLength > 7)
            {
                text = string.Concat(text.AsSpan(0, fractionStart), text.AsSpan(fractionStart, 7), text.AsSpan(fractionEnd));
            }
        }

        return DateTimeOffset.Parse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);
    }

    /// <summary>Read one REQUIRED decimal member: the v2 wire carries numbers (not strings) — the raw text parses invariantly.</summary>
    private static decimal GetDecimal(JsonElement property)
    {
        if (property.ValueKind != JsonValueKind.Number ||
            !decimal.TryParse(property.GetRawText(), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            throw new FormatException(
                $"An Alpaca numeric member was not a parseable number: {property.GetRawText()}");
        }

        return parsed;
    }

    /// <summary>Read one OPTIONAL decimal member: a present-and-parseable number returns it, an absent or non-number member returns false.</summary>
    private static bool TryGetDecimal(JsonElement element, string property, out decimal value)
    {
        value = 0m;
        if (!element.TryGetProperty(property, out var prop) ||
            prop.ValueKind != JsonValueKind.Number ||
            !decimal.TryParse(prop.GetRawText(), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private static string? TryGetString(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

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

        _messageSubscription.Dispose();
        _connectionSubscription.Dispose();

        await base.DisposeAsync();
        await _wsClient.DisposeAsync();
        _sendGate.Dispose();
        // HttpClient comes from IHttpClientFactory; its lifetime is managed there.
    }

    #endregion

    #region Nested Types

    /// <summary>
    /// One stock symbol's last known ticker facts (T-198): the v2 wire sends
    /// SEPARATE trade, quote and dailyBar events, so the merge keeps the
    /// previous value of every channel a frame does not carry. Mutable by
    /// design — the merge owns it; the published <see cref="Ticker"/> stays
    /// immutable. Publication rides <see cref="HasLastPrice"/>: nothing
    /// publishes before the wire said a last price, and the bid/ask and
    /// volume members stay the honest zeros until their channels serve.
    /// </summary>
    private sealed class StockTickerState
    {
        /// <summary>The last seen last price (the publish gate rides it).</summary>
        public decimal LastPrice;

        /// <summary>Whether the wire has said a last price yet.</summary>
        public bool HasLastPrice;

        /// <summary>The last seen best bid (zero until the quote channel serves one; the zero reads as the unserved fact it is).</summary>
        public decimal Bid;

        /// <summary>The last seen best ask (zero until the quote channel serves one).</summary>
        public decimal Ask;

        /// <summary>The last seen running session volume (zero until the dailyBars channel serves one).</summary>
        public decimal Volume;
    }

    private sealed record AuthAction(
        [property: JsonPropertyName("action")] string Action,
        [property: JsonPropertyName("key")] string Key,
        [property: JsonPropertyName("secret")] string Secret)
    {
        public AuthAction(string key, string secret)
            : this("auth", key, secret)
        {
        }
    }

    private sealed record SubscribeAction(
        [property: JsonPropertyName("action")] string Action,
        [property: JsonPropertyName("trades")] string[] Trades,
        [property: JsonPropertyName("quotes")] string[] Quotes,
        [property: JsonPropertyName("dailyBars")] string[] DailyBars)
    {
        public SubscribeAction(string[] trades, string[] quotes, string[] dailyBars)
            : this("subscribe", trades, quotes, dailyBars)
        {
        }
    }

    #endregion
}
