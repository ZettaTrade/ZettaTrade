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
/// The trading-behavior suite for the Binance testnet adapter (T-193), REST
/// seam: signed request shape (the <c>X-MBX-APIKEY</c> header plus the
/// HMAC-SHA256 hex signature over the exact query string sent, the signature
/// member appended last), the strict family gates (limit-GTC only; market/
/// OCO/reduce-only/bracket/non-GTC refused BEFORE the venue), the strict row
/// mapping (unmappable rows dropped with a logged reason, never defaulted),
/// the rejected-not-default venue error contract (both the HTTP-error body
/// and the 200-with-negative-code shape) and the credentials contract
/// (secrets only from the store, per operation).
///
/// The private WebSocket rides the in-process loopback server, so a
/// placement's lazy listen-key session never touches a real host; the
/// user-data stream carries no handshake to acknowledge, so disposal is
/// prompt by construction.
/// </summary>
public sealed class BinanceTradingAdapterTests
{
    #region Constants

    /// <summary>The testnet API key the stub secret store serves.</summary>
    private const string StubApiKey = "bt-api-key";

    /// <summary>The testnet API secret the stub secret store serves.</summary>
    private const string StubApiSecret = "bt-api-secret";

    /// <summary>An accepted placement ack (live spot shape: both ids echoed).</summary>
    private const string PlaceAckFrame = """
        {"symbol":"BTCUSDT","orderId":28,"orderListId":-1,"clientOrderId":"echoed-zt","transactTime":1700000000000}
        """;

    /// <summary>The venue's reject shape (HTTP 200, negative code).</summary>
    private const string VenueRejectFrame = """
        {"code":-2010,"msg":"Account has insufficient balance for requested action."}
        """;

    private const string CancelAckFrame = """
        {"symbol":"BTCUSDT","origClientOrderId":"zt-link-one","orderId":28,"orderListId":-1,"clientOrderId":"zt-link-one","price":"45000.50","origQty":"1.500","executedQty":"0.000","status":"CANCELED","timeInForce":"GTC","type":"LIMIT","side":"BUY"}
        """;

    /// <summary>An open-orders page: one fully mappable row, one orderId-only row, three dropped each.</summary>
    private const string OpenOrdersFrame = """
        [
          {"symbol":"BTCUSDT","orderId":11,"clientOrderId":"zt-link-one","price":"45000.5","origQty":"1.5","executedQty":"0","status":"NEW","timeInForce":"GTC","type":"LIMIT","side":"BUY","time":1700000000000,"updateTime":1700000001000},
          {"symbol":"BTCUSDT","orderId":12,"clientOrderId":"","price":"45001.25","origQty":"2","executedQty":"0.5","status":"PARTIALLY_FILLED","timeInForce":"GTC","type":"LIMIT","side":"SELL","time":1700000002000,"updateTime":1700000003000},
          {"symbol":"BT^C","orderId":13,"clientOrderId":"zt-link-three","price":"1","origQty":"1","executedQty":"0","status":"NEW","timeInForce":"GTC","type":"LIMIT","side":"BUY"},
          {"symbol":"BTCUSDT","orderId":14,"clientOrderId":"zt-link-four","price":"1","origQty":"1","executedQty":"0","status":"PENDING_NEW","timeInForce":"GTC","type":"LIMIT","side":"BUY"},
          {"symbol":"BTCUSDT","orderId":15,"clientOrderId":"zt-link-five","origQty":"1","executedQty":"0","status":"NEW","timeInForce":"GTC","type":"LIMIT","side":"BUY"}
        ]
        """;

    /// <summary>The post-only rows: the venue's GTX token and the LIMIT_MAKER type both carry the maker-only identity.</summary>
    private const string PostOnlyOpenOrdersFrame = """
        [
          {"symbol":"BTCUSDT","orderId":21,"clientOrderId":"zt-link-gtx","price":"45000.5","origQty":"1","executedQty":"0","status":"NEW","timeInForce":"GTX","type":"LIMIT","side":"BUY"},
          {"symbol":"ETHUSDT","orderId":22,"clientOrderId":"zt-link-maker","price":"3000.25","origQty":"2","executedQty":"0","status":"NEW","timeInForce":"GTC","type":"LIMIT_MAKER","side":"SELL"}
        ]
        """;

    private const string EmptyOpenOrdersFrame = "[]";

    private const string MalformedOpenOrdersFrame = """
        {"code":0,"msg":"OK"}
        """;

