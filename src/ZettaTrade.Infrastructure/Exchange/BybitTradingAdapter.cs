namespace ZettaTrade.Infrastructure.Exchange;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// The Bybit venue trading adapter (T-127): the trading seam over the Bybit
/// TESTNET v5 surface. LIMIT orders only, with the full lifecycle mapping —
/// the placement rides <c>POST /v5/order/create</c>, the ack returns
/// <c>Submitted</c>, and the private stream's <c>order.spot</c> topic maps
/// the venue transitions (submit → working → fill/partial → cancel) into
/// the Core statuses the panels and the journal speak. Every other order
/// family (conditional, OCO, reduce-only, non-GTC) is an explicit error,
/// never a degraded execution, and every venue reject (<c>retCode != 0</c>)
/// surfaces as an error under the rejected-not-default contract. An
/// unmapped venue status is refused, never defaulted.
/// <para>
/// Credentials come ONLY from <see cref="ISecretStore"/> (the testnet API
/// key pair — never a command line, never a config file) and reach the
/// venue as signed v5 requests: REST headers
/// (<c>X-BAPI-API-KEY</c>/<c>X-BAPI-TIMESTAMP</c>/<c>X-BAPI-RECV-WINDOW</c>/<c>X-BAPI-SIGN</c>,
/// HMAC-SHA256 over <c>timestamp + apiKey + recvWindow + body|queryString</c>)
/// and the private-stream auth handshake
/// (HMAC-SHA256 over <c>GET/realtime{expires}</c>). Secrets are fetched per
/// operation, never cached, and are never logged.
/// </para>
/// <para>
/// The private stream connects lazily on the first order operation (an
/// idle surface holds no sockets — the demo lane's parity), keeps the
/// connection alive with the 20 s heartbeat, auths and re-subscribes
/// <c>order.spot</c> after every reconnect, and exposes the venue order
/// transitions through <see cref="OrderUpdates"/> for the journal and the
/// notification paths. Market-data members pass through to the shared
/// Bybit market-data adapter (the same venue, one socket set apiece).
/// </para>
/// </summary>
public sealed class BybitTradingAdapter : ITradingAdapter
{
    #region Constants

    /// <summary>Named HttpClientFactory client used for the REST requests.</summary>
    public const string HttpClientName = "bybit-testnet";

    /// <summary>Timeout for the REST requests, in seconds.</summary>
    public const int RequestTimeoutSeconds = 30;

    /// <summary>REST base of the v5 API, TESTNET host (the only shipped pair).</summary>
    public const string DefaultRestBaseUrl = "https://api-testnet.bybit.com";

    /// <summary>WebSocket URL of the v5 private stream, TESTNET host.</summary>
    public const string DefaultPrivateWsUrl = "wss://stream-testnet.bybit.com/v5/private";

    /// <summary>Adapter identifier: the venue identity the trading-mode resolver maps (registry key "Bybit").</summary>
    public const string ExchangeId = "bybit";

    /// <summary>Adapter display name.</summary>
    public const string ExchangeName = "Bybit (testnet)";

    /// <summary>Prefix of the client-side order ids the adapter generates.</summary>
    private const string OrderLinkIdPrefix = "zt-";

    /// <summary>Length budget of a client order id: prefix (3) + GUID N-form (32).</summary>
    private const int OrderLinkIdLength = 35;

    /// <summary>Prefix of the position ids the adapter synthesizes for the venue spot rows.</summary>
    private const string PositionIdPrefix = "bybit-testnet-pos-";

    /// <summary>v5 spot trade category this surface trades.</summary>
    private const string SpotCategory = "spot";

    /// <summary>REST path of the order placement endpoint.</summary>
    private const string CreateOrderPath = "/v5/order/create";

    /// <summary>REST path of the order cancellation endpoint.</summary>
    private const string CancelOrderPath = "/v5/order/cancel";

    /// <summary>REST path of the open-orders query (real-time working orders).</summary>
    private const string OpenOrdersPath = "/v5/order/realtime";

    /// <summary>REST path of the position list query.</summary>
    private const string PositionsPath = "/v5/position/list";

    /// <summary>REST path of the wallet balance query.</summary>
    private const string WalletBalancePath = "/v5/account/wallet-balance";

    /// <summary>The v5 request window the auth'd REST calls ship (the venue's default, in ms).</summary>
    public const string RecvWindow = "5000";

    /// <summary>How far ahead (seconds) the private auth expiry sits, tolerating clock skew.</summary>
    private const int WsAuthExpiryAheadSeconds = 30;

    /// <summary>Interval of the private-stream heartbeat, in seconds (the venue's own cadence).</summary>
    private const int KeepaliveIntervalSeconds = 20;

    /// <summary>The heartbeat frame (the venue answers with an <c>op:pong</c> control frame).</summary>
    private const string KeepalivePingFrame = """{"op":"ping"}""";

    /// <summary>The private order topic this surface subscribes (categorised: spot only).</summary>
    private const string OrderTopic = "order.spot";

    /// <summary>The principled wait for the auth ack before the subscribe rides (seconds).</summary>
    private const int AuthAckTimeoutSeconds = 10;

    /// <summary>ISecretStore key of the testnet API key.</summary>
    public const string ApiKeySecretKey = "bybit.testnet.api-key";

    /// <summary>ISecretStore key of the testnet API secret.</summary>
    public const string ApiSecretSecretKey = "bybit.testnet.api-secret";

    /// <summary>The string this surface's error paths prefix venue failures with.</summary>
    private const string VenueFailurePrefix = "Bybit testnet request failed:";

    /// <summary>The reconnect policy (the market-data adapter's shipped shape).</summary>
    public static ReconnectPolicy DefaultReconnectPolicy { get; } = BybitAdapter.DefaultReconnectPolicy;

    #endregion

    #region Fields

    private readonly IMarketDataAdapter _marketData;
    private readonly ISecretStore _secrets;
    private readonly ILogger<BybitTradingAdapter> _logger;
    private readonly HttpClient _httpClient;
    private readonly WebSocketClient _wsClient;
    private readonly IDisposable _messageSubscription;
    private readonly IDisposable _connectionSubscription;

    /// <summary>Serializes order placement / cancellation; the venue state mutates under it.</summary>
    private readonly SemaphoreSlim _tradingGate = new(1, 1);

