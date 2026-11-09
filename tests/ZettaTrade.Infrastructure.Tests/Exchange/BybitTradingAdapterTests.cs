namespace ZettaTrade.Infrastructure.Tests.Exchange;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Infrastructure.Exchange;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// The trading-behavior suite for the Bybit testnet adapter (T-127), REST
/// seam: signed request shape (the v5 headers plus the HMAC-SHA256 signature
/// over timestamp + apiKey + recvWindow + body|queryString), the strict
/// family gates (limit-GTC only; OCO/reduce-only/non-GTC refused BEFORE the
/// venue; rejects surface as errors), the strict row mapping (unmappable
/// rows dropped with a logged reason, never defaulted) and the credentials
/// contract (secrets only from the store, per operation).
///
/// The private WebSocket rides the in-process loopback server, so a
/// placement's lazy session never touches a real host; the tests acknowledge
/// the auth+subscribe flow so disposal is prompt (the adapter waits for the
/// auth ack for its own bounded window).
/// </summary>
public sealed class BybitTradingAdapterTests
{
    #region Constants

    /// <summary>The testnet API key the stub secret store serves.</summary>
    private const string StubApiKey = "kt-api-key";

    /// <summary>The testnet API secret the stub secret store serves.</summary>
    private const string StubApiSecret = "kt-api-secret";

    /// <summary>An accepted placement ack (live v5 shape: retCode 0, both ids echoed).</summary>
    private const string PlaceAckFrame = """
        {"retCode":0,"retMsg":"OK","result":{"orderId":"venue-1","orderLinkId":"echoed-link"},"retExtInfo":{},"time":1700000000000}
        """;

    /// <summary>The REST paths this suite answers (the adapter's own private constants, mirrored).</summary>
    private static class BybitExchangeWirePaths
    {
        public const string CreateOrder = "/v5/order/create";
        public const string CancelOrder = "/v5/order/cancel";
        public const string OpenOrders = "/v5/order/realtime";
        public const string Positions = "/v5/position/list";
        public const string WalletBalance = "/v5/account/wallet-balance";
    }

    /// <summary>The harness-side HMAC mirror of the adapter's signing helper.</summary>
    private static class BybitTestHarnessSignature
    {
        public static string HexHmacSha256(string secret, string payload)
        {
            using var hmac = new System.Security.Cryptography.HMACSHA256(Encoding.UTF8.GetBytes(secret));
            return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
        }
    }

    /// <summary>The venue's reject shape (HTTP 200, non-zero retCode).</summary>
    private const string VenueRejectFrame = """
        {"retCode":10001,"retMsg":"OrderError: qty not in range","result":{},"retExtInfo":{},"time":1700000000000}
        """;

    private const string CancelAckFrame = """
        {"retCode":0,"retMsg":"OK","result":{},"retExtInfo":{},"time":1700000000000}
        """;

    /// <summary>An open-orders page: one fully mappable row, one orderId-only row, one dropped each.</summary>
    private const string OpenOrdersFrame = """
        {"retCode":0,"retMsg":"OK","result":{"list":[
          {"orderId":"venue-1","orderLinkId":"zt-link-one","symbol":"BTCUSDT","side":"Buy","orderStatus":"New","orderType":"Limit","timeInForce":"GTC","qty":"1.5","price":"45000.5","cumExecQty":"0","createdTime":"1700000000000","updatedTime":"1700000001000"},
          {"orderId":"venue-2","symbol":"BTCUSDT","orderIdSymbol":"x","side":"Sell","orderStatus":"PartiallyFilled","orderType":"Limit","timeInForce":"GTC","qty":"2","price":"45001.25","cumExecQty":"0.5","createdTime":"1700000002000","updatedTime":"1700000003000"},
          {"orderId":"venue-3","orderLinkId":"zt-link-three","symbol":"BT^C","side":"Buy","orderStatus":"New","orderType":"Limit","timeInForce":"GTC","qty":"1","price":"1","cumExecQty":"0"},
          {"orderId":"venue-4","orderLinkId":"zt-link-four","symbol":"BTCUSDT","side":"Buy","orderStatus":"Untriggered","orderType":"Limit","timeInForce":"GTC","qty":"1","price":"1","cumExecQty":"0"},
          {"orderId":"venue-5","orderLinkId":"zt-link-five","symbol":"BTCUSDT","side":"Buy","orderStatus":"New","orderType":"Limit","timeInForce":"GTC","qty":"1","cumExecQty":"0"}
        ]},"retExtInfo":{},"time":1700000000000}
        """;

