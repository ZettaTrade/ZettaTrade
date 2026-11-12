namespace ZettaTrade.Infrastructure.Exchange;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// The Binance venue trading adapter (T-193): the trading seam over the
/// Binance SPOT TESTNET surface. LIMIT orders only, with the full lifecycle
/// mapping — the placement rides <c>POST /api/v3/order</c> (the signed query
/// string), the ack returns <c>Submitted</c>, and the private user-data
/// stream's <c>executionReport</c> events map the venue transitions
/// (submit → working → fill/partial → cancel) into the Core statuses the
/// panels and the journal speak. Every other order family (market,
/// conditional, OCO, reduce-only, non-GTC) is an explicit error, never a
/// degraded execution, and every venue reject (an HTTP error body or a
/// negative <c>code</c> member) surfaces as an error under the
/// rejected-not-default contract. An unmapped venue status is refused,
/// never defaulted.
/// <para>
/// Credentials come ONLY from <see cref="ISecretStore"/> (the testnet API
/// key pair — never a command line, never a config file) and reach the
/// venue as signed requests: every trading call carries
/// <c>X-MBX-APIKEY</c> plus an HMAC-SHA256 hex signature over the exact
/// query string sent (the timestamp and recvWindow members first, the
/// <c>signature</c> member appended after signing). Secrets are fetched per
/// operation, never cached, and are never logged.
/// </para>
/// <para>
/// The private stream is the listen-key flow: <c>POST
/// /api/v3/userDataStream</c> (API-key header only) issues the session's
/// listen key, the adapter dials <c>{PrivateWsUrl}/{listenKey}</c>, and a
/// 30-minute REST keepalive holds the key alive. The stream is RECEIVE-ONLY
/// (the venue pushes; no app-level frames exist), so the adapter owns no
/// send gate and no auth handshake. It connects lazily on the first order
/// operation or <c>ConnectAsync</c> (an idle surface holds no sockets — the
/// demo lane's parity), the per-session transport re-dials the same
/// listen-key URL across its own reconnects, and a session whose connect
/// budget exhausts is re-established by the next order operation. The
/// stream is event-driven and legitimately silent: the per-session silence
/// window is widened to five minutes for exactly that (an idle session
/// arms nothing until the first event crosses it). Market-data members pass
/// through to the shared Binance market-data adapter (the same venue, one
/// socket set apiece).
/// </para>
/// </summary>
public sealed class BinanceTradingAdapter : ITradingAdapter
{
    #region Constants

    /// <summary>Named HttpClientFactory client used for the REST requests.</summary>
    public const string HttpClientName = "binance-testnet";

    /// <summary>Timeout for the REST requests, in seconds.</summary>
    public const int RequestTimeoutSeconds = 30;

    /// <summary>REST base of the spot testnet API (the only shipped host).</summary>
    public const string DefaultRestBaseUrl = "https://testnet.binance.vision";

    /// <summary>
    /// WebSocket base of the spot testnet user-data stream; the live URL
    /// becomes <c>{this}/{listenKey}</c> per session.
    /// </summary>
    public const string DefaultPrivateWsUrl = "wss://stream.testnet.binance.vision/ws";

    /// <summary>Adapter identifier: the venue identity the trading-mode resolver maps (registry key "Binance").</summary>
    public const string ExchangeId = "binance";

    /// <summary>Adapter display name.</summary>
    public const string ExchangeName = "Binance (testnet)";

    /// <summary>Prefix of the client-side order ids the adapter generates.</summary>
    private const string OrderLinkIdPrefix = "zt-";

    /// <summary>Length budget of a client order id: prefix (3) + GUID N-form (32) — the venue's 36-char budget holds.</summary>
    private const int OrderLinkIdLength = 35;

    /// <summary>REST path of the order placement and cancellation endpoints (the method tells them apart).</summary>
    private const string CreateOrderPath = "/api/v3/order";

    /// <summary>REST path of the order cancellation endpoint (shared with the placement path).</summary>
    private const string CancelOrderPath = "/api/v3/order";

    /// <summary>REST path of the open-orders query (all-symbol working orders).</summary>
    private const string OpenOrdersPath = "/api/v3/openOrders";

    /// <summary>REST path of the account query (the balances source).</summary>
    private const string AccountPath = "/api/v3/account";

    /// <summary>REST path of the user-data listen key (create and keepalive).</summary>
    private const string UserDataStreamPath = "/api/v3/userDataStream";

    /// <summary>The request window the signed REST calls ship (the venue's default, in ms).</summary>
    public const string RecvWindow = "5000";

    /// <summary>
    /// The private stream's silence window (minutes): the user-data stream
    /// is event-driven — an account without order activity legitimately
    /// receives nothing, so the shipped 60 s streaming window would churn
    /// the session. Five minutes keeps the half-open heal bounded while an
    /// idle-but-armed session stays quiet; the session arms only after its
    /// first event (the transport's arming rule), so an untouched surface
    /// never cycles at all.
    /// </summary>
    private const int PrivateStreamSilenceWindowMinutes = 5;

    /// <summary>Interval of the listen-key keepalive, in seconds (the venue's 60-minute validity, refreshed well ahead).</summary>
    private const int ListenKeyKeepaliveIntervalSeconds = 1800;

    /// <summary>ISecretStore key of the testnet API key.</summary>
    public const string ApiKeySecretKey = "binance.testnet.api-key";

    /// <summary>ISecretStore key of the testnet API secret.</summary>
    public const string ApiSecretSecretKey = "binance.testnet.api-secret";