    /// <summary>Serializes the connect/auth flows (place-time connect and the reconnect re-auth).</summary>
    private readonly SemaphoreSlim _sessionGate = new(1, 1);

    /// <summary>Serializes every outbound frame across the session flows and the keepalive.</summary>
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    /// <summary>The adapter's order state: one Core order per client id (the accepted surface).</summary>
    private readonly ConcurrentDictionary<string, Order> _orders = new();

    /// <summary>The placement announcer: the adapter's order-updates stream for the ledger and notifications.</summary>
    private readonly Subject<Order> _orderUpdates = new();

    /// <summary>The session flows (auth + subscribe after a connect): one consumer serves them FIFO.</summary>
    private readonly Channel<bool> _sessionQueue = Channel.CreateUnbounded<bool>(
        new UnboundedChannelOptions { SingleReader = true });

    /// <summary>Adapter-owned lifetime; the keepalive and session loops are awaited on disposal.</summary>
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly Task _keepaliveLoop;
    private readonly Task _sessionLoop;

    private volatile TaskCompletionSource<bool>? _authAckSignal;
    private volatile bool _authenticated;
    private int _disposed;

    #endregion

    #region Properties

    /// <summary>The adapter of the Bybit venue trading surface (the venue identity the resolver maps by).</summary>
    public string Id => ExchangeId;

    /// <summary>The adapter's display name.</summary>
    public string Name => ExchangeName;

    /// <summary>The private socket's connection state (the trading transport).</summary>
    public bool IsConnected => _wsClient.IsConnected;

    /// <summary>The surface's capabilities: trading and account reads over its own venue market data.</summary>
    public ExchangeCapabilities Capabilities =>
        ExchangeCapabilities.Trading | ExchangeCapabilities.AccountInfo | ExchangeCapabilities.MarketData
        | ExchangeCapabilities.OrderBook | ExchangeCapabilities.HistoricalData;

    /// <summary>
    /// The venue order transitions (submit → working → fill/partial → cancel
    /// → reject), one emit per transition the private stream mapped. The
    /// journal and the notification surfaces consume; the adapter publishes
    /// only — it never journals itself.
    /// </summary>
    public IObservable<Order> OrderUpdates => _orderUpdates.AsObservable();

    /// <summary>The transport endpoints the adapter re-hosts (internal: the registration tests assert the testnet pair).</summary>
    internal BybitTradingEndpoints Endpoints { get; }

    #endregion

    #region Constructor