    private const string EmptyOpenOrdersFrame = """
        {"retCode":0,"retMsg":"OK","result":{"list":[]},"retExtInfo":{},"time":1700000000000}
        """;

    private const string MalformedOpenOrdersFrame = """
        {"retCode":0,"retMsg":"OK","result":{},"retExtInfo":{},"time":1700000000000}
        """;

    private const string PositionsFrame = """
        {"retCode":0,"retMsg":"OK","result":{"list":[
          {"symbol":"ETHUSDT","side":"Buy","size":"3.25","avgPrice":"2100.75","updatedTime":"1700000000000"},
          {"symbol":"BTCUSDT","side":"None","size":"0","avgPrice":"0","updatedTime":"1700000000000"},
          {"symbol":"SOLUSDT","side":"Buy","size":"oops","avgPrice":"1","updatedTime":"1700000000000"}
        ]},"retExtInfo":{},"time":1700000000000}
        """;

    private const string BalancesFrame = """
        {"retCode":0,"retMsg":"OK","result":{"list":[
          {"totalEquity":"0","coin":[
            {"coin":"USDT","walletBalance":"10.5"},
            {"coin":"BTC","walletBalance":"0"},
            {"coin":"BROKEN","walletBalance":"not-a-number"}
          ]}
        ]},"retExtInfo":{},"time":1700000000000}
        """;

    /// <summary>The market data the trading adapter passes through; never contacted by the REST suite.</summary>
    private readonly IMarketDataAdapter _marketData = Substitute.For<IMarketDataAdapter>();

    #endregion

    #region Tests: placement

    [Fact]
    public async Task PlaceOrder_PutsTheSignedLimitBodyOnTheWireAndReturnsTheAcceptedOrder()
    {
        await using var stack = await TestStack.StartAsync();
        stack.Rest.SetResponse(BybitExchangeWirePaths.CreateOrder, PlaceAckFrame);

        var request = LimitRequest(OrderSide.Buy, 1.5m, 45000.5m);
        var accepted = await stack.Adapter.PlaceOrderAsync(request);

        // The wire body: category, symbol, side, limit flavor, invariants.
        var body = stack.Rest.LastBody;
        body.Should().Contain("\"category\":\"spot\"");
        body.Should().Contain("\"symbol\":\"BTCUSDT\"");
        body.Should().Contain("\"side\":\"Buy\"");
        body.Should().Contain("\"orderType\":\"Limit\"");
        body.Should().Contain("\"qty\":\"1.5\"");
        body.Should().Contain("\"price\":\"45000.5\"");
        body.Should().Contain("\"timeInForce\":\"GTC\"");
        body.Should().Contain("\"orderLinkId\":\"zt-");

        // The headers: the v5 auth set (the signature itself is the dedicated test).
        stack.Rest.LastRequest.Should().NotBeNull();
        stack.Rest.LastRequest!.Headers.Contains("X-BAPI-API-KEY").Should().BeTrue();
        stack.Rest.LastRequest.Headers.ToString().Should().Contain("X-BAPI-TIMESTAMP");
        stack.Rest.LastRequest.Headers.ToString().Should().Contain("X-BAPI-RECV-WINDOW");
        stack.Rest.LastRequest.Headers.ToString().Should().Contain("X-BAPI-SIGN");
        stack.Rest.LastRequest.RequestUri!.ToString().Should().Be("https://stub.local/v5/order/create");
        stack.Rest.LastRequest.Method.Should().Be(HttpMethod.Post);

        // The accepted Core order: the surface's own link id leads, the venue
        // order id attaches, the lifecycle opens at Submitted.
        accepted.Id.Should().Be("echoed-link");
        accepted.ExchangeId.Should().Be(BybitTradingAdapter.ExchangeId);
        accepted.ExchangeOrderId.Should().Be("venue-1");
        accepted.Symbol.Should().Be(request.Symbol);
        accepted.Side.Should().Be(OrderSide.Buy);
        accepted.Type.Should().Be(OrderType.Limit);
        accepted.Quantity.Value.Should().Be(1.5m);
        accepted.Price!.Value.Should().Be(45000.5m);
        accepted.TimeInForce.Should().Be(TimeInForce.GTC);
        accepted.Status.Should().Be(OrderStatus.Submitted);
        accepted.FilledQuantity.Value.Should().Be(0m);

        // The venue echo replaces the generated id on the Core order record;
        // the client order id holds the surface's own (prefix + GUID N-form).
        accepted.ClientOrderId.Should().StartWith("zt-");
        accepted.ClientOrderId.Should().HaveLength(35);

        await stack.AcknowledgeSessionAsync();
    }

