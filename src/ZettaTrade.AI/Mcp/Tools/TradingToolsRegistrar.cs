namespace ZettaTrade.AI.Mcp.Tools;

using System.Globalization;
using System.Text.Json;
using Livery.Localization;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// Registers all ZettaTrade MCP tools with the server host.
/// Includes safety checks and confirmation requirements for trading operations.
/// All messages returned to the AI are English by design (model-facing);
/// the human-visible confirmation dialog is localized via <see cref="ILocalizationManager"/>.
/// </summary>
public sealed class TradingToolsRegistrar(ILogger<TradingToolsRegistrar> logger)
{
    #region Constants

    /// <summary>Prefix of resx keys holding localized order side/type names ("Order.Buy", ...).</summary>
    private const string OrderNameKeyPrefix = "Order.";

    /// <summary>Resx key of the confirmation template for placing an order.</summary>
    private const string ConfirmPlaceOrderKey = "Trading.ConfirmPlaceOrderFormat";

    /// <summary>Resx key shown instead of an explicit price for market orders.</summary>
    private const string MarketPriceKey = "Trading.PriceMarket";

    /// <summary>Default candle limit for the get_market_data tool.</summary>
    private const int DefaultMarketDataLimit = 100;

    /// <summary>Historical window for the get_market_data tool, in days.</summary>
    private const int MarketDataLookbackDays = 7;

    /// <summary>
    /// The tool args' deserialization to the model's keys: the schema's keys
    /// are camelCase, so the binding rides the camel-naming policy with
    /// case-insensitive member matching — without it, the same schema-shaped
    /// arguments came back null and the tool answered "Symbol is required"
    /// for the simplest honest call.
    /// </summary>
    private static readonly JsonSerializerOptions ToolArgsOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    #endregion

    #region Fields

    private readonly ILogger<TradingToolsRegistrar> _logger = logger;

    #endregion

    #region Public Methods

    /// <summary>
    /// Register all trading-related MCP tools.
    /// </summary>
    public void RegisterAll(
        IMcpServerHost host,
        ITradingAdapter exchange,
        IRiskManager riskManager,
        IConfirmationService confirmationService,
        ILocalizationManager localization)
    {
        RegisterPlaceOrder(host, exchange, riskManager, confirmationService, localization);
        RegisterCancelOrder(host, exchange);
        RegisterGetPositions(host, exchange);
        RegisterGetBalance(host, exchange);
        RegisterGetMarketData(host, exchange);
    }

    #endregion

    #region Tool Registration

