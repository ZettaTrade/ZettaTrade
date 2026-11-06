namespace ZettaTrade.App.Tests.Mcp;

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Livery.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using ZettaTrade.AI.Mcp.Tools;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// The trading tools' argument surface: trade-critical enums (side, order
/// type) parse ONLY from a defined member, and the optional rows
/// (time-in-force, timeframe) default exactly when they are OMITTED — a
/// provided unparsable string is always an explicit error, never a silent
/// default (trading-safety rule; BUG-007, BUG-008). Every rejection happens
/// long before the exchange adapter is touched; every acceptance rides its
/// captured request to the exchange with the intended enums.
/// </summary>
public sealed class TradingToolsRegistrarTests
{
    #region Constants

    /// <summary>The exchange identity the fake adapter reports (Kraken parses "BTC/USDT" split).</summary>
    private const string ExchangeId = "kraken";

    /// <summary>The registrar's default candle limit, pinned by the tool description.</summary>
    private const int ExpectedDefaultMarketDataLimit = 100;

    /// <summary>The secret order id the fake adapter answers the placement with.</summary>
    private const string PlacedOrderId = "ord-test-1";

    #endregion

    #region Placement rejections

    /// <summary>A NUMERIC side string ("999") parses to no defined member: an explicit error, the exchange untouched.</summary>
    [Fact]
    public async Task PlaceOrder_ANumericSide_IsAnExplicitErrorAndNeverReachesTheExchange()
    {
        var kit = CreateRegistrarKit();

        var result = await kit.Host.Tools["place_order"].Handler(
            Args("{\"symbol\":\"BTC/USDT\",\"side\":\"999\",\"orderType\":\"Limit\",\"quantity\":0.5,\"price\":42000}"),
            CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Content[0].TextValue.Should().Contain("Invalid side").And.Contain("999").And.Contain("Buy, Sell");
        kit.PlacedRequest.Should().BeNull("a rejected side must never reach the exchange");
    }

    /// <summary>An UNDEFINED side name refuses exactly the same way.</summary>
    [Fact]
    public async Task PlaceOrder_AnUndefinedSideName_IsAnExplicitError()
    {
        var kit = CreateRegistrarKit();

        var result = await kit.Host.Tools["place_order"].Handler(
            Args("{\"symbol\":\"BTC/USDT\",\"side\":\"IntoTheVoid\",\"orderType\":\"Limit\",\"quantity\":0.5,\"price\":42000}"),
            CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Content[0].TextValue.Should().Contain("Invalid side").And.Contain("IntoTheVoid");
        kit.PlacedRequest.Should().BeNull();
    }

    /// <summary>A numeric order type string is also refused without an exchange contact.</summary>
    [Fact]
    public async Task PlaceOrder_ANumericOrderType_IsAnExplicitError()
    {
        var kit = CreateRegistrarKit();

        var result = await kit.Host.Tools["place_order"].Handler(
            Args("{\"symbol\":\"BTC/USDT\",\"side\":\"Buy\",\"orderType\":\"42\",\"quantity\":0.5,\"price\":42000}"),
            CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Content[0].TextValue.Should().Contain("Invalid orderType").And.Contain("42").And.Contain("Market, Limit");
        kit.PlacedRequest.Should().BeNull();
    }

    /// <summary>A PROVIDED unparsable time-in-force is an explicit error — never the silent GTC (BUG-008).</summary>
    [Fact]
    public async Task PlaceOrder_AnUnparsableTimeInForce_IsAnExplicitError()
    {
        var kit = CreateRegistrarKit();

        var result = await kit.Host.Tools["place_order"].Handler(
            Args("{\"symbol\":\"BTC/USDT\",\"side\":\"Buy\",\"orderType\":\"Limit\",\"quantity\":0.5,\"price\":42000,\"timeInForce\":\"FOQ\"}"),
            CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Content[0].TextValue.Should().Contain("Invalid timeInForce").And.Contain("FOQ").And.Contain("GTC, IOC, FOK, GTD");
        kit.PlacedRequest.Should().BeNull();
    }

    /// <summary>A GTD time-in-force without an expiry is caught by the order validation before the exchange.</summary>
    [Fact]
    public async Task PlaceOrder_ADefinedGtdWithoutExpiry_FailsTheOrderValidationExplicitly()
    {
        var kit = CreateRegistrarKit();

        var result = await kit.Host.Tools["place_order"].Handler(
            Args("{\"symbol\":\"BTC/USDT\",\"side\":\"Buy\",\"orderType\":\"Limit\",\"quantity\":0.5,\"price\":42000,\"timeInForce\":\"GTD\"}"),
            CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Content[0].TextValue.Should().Contain("Validation failed").And.Contain("Expiry");
        kit.PlacedRequest.Should().BeNull();
    }

    #endregion

    #region Placement acceptance

    /// <summary>An OMITTED time-in-force rides the request's GTC semantic to the exchange (defaults only for omission).</summary>
    [Fact]
    public async Task PlaceOrder_AnOmittedTimeInForce_PlacesTheOrderWithGtc()
    {
        var kit = CreateRegistrarKit();

        var result = await kit.Host.Tools["place_order"].Handler(LimitBuyArgs(), CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Content[0].TextValue.Should().Contain(PlacedOrderId);
        kit.PlacedRequest.Should().NotBeNull("the order rides its captured request to the exchange");
        var placedRequest = kit.PlacedRequest!;
        placedRequest.TimeInForce.Should().Be(TimeInForce.GTC);
        placedRequest.Side.Should().Be(OrderSide.Buy);
        placedRequest.Type.Should().Be(OrderType.Limit);
        // Unwrap the nullable price FIRST (identity: `!` is a flow annotation,
        // so `Price?.Value` would bind Nullable.Value — the struct, not the
        // decimal — an honest local keeps the comparison clean).
        (placedRequest.Price ?? Price.Zero).Value.Should().Be(42000m);
    }

    #endregion

    #region Market data

    /// <summary>A PROVIDED unparsable timeframe ("M2") is an explicit error — never the silent H1 (BUG-008).</summary>
    [Fact]
    public async Task GetMarketData_AnUnparsableTimeframe_IsAnExplicitErrorAndFetchesNothing()
    {
        var kit = CreateRegistrarKit();

        var result = await kit.Host.Tools["get_market_data"].Handler(
            Args("{\"symbol\":\"BTC/USDT\",\"timeframe\":\"M2\",\"limit\":10}"),
            CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Content[0].TextValue.Should().Contain("Invalid timeframe").And.Contain("M2");
        kit.BarsFetched.Should().BeFalse();
    }

    /// <summary>An OMITTED timeframe keeps the H1 semantic for the fetch.</summary>
    [Fact]
    public async Task GetMarketData_AnOmittedTimeframe_FetchesH1Bars()
    {
        var kit = CreateRegistrarKit();

        var result = await kit.Host.Tools["get_market_data"].Handler(
            Args("{\"symbol\":\"BTC/USDT\",\"limit\":25}"),
            CancellationToken.None);

        result.IsError.Should().BeFalse();
        kit.BarsFetched.Should().BeTrue();
        kit.BarsSymbol.Should().NotBeNull();
        kit.BarsSymbol!.BaseAsset.Should().Be("BTC");
        kit.BarsSymbol.QuoteAsset.Should().Be("USDT");
        kit.BarsTimeFrame.Should().Be(TimeFrame.H1);
        kit.BarsLimit.Should().Be(25);
    }

    /// <summary>A defined timeframe name ("M1") fetches exactly that timeframe; the omitted limit keeps the default.</summary>
    [Fact]
    public async Task GetMarketData_ADefinedTimeframe_FetchesThatTimeframeWithTheDefaultLimit()
    {
        var kit = CreateRegistrarKit();

        var result = await kit.Host.Tools["get_market_data"].Handler(
            Args("{\"symbol\":\"BTC/USDT\",\"timeframe\":\"M1\"}"),
            CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Content[0].TextValue.Should().Contain("Open");
        kit.BarsFetched.Should().BeTrue();
        kit.BarsTimeFrame.Should().Be(TimeFrame.M1);
        kit.BarsLimit.Should().Be(ExpectedDefaultMarketDataLimit);
    }

    #endregion

    #region Factories

    /// <summary>The canonical limit-buy request the acceptance facts drive (time-in-force omitted).</summary>
    private static JsonElement LimitBuyArgs() => Args(
        "{\"symbol\":\"BTC/USDT\",\"side\":\"Buy\",\"orderType\":\"Limit\",\"quantity\":0.5,\"price\":42000}");

    /// <summary>Parse one JSON argument object the way the host hands it to a handler.</summary>
    private static JsonElement Args(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// The registrar kit: a recording host capturing the registered handlers,
    /// a scripted exchange/risk stack capturing the traffic in its own slots,
    /// and the registrar wired over them with all five tools registered.
    /// </summary>
    private static RegistrarKit CreateRegistrarKit()
    {
        var kit = new RegistrarKit();
        var exchange = Substitute.For<ITradingAdapter>();
        exchange.Id.Returns(ExchangeId);

        // Both setups ride the SAME substitute call: the captures fill the
        // kit's slots and the stub answers with the order built from it.
        exchange
            .PlaceOrderAsync(
                Arg.Do<OrderRequest>(r => kit.PlacedRequest = r),
                Arg.Any<CancellationToken>())
            .Returns(call => PlacedOrder(call.Arg<OrderRequest>()!));
        exchange
            .GetBarsAsync(
                Arg.Do<Symbol>(s => { kit.BarsSymbol = s; kit.BarsFetched = true; }),
                Arg.Do<TimeFrame>(t => kit.BarsTimeFrame = t),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Do<int>(l => kit.BarsLimit = l),
                Arg.Any<CancellationToken>())
            .Returns([
                new Bar(DateTimeOffset.UtcNow.AddHours(-1), 42000m, 43000m, 41000m, 42500m, 1.5m),
            ]);

        var riskManager = Substitute.For<IRiskManager>();
        riskManager
            .CheckOrderAsync(Arg.Any<OrderRequest>(), Arg.Any<CancellationToken>())
            .Returns(RiskCheckResult.Approved());

        var host = new RecordingHost();
        kit.Registrar = new TradingToolsRegistrar(NullLogger<TradingToolsRegistrar>.Instance);
        kit.Registrar.RegisterAll(
            host,
            exchange,
            riskManager,
            Substitute.For<IConfirmationService>(),
            Substitute.For<ILocalizationManager>());

        kit.Host = host;
        kit.Exchange = exchange;
        return kit;
    }

    /// <summary>The order the fake adapter answers a placement with (echoing the captured request).</summary>
    private static Order PlacedOrder(OrderRequest request) => new()
    {
        Id = PlacedOrderId,
        ExchangeId = ExchangeId,
        Symbol = request.Symbol,
        Side = request.Side,
        Type = request.Type,
        Quantity = request.Quantity,
        Price = request.Price ?? new Price(42000m),
        Status = OrderStatus.Filled,
        TimeInForce = request.TimeInForce,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    #endregion

    /// <summary>The kit's captured slots: the assertions judge what truly reached the adapters.</summary>
    private sealed class RegistrarKit
    {
        /// <summary>The recording host the tools registered into.</summary>
        public RecordingHost Host { get; set; } = null!;

        /// <summary>The fake trading adapter.</summary>
        public ITradingAdapter Exchange { get; set; } = null!;

        /// <summary>The registrar instance (exposed for structure honesty).</summary>
        public TradingToolsRegistrar Registrar { get; set; } = null!;

        /// <summary>The OrderRequest the exchange saw, or null when nothing was placed.</summary>
        public OrderRequest? PlacedRequest { get; set; }

        /// <summary>Whether ANY GetBarsAsync call went through.</summary>
        public bool BarsFetched { get; set; }

        /// <summary>The symbol the bar fetch asked for.</summary>
        public Symbol? BarsSymbol { get; set; }

        /// <summary>The timeframe the bar fetch asked for.</summary>
        public TimeFrame BarsTimeFrame { get; set; }

        /// <summary>The limit the bar fetch asked for.</summary>
        public int BarsLimit { get; set; }
    }

    /// <summary>
    /// An IMcpServerHost stand-in that only records the registered tools in
    /// arrival order: the registrar suite drives the captured handlers
    /// directly (the real host's dispatch has its own suites).
    /// </summary>
    private sealed class RecordingHost : IMcpServerHost
    {
        /// <summary>The registered tools by name.</summary>
        public Dictionary<string, McpToolDefinition> Tools { get; } = new(StringComparer.Ordinal);

        /// <inheritdoc/>
        public bool IsRunning => false;

        /// <inheritdoc/>
        public string? Address => null;

        /// <inheritdoc/>
        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;

        /// <inheritdoc/>
        public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;

        /// <inheritdoc/>
        public void RegisterTool(McpToolDefinition tool) => Tools[tool.Name] = tool;

        /// <inheritdoc/>
        public void RegisterResource(McpResourceDefinition resource)
        {
        }

        /// <inheritdoc/>
        public void RegisterPrompt(McpPromptDefinition prompt)
        {
        }
    }
}