    [Fact]
    public async Task PlaceOrder_HonorsTheRequestClientOrderId()
    {
        await using var stack = await TestStack.StartAsync();

        var order = await stack.Adapter.PlaceOrderAsync(
            LimitRequest(OrderSide.Sell, 0.25m, 45500m) with { ClientOrderId = "caller-id" });

        stack.Rest.LastBody.Should().Contain("\"orderLinkId\":\"caller-id\"");
        order.ClientOrderId.Should().Be("caller-id");

        await stack.AcknowledgeSessionAsync();
    }

    [Fact]
    public async Task PlaceOrder_SignsThePostWithTheTimestampKeyWindowAndBody()
    {
        await using var stack = await TestStack.StartAsync();

        await stack.Adapter.PlaceOrderAsync(LimitRequest(OrderSide.Buy, 1m, 100m));

        var headers = stack.Rest.LastRequest!.Headers.NonValidated;
        var timestamp = headers["X-BAPI-TIMESTAMP"].Single();
        var recvWindow = headers["X-BAPI-RECV-WINDOW"].Single();
        var signature = headers["X-BAPI-SIGN"].Single();

        timestamp.Should().MatchRegex("^\\d+$");
        recvWindow.Should().Be(BybitTradingAdapter.RecvWindow);

        var payload = timestamp + StubApiKey + BybitTradingAdapter.RecvWindow + stack.Rest.LastBody;
        signature.Should().Be(BybitTestHarnessSignature.HexHmacSha256(StubApiSecret, payload));

        await stack.AcknowledgeSessionAsync();
    }

    [Fact]
    public async Task PlaceOrder_VenueRejectSurfacesAsAnError()
    {
        await using var stack = await TestStack.StartAsync();
        stack.Rest.SetResponse(BybitExchangeWirePaths.CreateOrder, VenueRejectFrame);

        var place = async () => await stack.Adapter.PlaceOrderAsync(LimitRequest(OrderSide.Buy, 100m, 1m));

        var rejected = await place.Should().ThrowAsync<InvalidOperationException>();
        rejected.Which.Message.Should().Contain("10001");
        rejected.Which.Message.Should().Contain("OrderError: qty not in range");

        // The session came up before the venue answered; acknowledge its flow
        // so the disposal does not wait out the adapter's auth-ack window.
        await stack.AcknowledgeSessionAsync();
    }

    [Fact]
    public async Task PlaceOrder_RefusesTheMarketFamilyBeforeTheVenue()
    {
        await using var stack = await TestStack.StartAsync();

        var request = new OrderRequest
        {
            Symbol = Symbol.Parse("BTC/USDT"),
            Side = OrderSide.Buy,
            Type = OrderType.Market,
            Quantity = new Quantity(1m),
        };

        var place = async () => await stack.Adapter.PlaceOrderAsync(request);

        var refusal = await place.Should().ThrowAsync<InvalidOperationException>();
        refusal.Which.Message.Should().Contain("Limit orders only");
        stack.Rest.RequestCount.Should().Be(0, "the refusal precedes any venue traffic");
    }