    /// <summary>
    /// Create the adapter over the testnet host pair, its venue market data
    /// for the pass-through members, and the secret store the testnet API
    /// key pair rides. Endpoints must carry non-empty members — a blank pair
    /// is a configuration error, thrown immediately (a misconfigured
    /// trading transport must never boot half-configured).
    /// </summary>
    public BybitTradingAdapter(
        ILogger<BybitTradingAdapter> logger,
        IHttpClientFactory httpClientFactory,
        IMarketDataAdapter marketData,
        ISecretStore secrets,
        BybitTradingEndpoints? endpoints = null,
        ReconnectSupervisor? reconnectSupervisor = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _marketData = marketData ?? throw new ArgumentNullException(nameof(marketData));
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));

        var pair = endpoints ?? BybitTradingEndpoints.Testnet;
        if (string.IsNullOrWhiteSpace(pair.RestBaseUrl) || string.IsNullOrWhiteSpace(pair.PrivateWsUrl))
        {
            throw new ArgumentException(
                "Configured Bybit trading endpoints must carry non-empty RestBaseUrl and PrivateWsUrl",
                nameof(endpoints));
        }

        Endpoints = pair;

        _httpClient = httpClientFactory.CreateClient(HttpClientName);
        _wsClient = new WebSocketClient(
            pair.PrivateWsUrl,
            _logger,
            DefaultReconnectPolicy,
            reconnectSupervisor?.For(ExchangeId, DefaultReconnectPolicy));

        _messageSubscription = _wsClient.MessageStream.Subscribe(HandleWebSocketMessage);
        _connectionSubscription = _wsClient.ConnectionStateStream.Subscribe(HandleConnectionStateChanged);

        _keepaliveLoop = RunKeepaliveLoopAsync(_lifetimeCts.Token);
        _sessionLoop = RunSessionLoopAsync(_lifetimeCts.Token);
    }

    #endregion

    #region Market data (delegated to the shared venue adapter)

    /// <summary>Connect the trading surface: the private session (lazy until the first order operation).</summary>
    public Task ConnectAsync(CancellationToken ct = default) => EnsurePrivateSessionAsync(ct);

    /// <inheritdoc/>
    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        _authenticated = false;
        await _wsClient.DisconnectAsync();
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<Symbol>> GetSymbolsAsync(CancellationToken ct = default) =>
        _marketData.GetSymbolsAsync(ct);

    /// <inheritdoc/>
    public IObservable<Ticker> SubscribeTicker(Symbol symbol) => _marketData.SubscribeTicker(symbol);

    /// <inheritdoc/>
    public IObservable<OrderBook> SubscribeOrderBook(Symbol symbol, int depth = 20) =>
        _marketData.SubscribeOrderBook(symbol, depth);

    /// <inheritdoc/>
    public IObservable<MarketTrade> SubscribeTrades(Symbol symbol) => _marketData.SubscribeTrades(symbol);

    /// <inheritdoc/>
    public Task<IReadOnlyList<Bar>> GetBarsAsync(
        Symbol symbol,
        TimeFrame timeFrame,
        DateTimeOffset from,
        DateTimeOffset? to = null,
        int limit = 500,
        CancellationToken ct = default) =>
        _marketData.GetBarsAsync(symbol, timeFrame, from, to, limit, ct);

    #endregion

    #region Trading

    /// <summary>
    /// Place one LIMIT GTC order on the testnet venue. Outside the surface's
    /// supported families the request is refused before anything touches
    /// the venue; a venue reject throws with its own retMsg (never a
    /// defaulted order, never a paper-shape substitution).
    /// </summary>
    public async Task<Order> PlaceOrderAsync(OrderRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        AssertSupportedRequest(request);
        request.Validate();

        await _tradingGate.WaitAsync(ct);
        try
        {
            var credentials = await FetchTestnetCredentialsAsync(ct);
            await EnsurePrivateSessionAsync(ct);

            var orderLinkId = request.ClientOrderId ?? NewOrderLinkId();
            // Post-only spot limits (T-191) ride the venue's own maker-only
            // time-in-force token; a plain GTC limit keeps its token.
            var body = new OrderCreateRequest(
                SpotCategory,
                request.Symbol.GetExchangeFormat(ExchangeId),
                request.Side == OrderSide.Buy ? FrameSides.Buy : FrameSides.Sell,
                OrderTypeNames.Limit,
                request.Quantity.Value.ToString(CultureInfo.InvariantCulture),
                request.Price!.Value.Value.ToString(CultureInfo.InvariantCulture),
                request.PostOnly ? TimeInForceNames.PostOnly : TimeInForceNames.Gtc,
                orderLinkId);

            var result = await SendRestAsync(
                HttpMethod.Post,
                CreateOrderPath,
                string.Empty,
                body,
                credentials,
                ct);
            var venueOrderId = TryGetString(result, "orderId");
            var echoedLink = TryGetString(result, "orderLinkId");

            var order = new Order
            {
                Id = string.IsNullOrEmpty(echoedLink) ? orderLinkId : echoedLink,
                ExchangeId = ExchangeId,
                ExchangeOrderId = string.IsNullOrEmpty(venueOrderId) ? orderLinkId : venueOrderId,
                Symbol = request.Symbol,
                Side = request.Side,
                Type = OrderType.Limit,
                Quantity = request.Quantity,
                Price = request.Price.Value,
                TimeInForce = TimeInForce.GTC,
                PostOnly = request.PostOnly,
                ClientOrderId = orderLinkId,
                Status = OrderStatus.Submitted,
                FilledQuantity = Quantity.Zero,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            _orders[order.Id] = order;

            _logger.LogInformation(
                "Bybit testnet place: {OrderId} {Side} {Quantity} {Symbol} limit {LimitPrice}",
                order.Id,
                order.Side,
                order.Quantity.Value,
                order.Symbol.Normalized,
                order.Price.Value);

            return order;
        }
        finally
        {
            _tradingGate.Release();
        }
    }

    /// <summary>
    /// Cancel one order. The symbol is mandated by the contract (the venue
    /// cancel is symbol-scoped) and the order id is the one this surface
    /// issued (the client order link id). A venue reject throws.
    /// </summary>
    public async Task CancelOrderAsync(Symbol symbol, string orderId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        ArgumentException.ThrowIfNullOrWhiteSpace(orderId);

        await _tradingGate.WaitAsync(ct);
        try
        {
            var credentials = await FetchTestnetCredentialsAsync(ct);
            var body = new OrderCancelRequest(
                SpotCategory,
                symbol.GetExchangeFormat(ExchangeId),
                orderId);

            await SendRestAsync(
                HttpMethod.Post,
                CancelOrderPath,
                string.Empty,
                body,
                credentials,
                ct);

            _logger.LogInformation(
                "Bybit testnet cancel: {OrderId} on {Symbol} — the cancelled state follows on the order stream",
                orderId,
                symbol.Normalized);
        }
        finally
        {
            _tradingGate.Release();
        }
    }

    /// <summary>The open (working) orders of the venue account, mapped strictly.</summary>
    public async Task<IReadOnlyList<Order>> GetOpenOrdersAsync(CancellationToken ct = default)
    {
        await _tradingGate.WaitAsync(ct);
        try
        {
            var credentials = await FetchTestnetCredentialsAsync(ct);
            var query = $"category={SpotCategory}";
            var result = await SendRestAsync(HttpMethod.Get, OpenOrdersPath, query, null, credentials, ct);

            return MapOpenOrders(result);
        }
        finally
        {
            _tradingGate.Release();
        }
    }

    /// <summary>The open positions of the venue spot account, mapped strictly.</summary>
    public async Task<IReadOnlyList<Position>> GetPositionsAsync(CancellationToken ct = default)
    {
        await _tradingGate.WaitAsync(ct);
        try
        {
            var credentials = await FetchTestnetCredentialsAsync(ct);
            var query = $"category={SpotCategory}";
            var result = await SendRestAsync(HttpMethod.Get, PositionsPath, query, null, credentials, ct);

            return MapPositions(result);
        }
        finally
        {
            _tradingGate.Release();
        }
    }

    /// <summary>The wallet balances of the unified account, mapped strictly.</summary>
    public async Task<IReadOnlyDictionary<string, Money>> GetBalancesAsync(CancellationToken ct = default)
    {
        await _tradingGate.WaitAsync(ct);
        try
        {
            var credentials = await FetchTestnetCredentialsAsync(ct);
            var query = $"accountType={WalletAccountType.Unified}";
            var result = await SendRestAsync(HttpMethod.Get, WalletBalancePath, query, null, credentials, ct);

            return MapBalances(result);
        }
        finally
        {
            _tradingGate.Release();
        }
    }

    #endregion

    #region Private Methods: type checks

    /// <summary>
    /// The surface trades LIMIT GTC spot orders only: every other family
    /// (market, conditional, OCO, bracket, reduce-only, non-GTC) is an
    /// explicit error before anything touches the venue — no degraded
    /// execution, no substituted execution shape. The limit price
    /// requirement rides the request's own validation below.
    /// </summary>
    private static void AssertSupportedRequest(OrderRequest request)
    {
        if (request.BracketLegs is not null)
        {
            throw new InvalidOperationException(
                "The Bybit testnet trading surface does not carry bracket orders " +
                "(an entry with attached take-profit/stop-loss exits); the request is refused, not degraded.");
        }

        if (request.Type != OrderType.Limit)
        {
            throw new InvalidOperationException(
                $"The Bybit testnet trading surface trades Limit orders only; a {request.Type} order is refused, not degraded.");
        }

        if (request.TimeInForce != TimeInForce.GTC)
        {
            throw new InvalidOperationException(
                $"The Bybit testnet trading surface ships GoodTillCancelled limit orders only; a {request.TimeInForce} request is refused, not degraded.");
        }

        if (request.OcoGroupId is not null)
        {
            throw new InvalidOperationException(
                "The Bybit testnet trading surface does not carry OCO pairs; the leg is refused, not degraded.");
        }

        if (request.ReduceOnly)
        {
            throw new InvalidOperationException(
                "The Bybit testnet spot surface carries no reduce-only orders; the request is refused, not degraded.");
        }
    }

    #endregion

    #region Private Methods: credentials

    /// <summary>
    /// The testnet API key pair from the secret store, fetched per operation
    /// (the operator may re-store credentials without a restart). A missing
    /// key pair is an explicit error — the venue request is refused, never
    /// silently de-credentials or substituted.
    /// </summary>
    private async Task<BybitCredentials> FetchTestnetCredentialsAsync(CancellationToken ct)
    {
        var apiKey = await _secrets.GetAsync(ApiKeySecretKey, ct);
        var apiSecret = await _secrets.GetAsync(ApiSecretSecretKey, ct);

        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(apiSecret))
        {
            throw new InvalidOperationException(
                $"Bybit testnet credentials are not available in the secret store (keys '{ApiKeySecretKey}' and '{ApiSecretSecretKey}'); " +
                "the venue request is refused rather than sent unauthenticated.");
        }

        return new BybitCredentials(apiKey, apiSecret);
    }

    #endregion

    #region Private Methods: session flows

    /// <summary>
    /// One session flow through the serialized send gate: the v5 auth
    /// handshake (the signature over GET/realtime + the expiry), the ack
    /// wait, then the order-topic subscribe. A failed or refused auth logs
    /// the error and leaves the session unauthorized — the next place-time
    /// connect attempt takes a fresh session (the transport's own reconnect
    /// policy governs the socket).
    /// </summary>
    private async Task ServeSessionAsync()
    {
        try
        {
            var credentials = await FetchTestnetCredentialsAsync(_lifetimeCts.Token);
            var expires = DateTimeOffset.UtcNow
                .AddSeconds(WsAuthExpiryAheadSeconds)
                .ToUnixTimeMilliseconds();
            var signature = SignHmacSha256Hex(credentials.ApiSecret, $"{OAuthFieldNames.SignatureRealm}{expires}");

            var ackSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _authAckSignal = ackSignal;

            await SendUnderGateAsync(
                new PrivateAuthFrame(FrameOps.Auth, [credentials.ApiKey, expires, signature]));

            var success = await ackSignal.Task.WaitAsync(
                TimeSpan.FromSeconds(AuthAckTimeoutSeconds),
                _lifetimeCts.Token);
            if (!success)
            {
                throw new InvalidOperationException(
                    "Bybit testnet private-stream auth was refused by the venue; the session stays unauthorized.");
            }

            await SendUnderGateAsync(new SubscriptionFrame(FrameOps.Subscribe, [OrderTopic]));
            _logger.LogInformation(
                "Bybit testnet private stream authenticated and subscribed to {OrderTopic}",
                OrderTopic);
        }
        catch (OperationCanceledException)
        {
            // Disposal: the only exit of a session flow.
        }
        catch (TimeoutException)
        {
            _authenticated = false;
            _logger.LogError(
                "Bybit testnet private-stream auth ack did not arrive within {TimeoutSeconds}s; the next order operation re-runs the session flow",
                AuthAckTimeoutSeconds);
        }
        catch (Exception ex)
        {
            _authenticated = false;
            _logger.LogError(ex, "Bybit testnet private-stream session flow failed");
        }
    }

    /// <summary>Send one control frame under the serialized send gate.</summary>
    private async Task SendUnderGateAsync(object frame)
    {
        await _sendGate.WaitAsync(_lifetimeCts.Token);
        try
        {
            await _wsClient.SendJsonAsync(frame, _lifetimeCts.Token);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <summary>
    /// The ONE consumer of the session queue: entries are served strictly
    /// one at a time, so a reconnect and first connect cannot race their
    /// auth frames. Never fire-and-forget: the loop task is tracked and
    /// awaited on disposal.
    /// </summary>
    private async Task RunSessionLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var _ in _sessionQueue.Reader.ReadAllAsync(ct))
            {
                await ServeSessionAsync();
            }
        }
        catch (OperationCanceledException)
        {
            // Disposal: the only exit of the session pump.
        }
    }

    /// <summary>
    /// The place-time session entry (ConnectAsync): when the socket is not
    /// open, connect and enqueue the auth flow. When it is open, the
    /// standing session serves (the auth keeps the order topic subscribed);
    /// an unsigned session is re-enqueued idempotently.
    /// </summary>
    private async Task EnsurePrivateSessionAsync(CancellationToken ct)
    {
        await _sessionGate.WaitAsync(ct);
        try
        {
            if (!_wsClient.IsConnected)
            {
                await _wsClient.ConnectAsync(ct);
                _sessionQueue.Writer.TryWrite(true);
            }
            else if (!_authenticated)
            {
                _sessionQueue.Writer.TryWrite(true);
            }
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    /// <summary>
    /// The reconnect handler: after every fresh socket, the session queue
    /// serves the auth+subscribe flow (the server holds no auth across
    /// sockets, so a silent reconnect would keep the socket live while no
    /// order updates flow). The keepalive loop continues undisturbed.
    /// </summary>
    private void HandleConnectionStateChanged(bool isConnected)
    {
        _authenticated = false;
        if (isConnected)
        {
            _sessionQueue.Writer.TryWrite(true);
        }
    }

    /// <summary>
    /// The heartbeat: the private sockets disconnect when silent — the
    /// adapter sends the ping frame every cadence on an adapter-owned timer
    /// (the loop task is tracked and awaited on disposal, never abandoned).
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
                    _logger.LogWarning(ex, "Bybit testnet keepalive ping failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Disposal: the only exit of the keepalive loop.
        }
    }

    /// <summary>
    /// The private-stream frame dispatch: control frames carry the
    /// auth/subscribe verdicts, the <c>order.spot</c> data frames map venue
    /// order transitions. Errors from one message never leave the handler.
    /// </summary>
    private void HandleWebSocketMessage(string message)
    {
        try
        {
            using var json = JsonDocument.Parse(message);
            var root = json.RootElement;
            var op = TryGetString(root, "op");

            if (root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True)
            {
                var opName = op ?? string.Empty;
                if (opName == FrameOps.Auth)
                {
                    _authenticated = true;
                    _ = _authAckSignal?.TrySetResult(true);
                    return;
                }

                if (opName == FrameOps.Subscribe)
                {
                    _logger.LogInformation("Bybit testnet private stream subscribed to the order topic");
                    return;
                }

                return;
            }

            if (root.TryGetProperty("success", out var failure) &&
                failure.ValueKind == JsonValueKind.False &&
                !string.IsNullOrEmpty(op))
            {
                _authenticated = false;
                var retMsg = TryGetString(root, "ret_msg") ?? "no message";
                if (op == FrameOps.Auth)
                {
                    _ = _authAckSignal?.TrySetResult(false);
                }

                _logger.LogError("Bybit testnet private stream {Op} failed: {RetMsg}", op, retMsg);
                return;
            }

            if (root.TryGetProperty("topic", out var topic) &&
                string.Equals(topic.GetString(), OrderTopic, StringComparison.Ordinal) &&
                root.TryGetProperty("data", out var data))
            {
                HandleOrderFrame(data);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling Bybit testnet private-stream message");
        }
    }

    #endregion

    #region Private Methods: signing + REST

    /// <summary>
    /// One signed v5 REST call: the auth headers the venue's spec demands
    /// (the signature over timestamp + apiKey + recvWindow + body for POST)
    /// and queryString for GET. A non-zero <c>retCode</c> is a protocol-level
    /// failure (HTTP may still be 200) and throws — never a defaulted
    /// result. The <c>result</c> member is the value.
    /// </summary>
    private async Task<JsonElement> SendRestAsync(
        HttpMethod method,
        string path,
        string queryString,
        object? body,
        BybitCredentials credentials,
        CancellationToken ct)
    {
        var jsonBody = body is null ? string.Empty : JsonSerializer.Serialize(body);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        var signPayload = queryString.Length > 0
            ? timestamp + credentials.ApiKey + RecvWindow + queryString
            : timestamp + credentials.ApiKey + RecvWindow + jsonBody;
        var signature = SignHmacSha256Hex(credentials.ApiSecret, signPayload);

        var url = $"{Endpoints.RestBaseUrl}{path}{(queryString.Length > 0 ? $"?{queryString}" : string.Empty)}";
        using var message = new HttpRequestMessage(method, url)
        {
            Content = jsonBody.Length > 0
                ? new ByteArrayContent(Encoding.UTF8.GetBytes(jsonBody))
                : null,
        };
        message.Headers.TryAddWithoutValidation(OAuthHeaderNames.ApiKey, credentials.ApiKey);
        message.Headers.TryAddWithoutValidation(OAuthHeaderNames.Timestamp, timestamp);
        message.Headers.TryAddWithoutValidation(OAuthHeaderNames.RecvWindow, RecvWindow);
        message.Headers.TryAddWithoutValidation(OAuthHeaderNames.Sign, signature);
        if (message.Content is not null)
        {
            message.Content.Headers.TryAddWithoutValidation(OAuthHeaderNames.ContentType, OAuthHeaderNames.JsonContentType);
        }

        using var response = await _httpClient.SendAsync(message, ct);
        response.EnsureSuccessStatusCode();

        await using (var stream = await response.Content.ReadAsStreamAsync(ct))
        using (var document = await JsonDocument.ParseAsync(stream, options: default, cancellationToken: ct))
        {
            var root = document.RootElement;
            if (root.TryGetProperty("retCode", out var retCode) &&
                retCode.ValueKind == JsonValueKind.Number &&
                retCode.GetInt64() != 0)
            {
                var retMsg = TryGetString(root, "retMsg") ?? "no message";
                throw new InvalidOperationException(
                    $"{VenueFailurePrefix} retCode={retCode.GetInt64()}, retMsg={retMsg} ({path})");
            }

            return root.TryGetProperty(OAuthFieldNames.Result, out var resultValue) &&
                   resultValue.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                ? resultValue.Clone()
                : throw new InvalidOperationException(
                    $"{VenueFailurePrefix} the response carries no parsable result member ({path})");
        }
    }

    #endregion

    #region Private Methods: queries' mapping

    /// <summary>
    /// The open-orders rows: one Core order per mappable venue row. Rows
    /// that cannot carry an honest Core representation are refused with a
    /// logged reason — never defaulted onto the panels.
    /// </summary>
    private IReadOnlyList<Order> MapOpenOrders(JsonElement result)
    {
        var list = result.TryGetProperty(OAuthFieldNames.ListMember, out var listValue) &&
                   listValue.ValueKind == JsonValueKind.Array
            ? listValue
            : throw new InvalidOperationException(
                $"{VenueFailurePrefix} the open-orders response carries no list member ({OpenOrdersPath})");

        var orders = new List<Order>(list.GetArrayLength());
        foreach (var row in list.EnumerateArray())
        {
            if (!TryMapOrderRow(row, out var order, out var skipReason))
            {
                _logger.LogWarning(
                    "Bybit testnet open-order row dropped ({SkipReason}): {Row}",
                    skipReason,
                    row.GetRawText());
                continue;
            }

            orders.Add(order!);
            _orders[order!.Id] = order;
        }

        return orders;
    }

    /// <summary>
    /// Map one open-order row into the Core order. Rows with no Core
    /// representation on this surface (unparsable symbol/side/status/type/
    /// time-in-force/number) are refused with a reason — never defaulted.
    /// </summary>
    private static bool TryMapOrderRow(JsonElement row, out Order? order, out string skipReason)
    {
        order = null;
        skipReason = string.Empty;

        var orderLinkId = TryGetString(row, "orderLinkId");
        var orderId = TryGetString(row, "orderId");
        var symbolStr = TryGetString(row, "symbol");
        var side = TryGetString(row, "side");
        var status = TryGetString(row, "orderStatus");
        var orderTypeStr = TryGetString(row, "orderType");
        var timeInForceStr = TryGetString(row, "timeInForce");
        var candidateId = orderLinkId is { Length: > 0 } link ? link : orderId;

        if (string.IsNullOrWhiteSpace(candidateId) ||
            string.IsNullOrWhiteSpace(symbolStr) ||
            string.IsNullOrWhiteSpace(side) ||
            string.IsNullOrWhiteSpace(status))
        {
            skipReason = "a member the mapping requires is missing";
            return false;
        }

        if (!Symbol.TryParseExchangeFormat(symbolStr, ExchangeId, out var symbol) || symbol is null)
        {
            skipReason = $"the symbol '{symbolStr}' is not parsable";
            return false;
        }

        if (!Enum.TryParse<OrderSide>(side, false, out var parsedSide))
        {
            skipReason = $"the side '{side}' is not parsable";
            return false;
        }

        OrderStatus mapped;
        try
        {
            mapped = BybitOrderStatusMapper.Map(status);
        }
        catch (InvalidOperationException ex)
        {
            skipReason = ex.Message;
            return false;
        }

        if (!Enum.TryParse<OrderType>(orderTypeStr, false, out var parsedOrderType))
        {
            skipReason = $"the order type '{orderTypeStr}' is not parsable";
            return false;
        }

        if (!TryParseVenueTimeInForce(timeInForceStr, out var parsedTimeInForce, out var rowPostOnly))
        {
            skipReason = $"the time-in-force '{timeInForceStr}' is not parsable";
            return false;
        }

        var qtyStr = TryGetString(row, "qty");
        if (qtyStr is null || !TryParseDecimalString(qtyStr, out var qtyValue))
        {
            skipReason = "the row carries no parsable quantity";
            return false;
        }

        var cumStr = TryGetString(row, "cumExecQty");
        if (cumStr is null || !TryParseDecimalString(cumStr, out var filledValue))
        {
            skipReason = "the row carries no parsable cumulated fill";
            return false;
        }

        var priceStr = TryGetString(row, "price");
        if (priceStr is null || !TryParseDecimalString(priceStr, out var priceValue))
        {
            skipReason = "the row carries no parsable limit price";
            return false;
        }

        order = new Order
        {
            Id = candidateId,
            ExchangeId = ExchangeId,
            ExchangeOrderId = orderId,
            Symbol = symbol!,
            Side = parsedSide,
            Type = parsedOrderType,
            Quantity = new Quantity(qtyValue),
            Price = new Price(priceValue),
            StopPrice = TryParseDecimalString(TryGetString(row, "triggerPrice"), out var trigger)
                ? new Price(trigger)
                : null,
            TimeInForce = parsedTimeInForce,
            PostOnly = rowPostOnly,
            ClientOrderId = orderLinkId,
            Status = mapped,
            FilledQuantity = new Quantity(filledValue),
            CreatedAt = TryParseTimestampMillis(TryGetString(row, "createdTime")) ?? DateTimeOffset.UtcNow,
            UpdatedAt = TryParseTimestampMillis(TryGetString(row, "updatedTime")),
        };

        return true;
    }

    /// <summary>The position rows of the spot account: flat or unparsable rows are refused, not fabricated.</summary>
    private IReadOnlyList<Position> MapPositions(JsonElement result)
    {
        var list = result.TryGetProperty(OAuthFieldNames.ListMember, out var listValue) &&
                   listValue.ValueKind == JsonValueKind.Array
            ? listValue
            : throw new InvalidOperationException(
                $"{VenueFailurePrefix} the positions response carries no list member ({PositionsPath})");

        var positions = new List<Position>(list.GetArrayLength());
        foreach (var row in list.EnumerateArray())
        {
            if (!TryMapPositionRow(row, out var position, out var skipReason))
            {
                _logger.LogWarning("Bybit testnet position row dropped ({SkipReason}): {Row}", skipReason, row.GetRawText());
                continue;
            }

            positions.Add(position!);
        }

        return positions;
    }

    /// <summary>Map one position row; a missing member, a flat row or an unparsable number is a refused row.</summary>
    private static bool TryMapPositionRow(JsonElement row, out Position? position, out string skipReason)
    {
        position = null;
        skipReason = string.Empty;

        var symbolStr = TryGetString(row, OAuthFieldNames.SymbolField);
        var side = TryGetString(row, "side");
        var sizeStr = TryGetString(row, "size");
        var avgPriceStr = TryGetString(row, "avgPrice");

        if (string.IsNullOrWhiteSpace(symbolStr) ||
            string.IsNullOrWhiteSpace(side) ||
            string.IsNullOrWhiteSpace(sizeStr) ||
            string.IsNullOrWhiteSpace(avgPriceStr))
        {
            skipReason = "a member the mapping requires is missing";
            return false;
        }

        if (string.Equals(side, PositionSides.None, StringComparison.OrdinalIgnoreCase))
        {
            skipReason = "the position is flat";
            return false;
        }

        if (!Symbol.TryParseExchangeFormat(symbolStr, ExchangeId, out var symbol) || symbol is null)
        {
            skipReason = $"the symbol '{symbolStr}' is not parsable";
            return false;
        }

        if (!Enum.TryParse<OrderSide>(side, false, out var parsedSide))
        {
            skipReason = $"the side '{side}' is not parsable";
            return false;
        }

        if (!TryParseDecimalString(sizeStr, out var size))
        {
            skipReason = "the position size is not parsable";
            return false;
        }

        if (!TryParseDecimalString(avgPriceStr, out var entryPrice))
        {
            skipReason = "the position entry price is not parsable";
            return false;
        }

        position = new Position
        {
            Id = $"{PositionIdPrefix}{symbol.Normalized}",
            ExchangeId = ExchangeId,
            Symbol = symbol,
            Side = parsedSide,
            Quantity = new Quantity(size),
            EntryPrice = new Price(entryPrice),
            CurrentPrice = new Price(entryPrice),
            OpenedAt = TryParseTimestampMillis(TryGetString(row, "updatedTime")) ?? DateTimeOffset.UtcNow,
        };

        return true;
    }

    /// <summary>
    /// The unified-account balance rows: the wallet balance per coin, with
    /// zero balance rows dropped (an empty-wallet coin carries no display
    /// value; a missing or unparsable row is logged, never defaulted).
    /// </summary>
    private IReadOnlyDictionary<string, Money> MapBalances(JsonElement result)
    {
        var balances = new Dictionary<string, Money>();
        var list = result.TryGetProperty(OAuthFieldNames.ListMember, out var listValue) &&
                   listValue.ValueKind == JsonValueKind.Array
            ? listValue
            : throw new InvalidOperationException(
                $"{VenueFailurePrefix} the balances response carries no list member ({WalletBalancePath})");

        foreach (var row in list.EnumerateArray())
        {
            if (!row.TryGetProperty("coin", out var coins) || coins.ValueKind != JsonValueKind.Array)
            {
                _logger.LogWarning(
                    "Bybit testnet balance row carries no coin array and is dropped: {Row}",
                    row.GetRawText());
                continue;
            }

            foreach (var coin in coins.EnumerateArray())
            {
                var name = TryGetString(coin, OAuthFieldNames.CoinField);
                var amountStr = TryGetString(coin, "walletBalance");
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(amountStr))
                {
                    _logger.LogWarning("Bybit testnet balance row carries no coin name or amount and is dropped");
                    continue;
                }

                if (!TryParseDecimalString(amountStr, out var amount))
                {
                    _logger.LogWarning(
                        "Bybit testnet balance of {Coin} is not parsable and is dropped: {Row}",
                        name,
                        coin.GetRawText());
                    continue;
                }

                if (amount == decimal.Zero)
                {
                    continue;
                }

                balances[name] = new Money(amount, name);
            }
        }

        return balances;
    }

    #endregion

    #region Private Methods: order state map

    /// <summary>
    /// Map one order frame: the transition lands on the resting state for
    /// the client id (the adapter's own placements are keyed by their link
    /// id); a venue order unknown to this session constructs an honest
    /// order record from the frame. A status or fill progress publishes the
    /// transition on <see cref="OrderUpdates"/>.
    /// </summary>
    private void HandleOrderFrame(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Array)
        {
            _logger.LogWarning("Bybit testnet order frame data is not an event array, the frame is dropped");
            return;
        }

        foreach (var item in data.EnumerateArray())
        {
            if (!TryMapOrderUpdate(item, out var update, out var skipReason))
            {
                _logger.LogWarning("Bybit testnet order update dropped ({SkipReason}): {Item}", skipReason, item.GetRawText());
                continue;
            }

            ApplyOrderUpdate(update!);
        }
    }

    /// <summary>
    /// Map one frame entry: symbol/side/status/type/time-in-force and
    /// numbers parse strictly; an unmappable member refuses the entry with
    /// a reason — no defaulted statuses on the venue transitions.
    /// </summary>
    private static bool TryMapOrderUpdate(JsonElement item, out Order? update, out string skipReason)
    {
        update = null;
        skipReason = string.Empty;

        var orderLinkId = TryGetString(item, "orderLinkId");
        var orderId = TryGetString(item, "orderId");
        var symbolStr = TryGetString(item, "symbol");
        var side = TryGetString(item, "side");
        var status = TryGetString(item, "orderStatus");
        var orderTypeStr = TryGetString(item, "orderType");
        var timeInForceStr = TryGetString(item, "timeInForce");
        var candidateId = string.IsNullOrWhiteSpace(orderLinkId) ? orderId : orderLinkId;

        if (string.IsNullOrWhiteSpace(candidateId) ||
            string.IsNullOrWhiteSpace(symbolStr) ||
            string.IsNullOrWhiteSpace(side) ||
            string.IsNullOrWhiteSpace(status))
        {
            skipReason = "a member the mapping requires is missing";
            return false;
        }

        if (!Symbol.TryParseExchangeFormat(symbolStr, ExchangeId, out var symbol) || symbol is null)
        {
            skipReason = $"the symbol '{symbolStr}' is not parsable";
            return false;
        }

        if (!Enum.TryParse<OrderSide>(side, false, out var parsedSide))
        {
            skipReason = $"the side '{side}' is not parsable";
            return false;
        }

        OrderStatus mapped;
        try
        {
            mapped = BybitOrderStatusMapper.Map(status);
        }
        catch (InvalidOperationException ex)
        {
            skipReason = ex.Message;
            return false;
        }

        if (!Enum.TryParse<OrderType>(orderTypeStr, false, out var parsedOrderType))
        {
            skipReason = $"the order type '{orderTypeStr}' is not parsable";
            return false;
        }

        if (!TryParseVenueTimeInForce(timeInForceStr, out var parsedTimeInForce, out var updatePostOnly))
        {
            skipReason = $"the time-in-force '{timeInForceStr}' is not parsable";
            return false;
        }

        var qtyStr = TryGetString(item, "qty");
        if (qtyStr is null || !TryParseDecimalString(qtyStr, out var qtyValue))
        {
            skipReason = "the update carries no parsable quantity";
            return false;
        }

        var priceStr = TryGetString(item, "price");
        if (priceStr is null || !TryParseDecimalString(priceStr, out var priceValue))
        {
            skipReason = "the update carries no parsable limit price";
            return false;
        }

        var cumStr = TryGetString(item, "cumExecQty");
        if (cumStr is null || !TryParseDecimalString(cumStr, out var filledValue))
        {
            skipReason = "the update carries no parsable cumulated fill";
            return false;
        }

        update = new Order
        {
            Id = candidateId,
            ExchangeId = ExchangeId,
            ExchangeOrderId = orderId,
            Symbol = symbol!,
            Side = parsedSide,
            Type = parsedOrderType,
            Quantity = new Quantity(qtyValue),
            Price = new Price(priceValue),
            TimeInForce = parsedTimeInForce,
            PostOnly = updatePostOnly,
            ClientOrderId = orderLinkId,
            Status = mapped,
            FilledQuantity = new Quantity(filledValue),
            CreatedAt = TryParseTimestampMillis(TryGetString(item, "createdTime")) ?? DateTimeOffset.UtcNow,
            UpdatedAt = TryParseTimestampMillis(TryGetString(item, "updatedTime")) ?? DateTimeOffset.UtcNow,
            RejectReason = TryGetString(item, "rejectReason"),
        };

        return true;
    }

    /// <summary>
    /// Apply one mapped update to the resting state: a transition that
    /// changed status or fill publishes; a no-op repeats nothing.
    /// </summary>
    private void ApplyOrderUpdate(Order update)
    {
        if (_orders.TryGetValue(update.Id, out var standing))
        {
            if (standing.Status == update.Status &&
                standing.FilledQuantity.Value == update.FilledQuantity.Value)
            {
                return;
            }

            // Preserve the placement facts the frame cannot carry (the
            // typed row's display fields); the mapping takes the venue's
            // truth for the lifecycle fields it owns.
            var merged = standing with
            {
                Status = update.Status,
                FilledQuantity = update.FilledQuantity,
                UpdatedAt = standing.UpdatedAt < update.UpdatedAt ? update.UpdatedAt : standing.UpdatedAt,
                RejectReason = update.RejectReason,
                ExchangeOrderId = update.ExchangeOrderId,
            };
            _orders[update.Id] = merged;
            _orderUpdates.OnNext(merged);
            LogTransition(merged);
            return;
        }

        // An order the venue knows but this session does not (placed in
        // another client): the adapter constructs the row from the frame —
        // an honest record, not a phantom.
        _orders[update.Id] = update;
        _orderUpdates.OnNext(update);
        LogTransition(update);
    }

    private void LogTransition(Order order)
    {
        _logger.LogInformation(
            "Bybit testnet order update: {OrderId} — status {Status}, filled {Filled}/{Quantity}",
            order.Id,
            order.Status,
            order.FilledQuantity.Value,
            order.Quantity.Value);
    }

    #endregion

    #region Private Methods: helpers

    private static string NewOrderLinkId()
    {
        var id = $"{OrderLinkIdPrefix}{Guid.NewGuid():N}";
        Debug.Assert(
            id.Length == OrderLinkIdLength,
            $"Generated client order id should be {OrderLinkIdLength} characters");

        return id;
    }

    private static string SignHmacSha256Hex(string secret, string payload)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>Invariant-culture decimal read for a venue numeric string.</summary>
    private static bool TryParseDecimalString(string? text, out decimal value) =>
        decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out value);

    /// <summary>
    /// Map the venue's time-in-force token onto the model (T-191): the spot
    /// surface reports a maker-only limit as the token "PostOnly", which the
    /// model holds as a GTC order carrying the post-only flag — the venue
    /// token is an execution instruction, not a distinct time-in-force. Any
    /// other token parses as the enum; an unparsable one refuses the row.
    /// </summary>
    private static bool TryParseVenueTimeInForce(string? timeInForceStr, out TimeInForce parsed, out bool postOnly)
    {
        postOnly = false;
        if (timeInForceStr == TimeInForceNames.PostOnly)
        {
            parsed = TimeInForce.GTC;
            postOnly = true;
            return true;
        }

        return Enum.TryParse(timeInForceStr, false, out parsed);
    }

    /// <summary>Read one venue millisecond timestamp; absent or unparsable yields null (the caller default-covers).</summary>
    private static DateTimeOffset? TryParseTimestampMillis(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) ||
            !long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var milliseconds))
        {
            return null;
        }

        return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
    }

    private static string? TryGetString(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    #endregion

    #region Nested Types

    private static class FrameOps
    {
        public const string Auth = "auth";
        public const string Subscribe = "subscribe";
    }

    private static class FrameSides
    {
        public const string Buy = "Buy";
        public const string Sell = "Sell";
    }

    private static class OrderTypeNames
    {
        public const string Limit = "Limit";
    }

    private static class TimeInForceNames
    {
        public const string Gtc = "GTC";

        /// <summary>The venue's maker-only token for spot limit orders (T-191).</summary>
        public const string PostOnly = "PostOnly";
    }

    private static class WalletAccountType
    {
        public const string Unified = "UNIFIED";
    }

    private static class PositionSides
    {
        public const string None = "None";
    }

    private static class OAuthHeaderNames
    {
        public const string ApiKey = "X-BAPI-API-KEY";
        public const string Timestamp = "X-BAPI-TIMESTAMP";
        public const string RecvWindow = "X-BAPI-RECV-WINDOW";
        public const string Sign = "X-BAPI-SIGN";
        public const string ContentType = "Content-Type";
        public const string JsonContentType = "application/json";
    }

    private static class OAuthFieldNames
    {
        public const string Result = "result";
        public const string ListMember = "list";
        public const string SymbolField = "symbol";
        public const string CoinField = "coin";

        /// <summary>The v5 auth signature realm: the payload prefix of the private-stream handshake.</summary>
        public const string SignatureRealm = "GET/realtime";
    }

    /// <summary>The fetchable testnet credential pair, held only for the request's identity.</summary>
    /// <param name="ApiKey">The testnet API key.</param>
    /// <param name="ApiSecret">The testnet API secret.</param>
    private sealed record BybitCredentials(string ApiKey, string ApiSecret);

    private sealed record OrderCreateRequest(
        [property: JsonPropertyName("category")] string Category,
        [property: JsonPropertyName("symbol")] string Symbol,
        [property: JsonPropertyName("side")] string Side,
        [property: JsonPropertyName("orderType")] string OrderType,
        [property: JsonPropertyName("qty")] string Quantity,
        [property: JsonPropertyName("price")] string Price,
        [property: JsonPropertyName("timeInForce")] string TimeInForce,
        [property: JsonPropertyName("orderLinkId")] string OrderLinkId);

    private sealed record OrderCancelRequest(
        [property: JsonPropertyName("category")] string Category,
        [property: JsonPropertyName("symbol")] string Symbol,
        [property: JsonPropertyName("orderLinkId")] string OrderLinkId);

    private sealed record PrivateAuthFrame(
        [property: JsonPropertyName("op")] string Op,
        [property: JsonPropertyName("args")] object[] Args);

    private sealed record SubscriptionFrame(
        [property: JsonPropertyName("op")] string Op,
        [property: JsonPropertyName("args")] string[] Args);

    #endregion

    #region IAsyncDisposable

    /// <summary>
    /// Dispose the adapter: stop the owned loops, disconnect the private
    /// socket (a disposable-but-unobserved venue surface holds nothing
    /// anyway) and free the serialization gates.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _lifetimeCts.Cancel();
        await _keepaliveLoop;
        await _sessionLoop;
        _lifetimeCts.Dispose();

        _messageSubscription.Dispose();
        _connectionSubscription.Dispose();
        _orderUpdates.Dispose();
        _tradingGate.Dispose();
        _sessionGate.Dispose();
        _sendGate.Dispose();

        await _wsClient.DisposeAsync();
        _orders.Clear();
    }

    #endregion
}