    /// <summary>The string this surface's error paths prefix venue failures with.</summary>
    private const string VenueFailurePrefix = "Binance testnet request failed:";

    /// <summary>The reconnect policy (the market-data adapter's shipped shape).</summary>
    public static ReconnectPolicy DefaultReconnectPolicy { get; } = BinanceAdapter.DefaultReconnectPolicy;

    #endregion

    #region Fields

    private readonly IMarketDataAdapter _marketData;
    private readonly ISecretStore _secrets;
    private readonly ILogger<BinanceTradingAdapter> _logger;
    private readonly HttpClient _httpClient;
    private readonly IReconnectStateSink? _supervisorSink;

    /// <summary>Serializes order placement / cancellation; the venue state mutates under it.</summary>
    private readonly SemaphoreSlim _tradingGate = new(1, 1);

    /// <summary>Serializes the session flows (place-time connect and the lazy ConnectAsync).</summary>
    private readonly SemaphoreSlim _sessionGate = new(1, 1);

    /// <summary>
    /// The adapter's order state: one Core order per client id (the accepted surface).
    /// </summary>
    private readonly ConcurrentDictionary<string, Order> _orders = new();

    /// <summary>The placement announcer: the adapter's order-updates stream for the ledger and notifications.</summary>
    private readonly Subject<Order> _orderUpdates = new();

    /// <summary>
    /// The session flows (a fresh listen-key session after a connect): one
    /// consumer serves them FIFO, each caller's completion handed back
    /// through the request (ConnectAsync awaits its flow — a connect is a
    /// connect, never an in-flight maybe).
    /// </summary>
    private readonly Channel<SessionRequest> _sessionQueue = Channel.CreateUnbounded<SessionRequest>(
        new UnboundedChannelOptions { SingleReader = true });

    /// <summary>Adapter-owned lifetime; the keepalive and session loops are awaited on disposal.</summary>
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly Task _keepaliveLoop;
    private readonly Task _sessionLoop;

    /// <summary>The standing private-stream transport and its two subscriptions (per session, replaced by every flow).</summary>
    private WebSocketClient? _sessionClient;
    private IDisposable? _messageSubscription;
    private IDisposable? _connectionSubscription;

    /// <summary>The live session's listen key (the keepalive loop's PUT target; null while no session exists).</summary>
    private volatile string? _listenKey;
    private int _disposed;

    #endregion

    #region Properties

    /// <summary>The adapter of the Binance venue trading surface (the venue identity the resolver maps by).</summary>
    public string Id => ExchangeId;

    /// <summary>The adapter's display name.</summary>
    public string Name => ExchangeName;

    /// <summary>The private socket's connection state (the trading transport).</summary>
    public bool IsConnected => _sessionClient is { IsConnected: true };

    /// <summary>The surface's capabilities: trading and account reads over its own venue market data.</summary>
    public ExchangeCapabilities Capabilities =>
        ExchangeCapabilities.Trading | ExchangeCapabilities.AccountInfo | ExchangeCapabilities.MarketData
        | ExchangeCapabilities.OrderBook | ExchangeCapabilities.HistoricalData;

    /// <summary>
    /// The venue order transitions (submit → working → fill/partial → cancel
    /// → reject), one emit per transition the user-data stream mapped. The
    /// journal and the notification surfaces consume; the adapter publishes
    /// only — it never journals itself.
    /// </summary>
    public IObservable<Order> OrderUpdates => _orderUpdates.AsObservable();

    /// <summary>The transport endpoints the adapter re-hosts (internal: the registration tests assert the testnet pair).</summary>
    internal BinanceTradingEndpoints Endpoints { get; }

    #endregion

    #region Constructor