    [Fact]
    public async Task PlaceOrder_RefusesNonGtcAndOcoAndReduceOnlyShapes()
    {
        await using var stack = await TestStack.StartAsync();

        // IOC limit: refused on the time-in-force gate.
        var ioc = async () => await stack.Adapter.PlaceOrderAsync(
            LimitRequest(OrderSide.Buy, 1m, 100m) with { TimeInForce = TimeInForce.IOC });
        var iocRefusal = await ioc.Should().ThrowAsync<InvalidOperationException>();
        iocRefusal.Which.Message.Should().Contain("GoodTillCancelled");

        // OCO leg: refused on the pair gate.
        var oco = async () => await stack.Adapter.PlaceOrderAsync(
            LimitRequest(OrderSide.Buy, 1m, 100m) with { OcoGroupId = "oco-group" });
        var ocoRefusal = await oco.Should().ThrowAsync<InvalidOperationException>();
        ocoRefusal.Which.Message.Should().Contain("OCO");

        // Reduce-only: refused on the spot-shape gate.
        var reduceOnly = async () => await stack.Adapter.PlaceOrderAsync(
            LimitRequest(OrderSide.Buy, 1m, 100m) with { ReduceOnly = true });
        var reduceRefusal = await reduceOnly.Should().ThrowAsync<InvalidOperationException>();
        reduceRefusal.Which.Message.Should().Contain("reduce-only");

        stack.Rest.RequestCount.Should().Be(0, "the refusals precede any venue traffic");
        stack.Rest.RequestCount.Should().Be(0, "the refusals precede any venue traffic");
    }

    [Fact]
    public async Task PlaceOrder_WithoutCredentials_ThrowsNamingTheSecretKeys()
    {
        await using var stack = await TestStack.StartAsync(withCredentials: false);

        var place = async () => await stack.Adapter.PlaceOrderAsync(LimitRequest(OrderSide.Buy, 1m, 100m));

        var refused = await place.Should().ThrowAsync<InvalidOperationException>();
        refused.Which.Message.Should().Contain(BybitTradingAdapter.ApiKeySecretKey);
        refused.Which.Message.Should().Contain(BybitTradingAdapter.ApiSecretSecretKey);
        stack.Rest.RequestCount.Should().Be(0, "an unauthenticated venue request is never sent");
    }

    #endregion

    #region Tests: cancel + queries

    [Fact]
    public async Task CancelOrder_SendsTheSymbolScopedLinkIdBody()
    {
        await using var stack = await TestStack.StartAsync();
        stack.Rest.SetResponse(BybitExchangeWirePaths.CancelOrder, CancelAckFrame);

        await stack.Adapter.CancelOrderAsync(Symbol.Parse("BTC/USDT"), "zt-link-one");

        stack.Rest.LastRequest!.Method.Should().Be(HttpMethod.Post);
        stack.Rest.LastRequest.RequestUri!.ToString().Should().Be("https://stub.local/v5/order/cancel");
        stack.Rest.LastBody.Should().Contain("\"category\":\"spot\"");
        stack.Rest.LastBody.Should().Contain("\"symbol\":\"BTCUSDT\"");
        stack.Rest.LastBody.Should().Contain("\"orderLinkId\":\"zt-link-one\"");
    }