    private void RegisterPlaceOrder(
        IMcpServerHost host,
        ITradingAdapter exchange,
        IRiskManager riskManager,
        IConfirmationService confirmationService,
        ILocalizationManager localization)
    {
        host.RegisterTool(new McpToolDefinition
        {
            Name = "place_order",
            Description = "Place a new trading order. Requires human confirmation for orders above risk threshold.",
            InputSchema = new
            {
                type = "object",
                properties = new
                {
                    symbol = new { type = "string", description = "Trading pair (e.g., BTC/USDT)" },
                    side = new { type = "string", @enum = new[] { "Buy", "Sell" } },
                    orderType = new { type = "string", @enum = new[] { "Market", "Limit", "StopMarket", "StopLimit" } },
                    quantity = new { type = "number", description = "Order quantity" },
                    price = new { type = "number", description = "Limit price (required for limit orders)" },
                    stopPrice = new { type = "number", description = "Stop trigger price" },
                    timeInForce = new { type = "string", @enum = new[] { "GTC", "IOC", "FOK", "GTD" } },
                    expiry = new { type = "string", format = "date-time", description = "Expiry for GTD orders" }
                },
                required = new[] { "symbol", "side", "orderType", "quantity" }
            }.ToJsonElement(),
            Handler = async (argsJson, ct) =>
            {
                try
                {
                    if (argsJson is null)
                        return new McpToolResult(IsError: true, Content: [McpContent.CreateText("Arguments required")]);

                    var args = argsJson.Value.Deserialize<PlaceOrderArgs>(ToolArgsOptions);
                    if (args is null)
                        return new McpToolResult(IsError: true, Content: [McpContent.CreateText("Invalid arguments")]);

                    // Don't default to BTCUSDT - require explicit symbol
                    if (string.IsNullOrWhiteSpace(args.Symbol))
                        return new McpToolResult(IsError: true, Content: [McpContent.CreateText("Symbol is required")]);

                    if (!Symbol.TryParseExchangeFormat(args.Symbol, exchange.Id, out var symbol) || symbol is null)
                        return new McpToolResult(IsError: true, Content: [McpContent.CreateText($"Invalid symbol format: {args.Symbol}")]);

                    // Trade-critical enums: only a defined member passes (a
                    // numeric or undefined string is an explicit error, never
                    // a definition ride-through; trading-safety rule).
                    if (!ToolEnumParser.TryParseDefined(args.Side, out OrderSide side))
                        return new McpToolResult(IsError: true, Content: [McpContent.CreateText($"Invalid side: {args.Side}. Must be one of: {ToolEnumParser.AllowedNames<OrderSide>()}")]);

                    if (!ToolEnumParser.TryParseDefined(args.OrderType, out OrderType orderType))
                        return new McpToolResult(IsError: true, Content: [McpContent.CreateText($"Invalid orderType: {args.OrderType}. Must be one of: {ToolEnumParser.AllowedNames<OrderType>()}")]);

                    // timeInForce is optional (an omitted row keeps the order
                    // request's GTC semantic), but a PROVIDED unparsable value
                    // is an explicit error - never a silent default (BUG-008).
                    TimeInForce timeInForce = TimeInForce.GTC;
                    if (args.TimeInForce is not null && !ToolEnumParser.TryParseDefined(args.TimeInForce, out timeInForce))
                        return new McpToolResult(IsError: true, Content: [McpContent.CreateText($"Invalid timeInForce: {args.TimeInForce}. Must be one of: {ToolEnumParser.AllowedNames<TimeInForce>()}")]);

                    var request = new OrderRequest
                    {
                        Symbol = symbol,
                        Side = side,
                        Type = orderType,
                        Quantity = new Quantity(args.Quantity),
                        Price = args.Price.HasValue ? new Price(args.Price.Value) : null,
                        StopPrice = args.StopPrice.HasValue ? new Price(args.StopPrice.Value) : null,
                        TimeInForce = timeInForce,
                        Expiry = args.Expiry,
                    };

                    try
                    {
                        request.Validate();
                    }
                    catch (InvalidOperationException ex)
                    {
                        return new McpToolResult(IsError: true, Content: [McpContent.CreateText($"Validation failed: {ex.Message}")]);
                    }

                    var riskCheck = await riskManager.CheckOrderAsync(request, ct);
                    if (!riskCheck.IsApproved)
                    {
                        return new McpToolResult(IsError: true, Content: [McpContent.CreateText($"Risk check failed: {riskCheck.Reason}")]);
                    }

                    if (riskCheck.RequiresConfirmation)
                    {
                        var confirmed = await confirmationService.RequestConfirmationAsync(
                            BuildPlaceOrderConfirmation(localization, side, orderType, args, symbol),
                            ct);

                        if (!confirmed)
                        {
                            return new McpToolResult(IsError: true, Content: [McpContent.CreateText("Order cancelled by user")]);
                        }
                    }

                    var order = await exchange.PlaceOrderAsync(request, ct);
                    _logger.LogInformation("MCP: Order placed — {Order}", order);

                    return new McpToolResult(
                        IsError: false,
                        Content: [McpContent.CreateText(JsonSerializer.Serialize(new
                        {
                            orderId = order.Id,
                            status = order.Status.ToString(),
                            symbol = order.Symbol.Normalized,
                            side = order.Side.ToString(),
                            quantity = order.Quantity.Value,
                            price = order.Price.Value,
                        }))]);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "MCP: place_order failed");
                    return new McpToolResult(IsError: true, Content: [McpContent.CreateText(ex.Message)]);
                }
            },
        });
    }

    /// <summary>
    /// Compose a structured, localized-friendly confirmation request: the UI resolves the
    /// template in the current language; numbers are pre-formatted with InvariantCulture.
    /// </summary>
    private static ConfirmationRequest BuildPlaceOrderConfirmation(
        ILocalizationManager localization,
        OrderSide side,
        OrderType orderType,
        PlaceOrderArgs args,
        Symbol symbol)
    {
        var sideText = localization.GetString($"{OrderNameKeyPrefix}{side}");
        var typeText = localization.GetString($"{OrderNameKeyPrefix}{orderType}");
        var priceText = args.Price.HasValue
            ? args.Price.Value.ToString(CultureInfo.InvariantCulture)
            : localization.GetString(MarketPriceKey);

        return new ConfirmationRequest
        {
            MessageKey = ConfirmPlaceOrderKey,
            Arguments =
            [
                sideText,
                typeText,
                args.Quantity.ToString(CultureInfo.InvariantCulture),
                symbol.Normalized,
                priceText,
            ],
        };
    }

    private void RegisterCancelOrder(IMcpServerHost host, ITradingAdapter exchange)
    {
        host.RegisterTool(new McpToolDefinition
        {
            Name = "cancel_order",
            Description = "Cancel a pending order by its ID.",
            InputSchema = new
            {
                type = "object",
                properties = new
                {
                    symbol = new { type = "string", description = "Trading pair (e.g., BTC/USDT)" },
                    orderId = new { type = "string", description = "The order ID to cancel" }
                },
                required = new[] { "symbol", "orderId" }
            }.ToJsonElement(),
            Handler = async (argsJson, ct) =>
            {
                try
                {
                    if (argsJson is null)
                        return new McpToolResult(IsError: true, Content: [McpContent.CreateText("Arguments required")]);

                    var args = argsJson.Value.Deserialize<CancelOrderArgs>(ToolArgsOptions);
                    if (args?.OrderId is null || string.IsNullOrWhiteSpace(args.Symbol))
                        return new McpToolResult(IsError: true, Content: [McpContent.CreateText("orderId and symbol are required")]);

                    if (!Symbol.TryParseExchangeFormat(args.Symbol, exchange.Id, out var symbol) || symbol is null)
                        return new McpToolResult(IsError: true, Content: [McpContent.CreateText($"Invalid symbol format: {args.Symbol}")]);

                    await exchange.CancelOrderAsync(symbol, args.OrderId, ct);
                    _logger.LogInformation("MCP: Order cancelled — {OrderId}", args.OrderId);

                    return new McpToolResult(IsError: false, Content: [McpContent.CreateText($"Order {args.OrderId} cancelled")]);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "MCP: cancel_order failed");
                    return new McpToolResult(IsError: true, Content: [McpContent.CreateText(ex.Message)]);
                }
            },
        });
    }

    private void RegisterGetPositions(IMcpServerHost host, ITradingAdapter exchange)
    {
        host.RegisterTool(new McpToolDefinition
        {
            Name = "get_positions",
            Description = "Get all currently open positions.",
            InputSchema = new { type = "object", properties = new { } }.ToJsonElement(),
            Handler = async (_, ct) =>
            {
                try
                {
                    var positions = await exchange.GetPositionsAsync(ct);
                    var result = positions.Select(p => new
                    {
                        symbol = p.Symbol.Normalized,
                        side = p.Side.ToString(),
                        quantity = p.Quantity.Value,
                        entryPrice = p.EntryPrice.Value,
                        currentPrice = p.CurrentPrice.Value,
                        stopLoss = p.StopLoss?.Value,
                        takeProfit = p.TakeProfit?.Value,
                    });

                    return new McpToolResult(IsError: false, Content: [McpContent.CreateText(JsonSerializer.Serialize(result))]);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "MCP: get_positions failed");
                    return new McpToolResult(IsError: true, Content: [McpContent.CreateText(ex.Message)]);
                }
            },
        });
    }

    private void RegisterGetBalance(IMcpServerHost host, ITradingAdapter exchange)
    {
        host.RegisterTool(new McpToolDefinition
        {
            Name = "get_balance",
            Description = "Get account balance for all assets.",
            InputSchema = new { type = "object", properties = new { } }.ToJsonElement(),
            Handler = async (_, ct) =>
            {
                try
                {
                    var balances = await exchange.GetBalancesAsync(ct);
                    var result = balances.ToDictionary(
                        kv => kv.Key,
                        kv => kv.Value.Amount);

                    return new McpToolResult(IsError: false, Content: [McpContent.CreateText(JsonSerializer.Serialize(result))]);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "MCP: get_balance failed");
                    return new McpToolResult(IsError: true, Content: [McpContent.CreateText(ex.Message)]);
                }
            },
        });
    }

    private void RegisterGetMarketData(IMcpServerHost host, ITradingAdapter exchange)
    {
        host.RegisterTool(new McpToolDefinition
        {
            Name = "get_market_data",
            Description = "Get historical OHLCV candlestick data for a symbol.",
            InputSchema = new
            {
                type = "object",
                properties = new
                {
                    symbol = new { type = "string" },
                    timeframe = new { type = "string", @enum = new[] { "M1", "M5", "M15", "H1", "H4", "D1" } },
                    limit = new { type = "integer", description = $"Number of candles (default: {DefaultMarketDataLimit})" }
                },
                required = new[] { "symbol" }
            }.ToJsonElement(),
            Handler = async (argsJson, ct) =>
            {
                try
                {
                    if (argsJson is null)
                        return new McpToolResult(IsError: true, Content: [McpContent.CreateText("Arguments required")]);

                    var args = argsJson.Value.Deserialize<MarketDataArgs>(ToolArgsOptions);
                    if (string.IsNullOrWhiteSpace(args?.Symbol))
                        return new McpToolResult(IsError: true, Content: [McpContent.CreateText("Symbol is required")]);

                    if (!Symbol.TryParseExchangeFormat(args.Symbol, exchange.Id, out var symbol) || symbol is null)
                        return new McpToolResult(IsError: true, Content: [McpContent.CreateText($"Invalid symbol format: {args.Symbol}")]);

                    // timeframe is optional (an omitted row keeps the H1
                    // semantic), but a PROVIDED unparsable value is an
                    // explicit error - never a silent default (BUG-008).
                    TimeFrame timeFrame = TimeFrame.H1;
                    if (args.Timeframe is not null && !ToolEnumParser.TryParseDefined(args.Timeframe, out timeFrame))
                        return new McpToolResult(IsError: true, Content: [McpContent.CreateText($"Invalid timeframe: {args.Timeframe}. Must be one of: {ToolEnumParser.AllowedNames<TimeFrame>()}")]);

                    var limit = args.Limit ?? DefaultMarketDataLimit;

                    var bars = await exchange.GetBarsAsync(
                        symbol,
                        timeFrame,
                        DateTimeOffset.UtcNow.AddDays(-MarketDataLookbackDays),
                        limit: limit,
                        ct: ct);

                    var result = bars.Select(b => new
                    {
                        time = b.Time.ToString("O"),
                        b.Open,
                        b.High,
                        b.Low,
                        b.Close,
                        b.Volume,
                    });

                    return new McpToolResult(IsError: false, Content: [McpContent.CreateText(JsonSerializer.Serialize(result))]);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "MCP: get_market_data failed");
                    return new McpToolResult(IsError: true, Content: [McpContent.CreateText(ex.Message)]);
                }
            },
        });
    }

    #endregion

    #region Tool Arguments

    private sealed record PlaceOrderArgs(
        string? Symbol,
        string? Side,
        string? OrderType,
        decimal Quantity,
        decimal? Price,
        decimal? StopPrice,
        string? TimeInForce,
        DateTimeOffset? Expiry);

    private sealed record CancelOrderArgs(string? Symbol, string? OrderId);

    private sealed record MarketDataArgs(string? Symbol, string? Timeframe, int? Limit);

    #endregion
}