    /// <summary>
    /// Create the adapter over the testnet host pair, its venue market data
    /// for the pass-through members, and the secret store the testnet API
    /// key pair rides. Endpoints must carry non-empty members — a blank pair
    /// is a configuration error, thrown immediately (a misconfigured
    /// trading transport must never boot half-configured).
    /// </summary>
    public BinanceTradingAdapter(
        ILogger<BinanceTradingAdapter> logger,
        IHttpClientFactory httpClientFactory,
        IMarketDataAdapter marketData,
        ISecretStore secrets,
        BinanceTradingEndpoints? endpoints = null,
        ReconnectSupervisor? reconnectSupervisor = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _marketData = marketData ?? throw new ArgumentNullException(nameof(marketData));
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));

        var pair = endpoints ?? BinanceTradingEndpoints.Testnet;
        if (string.IsNullOrWhiteSpace(pair.RestBaseUrl) || string.IsNullOrWhiteSpace(pair.PrivateWsUrl))
        {
            throw new ArgumentException(
                "Configured Binance trading endpoints must carry non-empty RestBaseUrl and PrivateWsUrl",
                nameof(endpoints));
        }

        Endpoints = pair;

        _httpClient = httpClientFactory.CreateClient(HttpClientName);
        _supervisorSink = reconnectSupervisor?.For(ExchangeId, DefaultReconnectPolicy);

        _keepaliveLoop = RunListenKeyKeepaliveLoopAsync(_lifetimeCts.Token);
        _sessionLoop = RunSessionLoopAsync(_lifetimeCts.Token);
    }

    #endregion

    #region Market data (delegated to the shared venue adapter)

    /// <summary>Connect the trading surface: the private session (lazy until the first order operation) is awaited.</summary>
    public Task ConnectAsync(CancellationToken ct = default) => EnsurePrivateSessionAsync(ct);

    /// <inheritdoc/>
    public Task DisconnectAsync(CancellationToken ct = default) => TearDownStandingSessionAsync();

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
    /// the venue; a venue reject throws with its own code and message (never
    /// a defaulted order, never a paper-shape substitution).
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

            var clientOrderId = request.ClientOrderId ?? NewOrderLinkId();
            // Post-only spot limits (T-191) ride the venue's own maker-only
            // time-in-force token; a plain GTC limit keeps its token.
            var rawQuery =
                $"symbol={request.Symbol.GetExchangeFormat(ExchangeId)}" +
                $"&side={(request.Side == OrderSide.Buy ? SideNames.Buy : SideNames.Sell)}" +
                $"&type={OrderTypeNames.Limit}" +
                $"&timeInForce={(request.PostOnly ? TimeInForceNames.Gtx : TimeInForceNames.Gtc)}" +
                $"&quantity={request.Quantity.Value.ToString(CultureInfo.InvariantCulture)}" +
                $"&price={request.Price!.Value.Value.ToString(CultureInfo.InvariantCulture)}" +
                $"&newClientOrderId={Uri.EscapeDataString(clientOrderId)}";

            var ack = await SendSignedRestAsync(HttpMethod.Post, CreateOrderPath, rawQuery, credentials, ct);
            var venueOrderId = TryGetNumberAsString(ack, FrameFieldNames.RestOrderId);
            var echoedClient = TryGetString(ack, FrameFieldNames.RestClientOrderId);

            var order = new Order
            {
                Id = string.IsNullOrEmpty(echoedClient) ? clientOrderId : echoedClient,
                ExchangeId = ExchangeId,
                ExchangeOrderId = string.IsNullOrEmpty(venueOrderId) ? clientOrderId : venueOrderId,
                Symbol = request.Symbol,
                Side = request.Side,
                Type = OrderType.Limit,
                Quantity = request.Quantity,
                Price = request.Price.Value,
                TimeInForce = TimeInForce.GTC,
                PostOnly = request.PostOnly,
                ClientOrderId = clientOrderId,
                Status = OrderStatus.Submitted,
                FilledQuantity = Quantity.Zero,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            _orders[order.Id] = order;

            _logger.LogInformation(
                "Binance testnet place: {OrderId} {Side} {Quantity} {Symbol} limit {LimitPrice}",
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
    /// issued (the client order id). A venue reject throws.
    /// </summary>
    public async Task CancelOrderAsync(Symbol symbol, string orderId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        ArgumentException.ThrowIfNullOrWhiteSpace(orderId);

        await _tradingGate.WaitAsync(ct);
        try
        {
            var credentials = await FetchTestnetCredentialsAsync(ct);
            var rawQuery =
                $"symbol={symbol.GetExchangeFormat(ExchangeId)}" +
                $"&origClientOrderId={Uri.EscapeDataString(orderId)}";

            await SendSignedRestAsync(HttpMethod.Delete, CancelOrderPath, rawQuery, credentials, ct);

            _logger.LogInformation(
                "Binance testnet cancel: {OrderId} on {Symbol} — the cancelled state follows on the order stream",
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
            var result = await SendSignedRestAsync(HttpMethod.Get, OpenOrdersPath, string.Empty, credentials, ct);

            return MapOpenOrders(result);
        }
        finally
        {
            _tradingGate.Release();
        }
    }

    /// <summary>
    /// The open positions of the venue account: an empty list, always. The
    /// Binance SPOT surface carries no position rows (spot trades settle
    /// balances, not positions) — the honest venue answer needs no REST
    /// call, and a fabricated row would be a phantom on the positions panel.
    /// </summary>
    public Task<IReadOnlyList<Position>> GetPositionsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Position>>([]);

    /// <summary>The wallet balances of the spot account, mapped strictly.</summary>
    public async Task<IReadOnlyDictionary<string, Money>> GetBalancesAsync(CancellationToken ct = default)
    {
        await _tradingGate.WaitAsync(ct);
        try
        {
            var credentials = await FetchTestnetCredentialsAsync(ct);
            var result = await SendSignedRestAsync(HttpMethod.Get, AccountPath, string.Empty, credentials, ct);

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
                "The Binance testnet trading surface does not carry bracket orders " +
                "(an entry with attached take-profit/stop-loss exits); the request is refused, not degraded.");
        }

        if (request.Type != OrderType.Limit)
        {
            throw new InvalidOperationException(
                $"The Binance testnet trading surface trades Limit orders only; a {request.Type} order is refused, not degraded.");
        }

        if (request.TimeInForce != TimeInForce.GTC)
        {
            throw new InvalidOperationException(
                $"The Binance testnet trading surface ships GoodTillCancelled limit orders only; a {request.TimeInForce} request is refused, not degraded.");
        }

        if (request.OcoGroupId is not null)
        {
            throw new InvalidOperationException(
                "The Binance testnet trading surface does not carry OCO pairs; the leg is refused, not degraded.");
        }

        if (request.ReduceOnly)
        {
            throw new InvalidOperationException(
                "The Binance testnet spot surface carries no reduce-only orders; the request is refused, not degraded.");
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
    private async Task<BinanceCredentials> FetchTestnetCredentialsAsync(CancellationToken ct)
    {
        var apiKey = await _secrets.GetAsync(ApiKeySecretKey, ct);
        var apiSecret = await _secrets.GetAsync(ApiSecretSecretKey, ct);

        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(apiSecret))
        {
            throw new InvalidOperationException(
                $"Binance testnet credentials are not available in the secret store (keys '{ApiKeySecretKey}' and '{ApiSecretSecretKey}'); " +
                "the venue request is refused rather than sent unauthenticated.");
        }

        return new BinanceCredentials(apiKey, apiSecret);
    }

    #endregion

    #region Private Methods: session flows

    /// <summary>
    /// One session flow on the pump: a FRESH listen key is issued (the venue
    /// invalidates the old one's session), the standing transport is torn
    /// down so the two never race, and the new listen-key socket connects.
    /// The completion travels back to the awaiting caller — a failed flow
    /// fails it (a connect that did not connect is an error, never a
    /// silently held paper surface).
    /// </summary>
    private async Task ServeSessionAsync(SessionRequest request)
    {
        try
        {
            await EstablishSessionAsync(_lifetimeCts.Token);
            request.Completion.TrySetResult(true);
        }
        catch (OperationCanceledException)
        {
            request.Completion.TrySetCanceled();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Binance testnet private-stream session flow failed");
            request.Completion.TrySetException(ex);
        }
    }

    /// <summary>
    /// The fresh listen-key session: the credentials (the key pair rides the
    /// listen-key REST call), the issued key, then the replacement transport
    /// — the old client is torn down BEFORE the new dial so two sockets
    /// never hold one listen-key session.
    /// </summary>
    private async Task EstablishSessionAsync(CancellationToken ct)
    {
        var credentials = await FetchTestnetCredentialsAsync(ct);
        var listenKey = await CreateListenKeyAsync(credentials, ct);

        await TearDownStandingSessionAsync();

        var client = new WebSocketClient(
            $"{Endpoints.PrivateWsUrl}/{listenKey}",
            _logger,
            DefaultReconnectPolicy,
            _supervisorSink,
            TimeSpan.FromMinutes(PrivateStreamSilenceWindowMinutes));

        var messageSubscription = client.MessageStream.Subscribe(HandleWebSocketMessage);
        var connectionSubscription = client.ConnectionStateStream.Subscribe(HandleConnectionStateChanged);

        try
        {
            await client.ConnectAsync(ct);
        }
        catch
        {
            messageSubscription.Dispose();
            connectionSubscription.Dispose();
            await client.DisposeAsync();
            throw;
        }

        _messageSubscription = messageSubscription;
        _connectionSubscription = connectionSubscription;
        _sessionClient = client;
        _listenKey = listenKey;

        _logger.LogInformation(
            "Binance testnet private stream established (the user-data session rides its listen key)");
    }

    /// <summary>
    /// Tear the standing session down (the per-session subscriptions and the
    /// transport). Idempotent: a session-less adapter tears down nothing.
    /// </summary>
    private async Task TearDownStandingSessionAsync()
    {
        _messageSubscription?.Dispose();
        _messageSubscription = null;
        _connectionSubscription?.Dispose();
        _connectionSubscription = null;

        if (_sessionClient is { } standing)
        {
            _sessionClient = null;
            _listenKey = null;
            await standing.DisposeAsync();
        }
    }

    /// <summary>
    /// The ONE consumer of the session queue: entries are served strictly
    /// one at a time, so a place-time connect and a lazy ConnectAsync cannot
    /// race their listen-key flows. Never fire-and-forget: the loop task is
    /// tracked and awaited on disposal.
    /// </summary>
    private async Task RunSessionLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var request in _sessionQueue.Reader.ReadAllAsync(ct))
            {
                await ServeSessionAsync(request);
            }
        }
        catch (OperationCanceledException)
        {
            // Disposal: the only exit of the session pump.
        }
    }

    /// <summary>
    /// The place-time session entry (ConnectAsync and the placement path):
    /// when the socket is not open, a fresh listen-key flow is enqueued and
    /// AWAITED (a cancelled caller abandons the wait, never the flow). When
    /// it is open, the standing session serves.
    /// </summary>
    private async Task EnsurePrivateSessionAsync(CancellationToken ct)
    {
        await _sessionGate.WaitAsync(ct);
        try
        {
            if (IsConnected)
            {
                return;
            }

            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _sessionQueue.Writer.TryWrite(new SessionRequest(completion));
            await completion.Task.WaitAsync(ct);
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    /// <summary>
    /// The connection-state handler: nothing to re-run on a fresh socket —
    /// the transport's own reconnect re-dials the SAME listen-key URL (the
    /// key stays alive under the keepalive loop), and the user-data stream
    /// carries no auth or subscribe handshake to repeat. A session whose
    /// reconnect budget exhausts stays down until the next order operation's
    /// session entry re-establishes it (a fresh key, a fresh socket).
    /// </summary>
    private void HandleConnectionStateChanged(bool isConnected)
    {
        if (isConnected)
        {
            _logger.LogInformation("Binance testnet private stream connected");
        }
    }

    /// <summary>
    /// The listen-key keepalive: the venue retires a listen key after an
    /// hour of silence, so the adapter PUTs it every 30 minutes while a
    /// session stands. One failed keepalive never kills the loop: the next
    /// cycle retries, and a retired key's stream simply drops — the
    /// reconnect machinery and the next order operation re-establish a
    /// fresh session.
    /// </summary>
    private async Task RunListenKeyKeepaliveLoopAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(ListenKeyKeepaliveIntervalSeconds));
            while (await timer.WaitForNextTickAsync(ct))
            {
                var listenKey = _listenKey;
                if (listenKey is null || _sessionClient is not { IsConnected: true })
                {
                    continue;
                }

                try
                {
                    var credentials = await FetchTestnetCredentialsAsync(ct);
                    await KeepListenKeyAliveAsync(credentials, listenKey, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Binance testnet listen-key keepalive failed; the next cycle retries");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Disposal: the only exit of the keepalive loop.
        }
    }

    /// <summary>
    /// The private-stream frame dispatch: the <c>executionReport</c> events
    /// map venue order transitions; every other user-data event
    /// (<c>outboundAccountPosition</c>, <c>listStatus</c>, …) carries no
    /// Core surface in this adapter's limit-only scope and is ignored — the
    /// panels poll the venue for what they show. Errors from one message
    /// never leave the handler.
    /// </summary>
    private void HandleWebSocketMessage(string message)
    {
        try
        {
            using var json = JsonDocument.Parse(message);
            var root = json.RootElement;
            var eventType = TryGetString(root, FrameFieldNames.EventType);

            if (string.Equals(eventType, EventNames.ExecutionReport, StringComparison.Ordinal))
            {
                HandleExecutionReport(root);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling Binance testnet private-stream message");
        }
    }

    #endregion

    #region Private Methods: order state map

    /// <summary>
    /// Map one execution report: the transition lands on the resting state
    /// for the client id (the adapter's own placements are keyed by their
    /// client order id); a venue order unknown to this session constructs an
    /// honest order record from the frame. A status or fill progress
    /// publishes the transition on <see cref="OrderUpdates"/>.
    /// </summary>
    private void HandleExecutionReport(JsonElement report)
    {
        if (!TryMapOrderUpdate(report, out var update, out var skipReason))
        {
            _logger.LogWarning(
                "Binance testnet order update dropped ({SkipReason}): {Item}",
                skipReason,
                report.GetRawText());
            return;
        }

        ApplyOrderUpdate(update!);
    }

    /// <summary>
    /// Map one execution report: symbol/side/status/type/time-in-force and
    /// numbers parse strictly; an unmappable member refuses the entry with
    /// a reason — no defaulted statuses on the venue transitions.
    /// </summary>
    private static bool TryMapOrderUpdate(JsonElement item, out Order? update, out string skipReason)
    {
        update = null;
        skipReason = string.Empty;

        var clientOrderId = TryGetString(item, FrameFieldNames.ClientOrderId);
        var orderId = TryGetNumberAsString(item, FrameFieldNames.OrderId);
        var symbolStr = TryGetString(item, FrameFieldNames.Symbol);
        var side = TryGetString(item, FrameFieldNames.Side);
        var status = TryGetString(item, FrameFieldNames.OrderStatus);
        var orderTypeStr = TryGetString(item, FrameFieldNames.OrderType);
        var timeInForceStr = TryGetString(item, FrameFieldNames.TimeInForce);
        var candidateId = string.IsNullOrWhiteSpace(clientOrderId) ? orderId : clientOrderId;

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

        if (!TryParseVenueSide(side, out var parsedSide))
        {
            skipReason = $"the side '{side}' is not parsable";
            return false;
        }

        OrderStatus mapped;
        try
        {
            mapped = BinanceOrderStatusMapper.Map(status);
        }
        catch (InvalidOperationException ex)
        {
            skipReason = ex.Message;
            return false;
        }

        if (!TryParseVenueOrderType(orderTypeStr, out var parsedOrderType, out var typePostOnly))
        {
            skipReason = $"the order type '{orderTypeStr}' is not parsable";
            return false;
        }

        if (!TryParseVenueTimeInForce(timeInForceStr, out var parsedTimeInForce, out var tifPostOnly))
        {
            skipReason = $"the time-in-force '{timeInForceStr}' is not parsable";
            return false;
        }

        var qtyStr = TryGetString(item, FrameFieldNames.Quantity);
        if (qtyStr is null || !TryParseDecimalString(qtyStr, out var qtyValue))
        {
            skipReason = "the update carries no parsable quantity";
            return false;
        }

        var priceStr = TryGetString(item, FrameFieldNames.Price);
        if (priceStr is null || !TryParseDecimalString(priceStr, out var priceValue))
        {
            skipReason = "the update carries no parsable limit price";
            return false;
        }

        var cumStr = TryGetString(item, FrameFieldNames.CumulativeFilled);
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
            PostOnly = typePostOnly || tifPostOnly,
            ClientOrderId = clientOrderId,
            Status = mapped,
            FilledQuantity = new Quantity(filledValue),
            CreatedAt = TryParseTimestampMillis(TryGetTimestampMember(item, FrameFieldNames.OrderTime)) ?? DateTimeOffset.UtcNow,
            UpdatedAt = TryParseTimestampMillis(TryGetTimestampMember(item, FrameFieldNames.EventTime)) ?? DateTimeOffset.UtcNow,
            // The spot execution report carries no reject-reason member: the
            // venue's rejections arrive as REST errors (surfaced at the call
            // site) or the REJECTED status itself.
            RejectReason = null,
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
            "Binance testnet order update: {OrderId} — status {Status}, filled {Filled}/{Quantity}",
            order.Id,
            order.Status,
            order.FilledQuantity.Value,
            order.Quantity.Value);
    }

    #endregion

    #region Private Methods: signing + REST

    /// <summary>
    /// One signed REST call: the query string is built raw, timestamped,
    /// windowed and signed (HMAC-SHA256 hex over the EXACT string sent), the
    /// signature member appended after signing, and the API key rides the
    /// header. A venue failure surfaces on both shapes the venue serves —
    /// an HTTP error status whose body carries <c>code</c>/<c>msg</c>, and
    /// a 200 body carrying a negative <c>code</c> — never a defaulted
    /// result.
    /// </summary>
    private async Task<JsonElement> SendSignedRestAsync(
        HttpMethod method,
        string path,
        string rawQuery,
        BinanceCredentials credentials,
        CancellationToken ct)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        var query = rawQuery.Length > 0
            ? $"{rawQuery}&timestamp={timestamp}&recvWindow={RecvWindow}"
            : $"timestamp={timestamp}&recvWindow={RecvWindow}";
        var signature = SignHmacSha256Hex(credentials.ApiSecret, query);
        query = $"{query}&signature={signature}";

        var url = $"{Endpoints.RestBaseUrl}{path}?{query}";
        using var message = new HttpRequestMessage(method, url);
        message.Headers.TryAddWithoutValidation(ApiKeyHeaderNames.Key, credentials.ApiKey);

        using var response = await _httpClient.SendAsync(message, ct);
        return await ReadVenueResponseAsync(response, path, ct);
    }

    /// <summary>
    /// One API-key-only REST call (the listen-key surface): no signature,
    /// the key rides the header alone — the venue's own contract for the
    /// user-data stream management.
    /// </summary>
    private async Task<JsonElement> SendApiKeyRestAsync(
        HttpMethod method,
        string path,
        string? rawQuery,
        BinanceCredentials credentials,
        CancellationToken ct)
    {
        var url = string.IsNullOrWhiteSpace(rawQuery)
            ? $"{Endpoints.RestBaseUrl}{path}"
            : $"{Endpoints.RestBaseUrl}{path}?{rawQuery}";
        using var message = new HttpRequestMessage(method, url);
        message.Headers.TryAddWithoutValidation(ApiKeyHeaderNames.Key, credentials.ApiKey);

        using var response = await _httpClient.SendAsync(message, ct);
        return await ReadVenueResponseAsync(response, path, ct);
    }

    /// <summary>
    /// Read one venue response under the rejected-not-default contract: a
    /// non-success HTTP status (whose body carries the venue's
    /// <c>code</c>/<c>msg</c>) and a success status whose body carries a
    /// negative <c>code</c> both throw with the venue's own words; the
    /// parsed JSON is the value.
    /// </summary>
    private async Task<JsonElement> ReadVenueResponseAsync(
        HttpResponseMessage response,
        string path,
        CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(body))
        {
            throw new InvalidOperationException(
                $"{VenueFailurePrefix} the response carries no parsable body ({path})");
        }

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement.Clone();

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"{VenueFailurePrefix} HTTP {(int)response.StatusCode}, code={TryGetVenueCode(root)}, msg={TryGetVenueMessage(root)} ({path})");
        }

        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty(FrameFieldNames.Code, out var code) &&
            code.ValueKind == JsonValueKind.Number &&
            code.GetInt64() < 0)
        {
            throw new InvalidOperationException(
                $"{VenueFailurePrefix} code={code.GetInt64()}, msg={TryGetVenueMessage(root)} ({path})");
        }

        return root;
    }

    /// <summary>Issue the session's listen key (the venue's own create call).</summary>
    private async Task<string> CreateListenKeyAsync(BinanceCredentials credentials, CancellationToken ct)
    {
        var result = await SendApiKeyRestAsync(HttpMethod.Post, UserDataStreamPath, null, credentials, ct);
        var listenKey = TryGetString(result, FrameFieldNames.ListenKey);
        return string.IsNullOrWhiteSpace(listenKey)
            ? throw new InvalidOperationException(
                $"{VenueFailurePrefix} the listen-key response carries no parsable listenKey ({UserDataStreamPath})")
            : listenKey!;
    }

    /// <summary>Refresh the session's listen key (the venue's own keepalive call).</summary>
    private async Task KeepListenKeyAliveAsync(
        BinanceCredentials credentials,
        string listenKey,
        CancellationToken ct) =>
        await SendApiKeyRestAsync(
            HttpMethod.Put,
            UserDataStreamPath,
            $"listenKey={Uri.EscapeDataString(listenKey)}",
            credentials,
            ct);

    #endregion

    #region Private Methods: queries' mapping

    /// <summary>
    /// The open-orders rows (the venue answers a bare array): one Core order
    /// per mappable venue row. Rows that cannot carry an honest Core
    /// representation are refused with a logged reason — never defaulted
    /// onto the panels.
    /// </summary>
    private IReadOnlyList<Order> MapOpenOrders(JsonElement result)
    {
        var list = result.ValueKind == JsonValueKind.Array
            ? result
            : throw new InvalidOperationException(
                $"{VenueFailurePrefix} the open-orders response is not an order array ({OpenOrdersPath})");

        var orders = new List<Order>(list.GetArrayLength());
        foreach (var row in list.EnumerateArray())
        {
            if (!TryMapOpenOrdersRow(row, out var order, out var skipReason))
            {
                _logger.LogWarning(
                    "Binance testnet open-order row dropped ({SkipReason}): {Row}",
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
    private static bool TryMapOpenOrdersRow(JsonElement row, out Order? order, out string skipReason)
    {
        order = null;
        skipReason = string.Empty;

        var clientOrderId = TryGetString(row, FrameFieldNames.RestClientOrderId);
        var orderId = TryGetNumberAsString(row, FrameFieldNames.RestOrderId);
        var symbolStr = TryGetString(row, FrameFieldNames.RestSymbol);
        var side = TryGetString(row, FrameFieldNames.RestSide);
        var status = TryGetString(row, FrameFieldNames.RestOrderStatus);
        var orderTypeStr = TryGetString(row, FrameFieldNames.RestOrderType);
        var timeInForceStr = TryGetString(row, FrameFieldNames.RestTimeInForce);
        var candidateId = !string.IsNullOrWhiteSpace(clientOrderId) ? clientOrderId : orderId;

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

        if (!TryParseVenueSide(side, out var parsedSide))
        {
            skipReason = $"the side '{side}' is not parsable";
            return false;
        }

        OrderStatus mapped;
        try
        {
            mapped = BinanceOrderStatusMapper.Map(status);
        }
        catch (InvalidOperationException ex)
        {
            skipReason = ex.Message;
            return false;
        }

        if (!TryParseVenueOrderType(orderTypeStr, out var parsedOrderType, out var typePostOnly))
        {
            skipReason = $"the order type '{orderTypeStr}' is not parsable";
            return false;
        }

        if (!TryParseVenueTimeInForce(timeInForceStr, out var parsedTimeInForce, out var tifPostOnly))
        {
            skipReason = $"the time-in-force '{timeInForceStr}' is not parsable";
            return false;
        }

        var qtyStr = TryGetString(row, FrameFieldNames.OriginalQuantity);
        if (qtyStr is null || !TryParseDecimalString(qtyStr, out var qtyValue))
        {
            skipReason = "the row carries no parsable quantity";
            return false;
        }

        var cumStr = TryGetString(row, FrameFieldNames.ExecutedQuantity);
        if (cumStr is null || !TryParseDecimalString(cumStr, out var filledValue))
        {
            skipReason = "the row carries no parsable cumulated fill";
            return false;
        }

        var priceStr = TryGetString(row, FrameFieldNames.RestPrice);
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
            TimeInForce = parsedTimeInForce,
            PostOnly = typePostOnly || tifPostOnly,
            ClientOrderId = clientOrderId,
            Status = mapped,
            FilledQuantity = new Quantity(filledValue),
            CreatedAt = TryParseTimestampMillis(TryGetTimestampMember(row, FrameFieldNames.CreatedTime)) ?? DateTimeOffset.UtcNow,
            UpdatedAt = TryParseTimestampMillis(TryGetTimestampMember(row, FrameFieldNames.UpdatedTime)),
        };

        return true;
    }

    /// <summary>
    /// The spot-account balance rows: the wallet balance per asset (free +
    /// locked), with zero balance rows dropped (an empty-wallet asset
    /// carries no display value; a missing or unparsable row is logged,
    /// never defaulted).
    /// </summary>
    private IReadOnlyDictionary<string, Money> MapBalances(JsonElement result)
    {
        var balances = new Dictionary<string, Money>();
        var list = result.ValueKind == JsonValueKind.Object &&
                   result.TryGetProperty(FrameFieldNames.Balances, out var listValue) &&
                   listValue.ValueKind == JsonValueKind.Array
            ? listValue
            : throw new InvalidOperationException(
                $"{VenueFailurePrefix} the account response carries no balances array ({AccountPath})");

        foreach (var row in list.EnumerateArray())
        {
            var asset = TryGetString(row, FrameFieldNames.Asset);
            var freeStr = TryGetString(row, FrameFieldNames.Free);
            var lockedStr = TryGetString(row, FrameFieldNames.Locked);

            if (string.IsNullOrWhiteSpace(asset) ||
                freeStr is null ||
                lockedStr is null ||
                !TryParseDecimalString(freeStr, out var free) ||
                !TryParseDecimalString(lockedStr, out var locked))
            {
                _logger.LogWarning(
                    "Binance testnet balance row carries no parsable asset or amounts and is dropped: {Row}",
                    row.GetRawText());
                continue;
            }

            var total = free + locked;
            if (total == decimal.Zero)
            {
                continue;
            }

            balances[asset] = new Money(total, asset);
        }

        return balances;
    }

    #endregion

    #region Private Methods: venue token maps

    /// <summary>
    /// Map the venue's uppercase side token onto the model: the Core enum
    /// members (<c>Buy</c>/<c>Sell</c>) never parse the wire's own spelling,
    /// so the map is explicit — an unknown token refuses the row.
    /// </summary>
    private static bool TryParseVenueSide(string? side, out OrderSide parsed)
    {
        switch (side)
        {
            case SideNames.Buy:
                parsed = OrderSide.Buy;
                return true;
            case SideNames.Sell:
                parsed = OrderSide.Sell;
                return true;
            default:
                parsed = default;
                return false;
        }
    }

    /// <summary>
    /// Map the venue's order-type token onto the model: LIMIT maps plain;
    /// LIMIT_MAKER (the venue's own post-only flavor, what another client's
    /// GTX order can surface as) maps as a post-only limit; every other
    /// family (market, stop, trailing) refuses the row — this surface never
    /// fabricated an execution shape it does not trade.
    /// </summary>
    private static bool TryParseVenueOrderType(string? orderType, out OrderType parsed, out bool postOnly)
    {
        postOnly = false;
        switch (orderType)
        {
            case OrderTypeNames.Limit:
                parsed = OrderType.Limit;
                return true;
            case OrderTypeNames.LimitMaker:
                parsed = OrderType.Limit;
                postOnly = true;
                return true;
            default:
                parsed = default;
                return false;
        }
    }

    /// <summary>
    /// Map the venue's time-in-force token onto the model (T-191): the spot
    /// surface reports a maker-only limit as the token "GTX", which the
    /// model holds as a GTC order carrying the post-only flag — the venue
    /// token is an execution instruction, not a distinct time-in-force. Any
    /// other token parses as the enum; an unparsable one refuses the row.
    /// </summary>
    private static bool TryParseVenueTimeInForce(string? timeInForceStr, out TimeInForce parsed, out bool postOnly)
    {
        postOnly = false;
        if (timeInForceStr == TimeInForceNames.Gtx)
        {
            parsed = TimeInForce.GTC;
            postOnly = true;
            return true;
        }

        return Enum.TryParse(timeInForceStr, false, out parsed);
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
        item.ValueKind == JsonValueKind.Object &&
        item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// Read one venue timestamp member as its invariant text: the REST rows
    /// carry it as a JSON number, the executionReport as a string; both are
    /// honest sources, anything else is absent.
    /// </summary>
    private static string? TryGetTimestampMember(JsonElement item, string property) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(property, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.Number => value.GetInt64().ToString(CultureInfo.InvariantCulture),
                JsonValueKind.String => value.GetString(),
                _ => null,
            }
            : null;

    /// <summary>Read one venue numeric member (order ids ride JSON numbers) as its invariant string.</summary>
    private static string? TryGetNumberAsString(JsonElement item, string property) =>
        item.ValueKind == JsonValueKind.Object &&
        item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt64().ToString(CultureInfo.InvariantCulture)
            : null;

    /// <summary>Read one venue error code off a failure body; absent yields the honest "no code".</summary>
    private static string TryGetVenueCode(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(FrameFieldNames.Code, out var code)
            ? code.ToString()
            : "no code";

    /// <summary>Read one venue error message off a failure body; absent yields the honest "no message".</summary>
    private static string TryGetVenueMessage(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(FrameFieldNames.Msg, out var msg)
            ? msg.ToString()
            : "no message";

    #endregion

    #region Nested Types

    private static class ApiKeyHeaderNames
    {
        public const string Key = "X-MBX-APIKEY";
    }

    private static class SideNames
    {
        public const string Buy = "BUY";
        public const string Sell = "SELL";
    }

    private static class OrderTypeNames
    {
        public const string Limit = "LIMIT";

        /// <summary>The venue's maker-only order type another client's GTX order can surface as (T-191).</summary>
        public const string LimitMaker = "LIMIT_MAKER";
    }

    private static class TimeInForceNames
    {
        public const string Gtc = "GTC";

        /// <summary>The venue's maker-only token for spot limit orders (T-191).</summary>
        public const string Gtx = "GTX";
    }

    private static class EventNames
    {
        /// <summary>The user-data frame's event-type member.</summary>
        public const string ExecutionReport = "executionReport";
    }

    private static class FrameFieldNames
    {
        /// <summary>The user-data frame's event-type member ("e").</summary>
        public const string EventType = "e";

        public const string Symbol = "s";
        public const string Side = "S";
        public const string OrderType = "o";
        public const string TimeInForce = "f";
        public const string OrderId = "i";
        public const string ClientOrderId = "c";
        public const string Quantity = "q";
        public const string Price = "p";
        public const string CumulativeFilled = "z";
        public const string OrderStatus = "X";
        public const string OrderTime = "O";
        public const string EventTime = "E";

        // The REST open-orders row members (Binance spells these out; the
        // executionReport above carries the single-letter abbreviations).
        public const string RestSymbol = "symbol";
        public const string RestSide = "side";
        public const string RestOrderStatus = "status";
        public const string RestOrderType = "type";
        public const string RestTimeInForce = "timeInForce";
        public const string RestPrice = "price";
        public const string RestOrderId = "orderId";
        public const string RestClientOrderId = "clientOrderId";

        // The REST row and account members (spelled out, unlike the report's
        // single-letter members).
        public const string OriginalQuantity = "origQty";
        public const string ExecutedQuantity = "executedQty";
        public const string CreatedTime = "time";
        public const string UpdatedTime = "updateTime";
        public const string Asset = "asset";
        public const string Free = "free";
        public const string Locked = "locked";
        public const string Balances = "balances";
        public const string ListenKey = "listenKey";
        public const string Code = "code";
        public const string Msg = "msg";
    }

    /// <summary>The fetchable testnet credential pair, held only for the request's identity.</summary>
    /// <param name="ApiKey">The testnet API key.</param>
    /// <param name="ApiSecret">The testnet API secret.</param>
    private sealed record BinanceCredentials(string ApiKey, string ApiSecret);

    /// <summary>One enqueued session flow with the completion handed back to its awaiting caller.</summary>
    /// <param name="Completion">The task the enqueuer awaits (the pump completes it on success, cancellation or failure).</param>
    private sealed record SessionRequest(TaskCompletionSource<bool> Completion);

    #endregion

    #region IAsyncDisposable

    /// <summary>
    /// Dispose the adapter: stop the owned loops, tear the standing private
    /// session down (a disposable-but-unobserved venue surface holds nothing
    /// anyway) and free the serialization gates. The market-data delegate is
    /// container-owned and outlives this surface.
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

        _orderUpdates.Dispose();
        _tradingGate.Dispose();
        _sessionGate.Dispose();

        await TearDownStandingSessionAsync();
        _orders.Clear();
    }

    #endregion
}