    private const string BalancesFrame = """
        {"balances":[
          {"asset":"USDT","free":"10.5","locked":"0"},
          {"asset":"BTC","free":"0","locked":"0"},
          {"asset":"BROKEN","free":"not-a-number","locked":"1"}
        ]}
        """;

    /// <summary>The market data the trading adapter passes through; never contacted by the REST suite.</summary>
    private readonly IMarketDataAdapter _marketData = Substitute.For<IMarketDataAdapter>();

    #endregion

    #region Tests: placement

    [Fact]
    public async Task PlaceOrder_PutsTheSignedLimitQueryOnTheWireAndReturnsTheAcceptedOrder()
    {
        await using var stack = await TestStack.StartAsync();
        stack.Rest.SetResponse(BinanceWirePaths.CreateOrder, PlaceAckFrame);

        var request = LimitRequest(OrderSide.Buy, 1.5m, 45000.5m);
        var accepted = await stack.Adapter.PlaceOrderAsync(request);

        // The wire query: symbol, side, limit flavor, invariants, the signed
        // tail. No body — the signed query string IS the request.
        var query = stack.Rest.LastRequest!.RequestUri!.Query;
        query.Should().Contain("symbol=BTCUSDT");
        query.Should().Contain("side=BUY");
        query.Should().Contain("type=LIMIT");
        query.Should().Contain("timeInForce=GTC");
        query.Should().Contain("quantity=1.5");
        query.Should().Contain("price=45000.5");
        query.Should().Contain("newClientOrderId=zt-");
        query.Should().Contain("&timestamp=");
        query.Should().Contain("&recvWindow=5000");
        query.Should().Contain("&signature=");
        stack.Rest.LastRequest.RequestUri!.ToString().Should().StartWith("https://stub.local/api/v3/order?");
        stack.Rest.LastRequest.Method.Should().Be(HttpMethod.Post);

        // The headers: the venue's own API-key carrier (the signature rides
        // the query string, never a header).
        stack.Rest.LastRequest.Headers.ToString().Should().Contain("X-MBX-APIKEY");

        // The accepted Core order: the venue echo leads, the venue order id
        // attaches, the lifecycle opens at Submitted.
        accepted.Id.Should().Be("echoed-zt");
        accepted.ExchangeId.Should().Be(BinanceTradingAdapter.ExchangeId);
        accepted.ExchangeOrderId.Should().Be("28");
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
    }

    [Fact]
    public async Task PlaceOrder_PostOnlyLimit_SendsTheMakerOnlyTokenAndEchoesTheFlag()
    {
        await using var stack = await TestStack.StartAsync();
        stack.Rest.SetResponse(BinanceWirePaths.CreateOrder, PlaceAckFrame);

        var accepted = await stack.Adapter.PlaceOrderAsync(
            LimitRequest(OrderSide.Buy, 1.5m, 45000.5m) with { PostOnly = true });

        // The wire query carries the venue's maker-only token (T-191's GTX
        // discipline mirrored); the Core echo keeps the plain GTC
        // time-in-force with the flag riding beside it.
        stack.Rest.LastRequest!.RequestUri!.Query.Should().Contain("timeInForce=GTX");
        accepted.TimeInForce.Should().Be(TimeInForce.GTC);
        accepted.PostOnly.Should().BeTrue();
    }

    [Fact]
    public async Task PlaceOrder_HonorsTheRequestClientOrderId()
    {
        await using var stack = await TestStack.StartAsync();

        var order = await stack.Adapter.PlaceOrderAsync(
            LimitRequest(OrderSide.Sell, 0.25m, 45500m) with { ClientOrderId = "caller-id" });

        stack.Rest.LastRequest!.RequestUri!.Query.Should().Contain("newClientOrderId=caller-id");
        order.ClientOrderId.Should().Be("caller-id");
    }

    [Fact]
    public async Task PlaceOrder_SignsTheQueryWithTheTimestampWindowAndSignature()
    {
        await using var stack = await TestStack.StartAsync();

        await stack.Adapter.PlaceOrderAsync(LimitRequest(OrderSide.Buy, 1m, 100m));

        var query = stack.Rest.LastRequest!.RequestUri!.Query.TrimStart('?');
        var apiKey = stack.Rest.LastRequest.Headers.NonValidated["X-MBX-APIKEY"].Single();
        apiKey.Should().Be(StubApiKey);

        // The signature covers the EXACT query string sent, signature
        // member excluded (it is appended after signing).
        var signedIndex = query.LastIndexOf("&signature=", StringComparison.Ordinal);
        signedIndex.Should().BeGreaterThan(0, "the signed query carries its signature member last");
        var signedPayload = query[..signedIndex];
        var signature = query[(signedIndex + "&signature=".Length)..];

        signature.Should().Be(HexHmacSha256(StubApiSecret, signedPayload));
        signedPayload.Should().Contain("&timestamp=");
        signedPayload.Should().Contain("&recvWindow=" + BinanceTradingAdapter.RecvWindow);
    }