    [Fact]
    public async Task GetOpenOrders_MapsTheMappableRowsAndDropsTheRestWithReasons()
    {
        await using var stack = await TestStack.StartAsync();
        stack.Rest.SetResponse(BybitExchangeWirePaths.OpenOrders, OpenOrdersFrame);

        var orders = await stack.Adapter.GetOpenOrdersAsync();

        // The two honest rows: a link-id-keyed one and an orderId-keyed one.
        orders.Where(order => order.Id == "zt-link-one").Should().ContainSingle();
        var orderIdKeyed = orders.Should().ContainSingle(order => order.Id == "venue-2").Subject;
        orders.Should().HaveCount(2);

        orderIdKeyed.Status.Should().Be(OrderStatus.PartiallyFilled);
        orderIdKeyed.FilledQuantity.Value.Should().Be(0.5m);
        orderIdKeyed.Price.Value.Should().Be(45001.25m);
        orderIdKeyed.CreatedAt.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1700000002000));
        orderIdKeyed.UpdatedAt.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1700000003000));

        // The drops surface as logged warnings with row fidelity (never silent):
        // the unparsable symbol, the unmappable status and the missing price.
        var warnings = stack.Logger
            .Entries
            .Where(entry => entry.Level == LogLevel.Warning)
            .Select(entry => entry.Message)
            .ToArray();
        warnings.Should().HaveCount(3);
        warnings.Single(w => w.Contains("BT^C")).Should().Contain("not parsable");
        warnings.Single(w => w.Contains("Untriggered")).Should().Contain("Untriggered");
        warnings.Single(w => w.Contains("venue-5")).Should().Contain("parsable limit price");
    }

    [Fact]
    public async Task GetOpenOrders_EmptyPageYieldsNothing()
    {
        await using var stack = await TestStack.StartAsync();
        stack.Rest.SetResponse(BybitExchangeWirePaths.OpenOrders, EmptyOpenOrdersFrame);

        var orders = await stack.Adapter.GetOpenOrdersAsync();

        orders.Should().BeEmpty();
    }

    [Fact]
    public async Task GetOpenOrders_MalformedResultIsAnError()
    {
        await using var stack = await TestStack.StartAsync();
        stack.Rest.SetResponse(BybitExchangeWirePaths.OpenOrders, MalformedOpenOrdersFrame);

        var query = async () => await stack.Adapter.GetOpenOrdersAsync();

        var refused = await query.Should().ThrowAsync<InvalidOperationException>();
        refused.Which.Message.Should().Contain("no list member");
    }

    [Fact]
    public async Task GetOpenOrders_SignsTheGetWithTheQueryString()
    {
        await using var stack = await TestStack.StartAsync();
        stack.Rest.SetResponse(BybitExchangeWirePaths.OpenOrders, EmptyOpenOrdersFrame);

        await stack.Adapter.GetOpenOrdersAsync();

        stack.Rest.LastRequest!.RequestUri!.ToString().Should().Be("https://stub.local/v5/order/realtime?category=spot");
        var headers = stack.Rest.LastRequest.Headers.NonValidated;
        var timestamp = headers["X-BAPI-TIMESTAMP"].Single();
        var signature = headers["X-BAPI-SIGN"].Single();
        var payload = timestamp + StubApiKey + BybitTradingAdapter.RecvWindow + "category=spot";
        signature.Should().Be(BybitTestHarnessSignature.HexHmacSha256(StubApiSecret, payload));
    }

    [Fact]
    public async Task GetPositions_MapsTheHonestRowsAndDropsTheNoise()
    {
        await using var stack = await TestStack.StartAsync();
        stack.Rest.SetResponse(BybitExchangeWirePaths.Positions, PositionsFrame);

        var positions = await stack.Adapter.GetPositionsAsync();

        var longEntry = positions.Should().ContainSingle().Subject;
        longEntry.Symbol.Should().Be(Symbol.Parse("ETH/USDT"));
        longEntry.Side.Should().Be(OrderSide.Buy);
        longEntry.Quantity.Value.Should().Be(3.25m);
        longEntry.EntryPrice.Value.Should().Be(2100.75m);
        longEntry.ExchangeId.Should().Be(BybitTradingAdapter.ExchangeId);
    }

    [Fact]
    public async Task GetBalances_QueriesTheUnifiedAccountAndDropsEmptyAndUnparsableRows()
    {
        await using var stack = await TestStack.StartAsync();
        stack.Rest.SetResponse(BybitExchangeWirePaths.WalletBalance, BalancesFrame);

        var balances = await stack.Adapter.GetBalancesAsync();

        stack.Rest.LastRequest!.RequestUri!.ToString().Should().Be("https://stub.local/v5/account/wallet-balance?accountType=UNIFIED");
        balances.Keys.Should().BeEquivalentTo("USDT");
        var usdt = balances.Should().ContainKey("USDT").WhoseValue;
        usdt.Amount.Should().Be(10.5m);
        usdt.Currency.Should().Be("USDT");
    }

    #endregion

    #region Test fixtures

    private static OrderRequest LimitRequest(OrderSide side, decimal quantity, decimal price) => new()
    {
        Symbol = Symbol.Parse("BTC/USDT"),
        Side = side,
        Type = OrderType.Limit,
        Quantity = new Quantity(quantity),
        Price = new Price(price),
        TimeInForce = TimeInForce.GTC,
    };

    /// <summary>The per-test stack: loopback WS (the lazily-connected private session), stub REST + secrets.</summary>
    private sealed class TestStack : IAsyncDisposable
    {
        public BybitTradingAdapter Adapter { get; private set; } = null!;
        public StubRecordingHandler Rest { get; private set; } = null!;
        public RecordingLogger Logger { get; private set; } = null!;
        private LoopbackWebSocketServer Server { get; set; } = null!;

        public static async Task<TestStack> StartAsync(bool withCredentials = true)
        {
            var server = await LoopbackWebSocketServer.StartAsync();
            var rest = new StubRecordingHandler();
            var credentials = new StubSecretStore(withCredentials);

            var logger = new RecordingLogger();
            var adapter = new BybitTradingAdapter(
                logger,
                new StubHttpClientFactory(rest),
                Substitute.For<IMarketDataAdapter>(),
                credentials,
                new BybitTradingEndpoints(
                    RestBaseUrl: "https://stub.local",
                    PrivateWsUrl: server.CombinedStreamUrl));

            return new TestStack
            {
                Adapter = adapter,
                Rest = rest,
                Logger = logger,
                Server = server,
            };
        }

        /// <summary>
        /// Acknowledge the lazy session's auth + subscribe handshakes: after
        /// a placement the session flow completes promptly and disposal does
        /// not wait out the adapter's bounded auth-ack window.
        /// </summary>
        public async Task AcknowledgeSessionAsync()
        {
            var connection = await Server.WaitForConnectionAsync(1, TimeSpan.FromSeconds(30));
            await Server.WaitForFrameAsync(f => f.Contains("\"op\":\"auth\""), TimeSpan.FromSeconds(30));
            await connection.SendTextAsync("{\"success\":true,\"op\":\"auth\"}");

            await Server.WaitForFrameAsync(f => f.Contains("\"op\":\"subscribe\""), TimeSpan.FromSeconds(30));
            await connection.SendTextAsync("{\"success\":true,\"op\":\"subscribe\",\"args\":[\"order.spot\"]}");
        }

        public async ValueTask DisposeAsync()
        {
            await Adapter.DisposeAsync();
            await Server.DisposeAsync();
        }
    }

    /// <summary>The stub v5 secret pair (never logs, serves the two keys only).</summary>
    private sealed class StubSecretStore : ISecretStore
    {
        public StubSecretStore(bool withCredentials)
        {
            _apiKey = withCredentials ? StubApiKey : null;
            _apiSecret = withCredentials ? StubApiSecret : null;
        }

        private readonly string? _apiKey;
        private readonly string? _apiSecret;

        public Task<string?> GetAsync(string key, CancellationToken ct = default) => Task.FromResult(
            key == BybitTradingAdapter.ApiKeySecretKey ? _apiKey
            : key == BybitTradingAdapter.ApiSecretSecretKey ? _apiSecret
            : null);

        public Task SetAsync(string key, string value, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RemoveAsync(string key, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> ListKeysAsync(string prefix = "", CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);
    }

    /// <summary>The recording REST transport: canned canned results per path + the captured request bits.</summary>
    private sealed class StubRecordingHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _responses = new(StringComparer.Ordinal);
        private int _requestCount;

        public HttpRequestMessage? LastRequest { get; private set; }
        public string LastBody { get; private set; } = string.Empty;
        public int RequestCount => Volatile.Read(ref _requestCount);

        public void SetResponse(string path, string content) => _responses[path] = content;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken ct)
        {
            Interlocked.Increment(ref _requestCount);
            LastRequest = request;
            LastBody = request.Content is { } content
                ? await content.ReadAsStringAsync(ct)
                : string.Empty;

            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var body = _responses.TryGetValue(path, out var canned)
                ? canned
                : "{\"retCode\":0,\"retMsg\":\"OK\",\"result\":{},\"retExtInfo\":{},\"time\":1}";

            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler);
    }

    /// <summary>The recording logger: captures every formatted entry in order.</summary>
    private sealed class RecordingLogger : ILogger<BybitTradingAdapter>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

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

    #endregion
}