    [Fact]
    public async Task PlaceOrder_VenueRejectSurfacesAsAnError()
    {
        await using var stack = await TestStack.StartAsync();
        stack.Rest.SetResponse(BinanceWirePaths.CreateOrder, VenueRejectFrame);

        var place = async () => await stack.Adapter.PlaceOrderAsync(LimitRequest(OrderSide.Buy, 100m, 1m));

        var rejected = await place.Should().ThrowAsync<InvalidOperationException>();
        rejected.Which.Message.Should().Contain("-2010");
        rejected.Which.Message.Should().Contain("Account has insufficient balance");
    }

    [Fact]
    public async Task PlaceOrder_HttpFailureSurfacesAsAnError()
    {
        await using var stack = await TestStack.StartAsync();
        stack.Rest.SetFailure(BinanceWirePaths.CreateOrder, System.Net.HttpStatusCode.BadRequest, """
            {"code":-1102,"msg":"Mandatory parameter 'quantity' was not sent."}
            """);

        var place = async () => await stack.Adapter.PlaceOrderAsync(LimitRequest(OrderSide.Buy, 100m, 1m));

        var rejected = await place.Should().ThrowAsync<InvalidOperationException>();
        rejected.Which.Message.Should().Contain("HTTP 400");
        rejected.Which.Message.Should().Contain("-1102");
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
    public async Task PlaceOrder_RefusesNonGtcAndOcoAndReduceOnlyAndBracketShapes()
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

        // Bracket entry: refused on the bracket gate — an entry carrying its
        // attached take-profit/stop-loss exits; the surface places single
        // orders only. The gate precedes the request's own validation, so the
        // payload's leg shapes are not exercised here.
        var bracket = async () => await stack.Adapter.PlaceOrderAsync(
            LimitRequest(OrderSide.Buy, 1m, 100m) with
            {
                BracketLegs = new BracketLegs
                {
                    TakeProfit = LimitRequest(OrderSide.Sell, 1m, 110m) with { OcoGroupId = "oco-bracket" },
                    StopLoss = LimitRequest(OrderSide.Sell, 1m, 90m) with
                    {
                        OcoGroupId = "oco-bracket",
                        StopPrice = new Price(95m),
                    },
                },
            });
        var bracketRefusal = await bracket.Should().ThrowAsync<InvalidOperationException>();
        bracketRefusal.Which.Message.Should().Contain("bracket orders");

        stack.Rest.RequestCount.Should().Be(0, "the refusals precede any venue traffic");
    }

    [Fact]
    public async Task PlaceOrder_WithoutCredentials_ThrowsNamingTheSecretKeys()
    {
        await using var stack = await TestStack.StartAsync(withCredentials: false);

        var place = async () => await stack.Adapter.PlaceOrderAsync(LimitRequest(OrderSide.Buy, 1m, 100m));

        var refused = await place.Should().ThrowAsync<InvalidOperationException>();
        refused.Which.Message.Should().Contain(BinanceTradingAdapter.ApiKeySecretKey);
        refused.Which.Message.Should().Contain(BinanceTradingAdapter.ApiSecretSecretKey);
        stack.Rest.RequestCount.Should().Be(0, "an unauthenticated venue request is never sent");
    }

    [Fact]
    public async Task PlaceOrder_ReusesTheStandingPrivateSession()
    {
        await using var stack = await TestStack.StartAsync();
        stack.Rest.SetResponse(BinanceWirePaths.CreateOrder, PlaceAckFrame);

        await stack.Adapter.PlaceOrderAsync(LimitRequest(OrderSide.Buy, 1m, 100m));
        await stack.Adapter.PlaceOrderAsync(LimitRequest(OrderSide.Buy, 1m, 100m));

        // One listen-key issuance serves both placements: the standing
        // session answers the second place-time connect.
        stack.Rest.RequestsOf(BinanceWirePaths.UserDataStream).Should().HaveCount(1);
        await stack.Server.WaitForConnectionAsync(1, TimeSpan.FromSeconds(30));
        stack.Server.ConnectionCount.Should().Be(1, "one private session, re-used");
    }

    #endregion

    #region Tests: cancel + queries

    [Fact]
    public async Task CancelOrder_SendsTheSymbolScopedSignedDelete()
    {
        await using var stack = await TestStack.StartAsync();
        stack.Rest.SetResponse(BinanceWirePaths.CancelOrder, CancelAckFrame);

        await stack.Adapter.CancelOrderAsync(Symbol.Parse("BTC/USDT"), "zt-link-one");

        stack.Rest.LastRequest!.Method.Should().Be(HttpMethod.Delete);
        var query = stack.Rest.LastRequest.RequestUri!.Query;
        query.Should().Contain("symbol=BTCUSDT");
        query.Should().Contain("origClientOrderId=zt-link-one");
        query.Should().Contain("&signature=");
        stack.Rest.LastRequest.Headers.ToString().Should().Contain("X-MBX-APIKEY");
    }

    [Fact]
    public async Task GetOpenOrders_MapsTheMappableRowsAndDropsTheRestWithReasons()
    {
        await using var stack = await TestStack.StartAsync();
        stack.Rest.SetResponse(BinanceWirePaths.OpenOrders, OpenOrdersFrame);

        var orders = await stack.Adapter.GetOpenOrdersAsync();

        // The two honest rows: a client-id-keyed one and an order-id-keyed one.
        orders.Where(order => order.Id == "zt-link-one").Should().ContainSingle();
        var orderIdKeyed = orders.Should().ContainSingle(order => order.Id == "12").Subject;
        orders.Should().HaveCount(2);

        orderIdKeyed.Status.Should().Be(OrderStatus.PartiallyFilled);
        orderIdKeyed.FilledQuantity.Value.Should().Be(0.5m);
        orderIdKeyed.Price.Value.Should().Be(45001.25m);
        orderIdKeyed.CreatedAt.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1700000002000));
        orderIdKeyed.UpdatedAt.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1700000003000));
        orderIdKeyed.ExchangeOrderId.Should().Be("12");

        // The drops surface as logged warnings with row fidelity (never silent):
        // the unparsable symbol, the unmappable status and the missing price.
        var warnings = stack.Logger
            .Entries
            .Where(entry => entry.Level == LogLevel.Warning)
            .Select(entry => entry.Message)
            .ToArray();
        warnings.Should().HaveCount(3);
        warnings.Single(w => w.Contains("BT^C")).Should().Contain("not parsable");
        warnings.Single(w => w.Contains("PENDING_NEW")).Should().Contain("no Core mapping");
        warnings.Single(w => w.Contains("zt-link-five")).Should().Contain("parsable limit price");
    }

    [Fact]
    public async Task GetOpenOrders_EmptyPageYieldsNothing()
    {
        await using var stack = await TestStack.StartAsync();
        stack.Rest.SetResponse(BinanceWirePaths.OpenOrders, EmptyOpenOrdersFrame);

        var orders = await stack.Adapter.GetOpenOrdersAsync();

        orders.Should().BeEmpty();
    }

    [Fact]
    public async Task GetOpenOrders_MapsThePostOnlyTokensOntoGtcWithTheFlag()
    {
        // The venue reports a maker-only limit as the "GTX" time-in-force or
        // the "LIMIT_MAKER" type; the model holds both as a GTC order
        // carrying the post-only flag (T-191) — the rows map, never drop.
        await using var stack = await TestStack.StartAsync();
        stack.Rest.SetResponse(BinanceWirePaths.OpenOrders, PostOnlyOpenOrdersFrame);

        var orders = await stack.Adapter.GetOpenOrdersAsync();

        orders.Should().HaveCount(2);
        var gtx = orders.Should().ContainSingle(order => order.Id == "zt-link-gtx").Subject;
        gtx.TimeInForce.Should().Be(TimeInForce.GTC);
        gtx.PostOnly.Should().BeTrue();
        var maker = orders.Should().ContainSingle(order => order.Id == "zt-link-maker").Subject;
        maker.TimeInForce.Should().Be(TimeInForce.GTC);
        maker.PostOnly.Should().BeTrue();
    }

    [Fact]
    public async Task GetOpenOrders_MalformedResultIsAnError()
    {
        await using var stack = await TestStack.StartAsync();
        stack.Rest.SetResponse(BinanceWirePaths.OpenOrders, MalformedOpenOrdersFrame);

        var query = async () => await stack.Adapter.GetOpenOrdersAsync();

        var refused = await query.Should().ThrowAsync<InvalidOperationException>();
        refused.Which.Message.Should().Contain("not an order array");
    }

    [Fact]
    public async Task GetOpenOrders_SignsTheGetWithTheQueryString()
    {
        await using var stack = await TestStack.StartAsync();
        stack.Rest.SetResponse(BinanceWirePaths.OpenOrders, EmptyOpenOrdersFrame);

        await stack.Adapter.GetOpenOrdersAsync();

        var url = stack.Rest.LastRequest!.RequestUri!.ToString();
        url.Should().StartWith("https://stub.local/api/v3/openOrders?timestamp=");
        var query = stack.Rest.LastRequest.RequestUri!.Query.TrimStart('?');
        var signedIndex = query.LastIndexOf("&signature=", StringComparison.Ordinal);
        signedIndex.Should().BeGreaterThan(0);
        var signature = query[(signedIndex + "&signature=".Length)..];
        signature.Should().Be(HexHmacSha256(StubApiSecret, query[..signedIndex]));
    }

    [Fact]
    public async Task GetPositions_ReturnsTheHonestEmptyListWithoutAnyVenueCall()
    {
        await using var stack = await TestStack.StartAsync();

        var positions = await stack.Adapter.GetPositionsAsync();

        positions.Should().BeEmpty("the spot surface carries no position rows");
        stack.Rest.RequestCount.Should().Be(0, "the empty answer needs no venue round trip");
    }

    [Fact]
    public async Task GetBalances_QueriesTheSpotAccountAndDropsEmptyAndUnparsableRows()
    {
        await using var stack = await TestStack.StartAsync();
        stack.Rest.SetResponse(BinanceWirePaths.Account, BalancesFrame);

        var balances = await stack.Adapter.GetBalancesAsync();

        stack.Rest.LastRequest!.RequestUri!.AbsolutePath.Should().Be("/api/v3/account");
        balances.Keys.Should().BeEquivalentTo("USDT");
        var usdt = balances.Should().ContainKey("USDT").WhoseValue;
        usdt.Amount.Should().Be(10.5m);
        usdt.Currency.Should().Be("USDT");
    }

    #endregion

    #region Tests: the listen-key session

    [Fact]
    public async Task ConnectAsync_IssuesTheListenKeyAndDialsItsStream()
    {
        await using var stack = await TestStack.StartAsync();

        await stack.Adapter.ConnectAsync();

        // The listen-key issuance: the venue's own create call — the API key
        // rides the header alone, no signature, no query.
        var request = stack.Rest.LastRequest!;
        request.Method.Should().Be(HttpMethod.Post);
        request.RequestUri!.ToString().Should().Be("https://stub.local/api/v3/userDataStream");
        request.Headers.NonValidated["X-MBX-APIKEY"].Single().Should().Be(StubApiKey);

        // The private stream: one dial, connected.
        await stack.Server.WaitForConnectionAsync(1, TimeSpan.FromSeconds(30));
        stack.Adapter.IsConnected.Should().BeTrue();
        stack.Server.ConnectionCount.Should().Be(1);
    }

    [Fact]
    public async Task ConnectAsync_ListenKeyVenueFailureSurfacesAsAnError()
    {
        await using var stack = await TestStack.StartAsync();
        stack.Rest.SetResponse(BinanceWirePaths.UserDataStream, """
            {"code":-1002,"msg":"Invalid API-key, IP, or permissions for action."}
            """);

        var connect = async () => await stack.Adapter.ConnectAsync();

        var refused = await connect.Should().ThrowAsync<InvalidOperationException>();
        refused.Which.Message.Should().Contain("-1002");
        stack.Server.ConnectionCount.Should().Be(0, "a failed issuance never dials a stream");
    }

    [Fact]
    public async Task ConnectAsync_ListenKeyBodyWithoutTheMemberIsAnError()
    {
        await using var stack = await TestStack.StartAsync();
        stack.Rest.SetResponse(BinanceWirePaths.UserDataStream, """
            {"foo":"bar"}
            """);

        var connect = async () => await stack.Adapter.ConnectAsync();

        var refused = await connect.Should().ThrowAsync<InvalidOperationException>();
        refused.Which.Message.Should().Contain("no parsable listenKey");
        stack.Server.ConnectionCount.Should().Be(0, "a failed issuance never dials a stream");
    }

    [Fact]
    public async Task ConnectAsync_BlankEndpointsAreAConstructorError()
    {
        await using var server = await LoopbackWebSocketServer.StartAsync();
        var rest = new StubRecordingHandler();

        var build = () => new BinanceTradingAdapter(
            NullLogger<BinanceTradingAdapter>.Instance,
            new StubHttpClientFactory(rest),
            _marketData,
            new StubSecretStore(withCredentials: true),
            new BinanceTradingEndpoints(RestBaseUrl: " ", PrivateWsUrl: server.CombinedStreamUrl));

        FluentActions.Invoking(build)
            .Should().ThrowExactly<ArgumentException>().WithParameterName("endpoints");
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

    /// <summary>The harness-side HMAC mirror of the adapter's signing helper.</summary>
    private static string HexHmacSha256(string secret, string payload)
    {
        using var hmac = new System.Security.Cryptography.HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    /// <summary>The REST paths this suite answers (the adapter's own private constants, mirrored).</summary>
    private static class BinanceWirePaths
    {
        public const string CreateOrder = "/api/v3/order";
        public const string CancelOrder = "/api/v3/order";
        public const string OpenOrders = "/api/v3/openOrders";
        public const string Account = "/api/v3/account";
        public const string UserDataStream = "/api/v3/userDataStream";
    }

    /// <summary>The per-test stack: loopback WS (the lazily-connected private session), stub REST + secrets.</summary>
    private sealed class TestStack : IAsyncDisposable
    {
        public BinanceTradingAdapter Adapter { get; private set; } = null!;
        public StubRecordingHandler Rest { get; private set; } = null!;
        public RecordingLogger Logger { get; private set; } = null!;
        public LoopbackWebSocketServer Server { get; private set; } = null!;

        public static async Task<TestStack> StartAsync(bool withCredentials = true)
        {
            var server = await LoopbackWebSocketServer.StartAsync();
            var rest = new StubRecordingHandler();
            // The listen-key issuance is every trading fact's first venue call:
            // the canned capture answers it by default.
            rest.SetResponse(BinanceWirePaths.UserDataStream, "{\"listenKey\":\"stub-listen-key\"}");
            var credentials = new StubSecretStore(withCredentials);

            var logger = new RecordingLogger();
            var adapter = new BinanceTradingAdapter(
                logger,
                new StubHttpClientFactory(rest),
                Substitute.For<IMarketDataAdapter>(),
                credentials,
                new BinanceTradingEndpoints(
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

        public async ValueTask DisposeAsync()
        {
            await Adapter.DisposeAsync();
            await Server.DisposeAsync();
        }
    }

    /// <summary>The stub testnet secret pair (never logs, serves the two keys only).</summary>
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
            key == BinanceTradingAdapter.ApiKeySecretKey ? _apiKey
            : key == BinanceTradingAdapter.ApiSecretSecretKey ? _apiSecret
            : null);

        public Task SetAsync(string key, string value, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RemoveAsync(string key, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> ListKeysAsync(string prefix = "", CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);
    }

    /// <summary>The recording REST transport: canned results per path + the captured request bits.</summary>
    private sealed class StubRecordingHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _responses = new(StringComparer.Ordinal);
        private readonly Dictionary<string, System.Net.HttpStatusCode> _statuses = new(StringComparer.Ordinal);
        private readonly List<string> _requestPaths = [];
        private int _requestCount;

        public HttpRequestMessage? LastRequest { get; private set; }
        public string LastBody { get; private set; } = string.Empty;
        public int RequestCount => Volatile.Read(ref _requestCount);

        public void SetResponse(string path, string content) => _responses[path] = content;
        public void SetFailure(string path, System.Net.HttpStatusCode status, string content)
        {
            _responses[path] = content;
            _statuses[path] = status;
        }

        public IReadOnlyList<string> RequestsOf(string path) => _requestPaths.Where(p => p == path).ToList();

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
            _requestPaths.Add(path);
            var body = _responses.TryGetValue(path, out var canned)
                ? canned
                : "{\"code\":0,\"msg\":\"OK\",\"result\":{}}";

            var status = _statuses.TryGetValue(path, out var cannedStatus)
                ? cannedStatus
                : System.Net.HttpStatusCode.OK;

            return new HttpResponseMessage(status)
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
    private sealed class RecordingLogger : ILogger<BinanceTradingAdapter>
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
