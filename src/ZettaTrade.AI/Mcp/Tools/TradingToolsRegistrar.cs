namespace ZettaTrade.AI.Mcp.Tools;

using System.Text.Json;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.Core.Enums;

/// <summary>
/// Registers all ZettaTrade MCP tools with the server host.
/// Includes safety checks and confirmation requirements for trading operations.
/// </summary>
public static class TradingToolsRegistrar
{
    /// <summary>
    /// Register all trading-related MCP tools.
    /// </summary>
    public static void RegisterAll(
        IMcpServerHost host,
        ITradingAdapter exchange,
        IRiskManager riskManager,
        IConfirmationService confirmationService,
        ILogger logger)
    {
        RegisterPlaceOrder(host, exchange, riskManager, confirmationService, logger);
        RegisterCancelOrder(host, exchange, logger);
        RegisterGetPositions(host, exchange, logger);
        RegisterGetBalance(host, exchange, logger);
        RegisterGetMarketData(host, exchange, logger);
    }

    private static void RegisterPlaceOrder(
        IMcpServerHost host, 
        ITradingAdapter exchange,
        IRiskManager riskManager,
        IConfirmationService confirmationService,
        ILogger logger)
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
                        return new McpToolResult(IsError: true, Content: new[] { McpContent.Text("Arguments required") });

                    var args = argsJson.Value.Deserialize<PlaceOrderArgs>();
                    if (args is null)
                        return new McpToolResult(IsError: true, Content: new[] { McpContent.Text("Invalid arguments") });

                    // Validate symbol - don't default to BTCUSDT
                    if (string.IsNullOrWhiteSpace(args.Symbol))
                        return new McpToolResult(IsError: true, Content: new[] { McpContent.Text("Symbol is required") });

                    if (!Symbol.TryParseExchangeFormat(args.Symbol, exchange.Id, out var symbol) || symbol is null)
                        return new McpToolResult(IsError: true, Content: new[] { McpContent.Text($"Invalid symbol format: {args.Symbol}") });

                    // Validate side - don't default to Sell
                    if (!Enum.TryParse<OrderSide>(args.Side, ignoreCase: true, out var side))
                        return new McpToolResult(IsError: true, Content: new[] { McpContent.Text($"Invalid side: {args.Side}. Must be 'Buy' or 'Sell'") });

                    // Validate order type - don't default to Market
                    if (!Enum.TryParse<OrderType>(args.OrderType, ignoreCase: true, out var orderType))
                        return new McpToolResult(IsError: true, Content: new[] { McpContent.Text($"Invalid orderType: {args.OrderType}. Must be 'Market', 'Limit', 'StopMarket', or 'StopLimit'") });

                    var request = new OrderRequest
                    {
                        Symbol = symbol,
                        Side = side,
                        Type = orderType,
                        Quantity = new Quantity(args.Quantity),
                        Price = args.Price.HasValue ? new Price(args.Price.Value) : null,
                        StopPrice = args.StopPrice.HasValue ? new Price(args.StopPrice.Value) : null,
                        TimeInForce = Enum.TryParse<TimeInForce>(args.TimeInForce, true, out var tif) ? tif : TimeInForce.GTC,
                        Expiry = args.Expiry,
                    };

                    // Validate order request
                    try
                    {
                        request.Validate();
                    }
                    catch (InvalidOperationException ex)
                    {
                        return new McpToolResult(IsError: true, Content: new[] { McpContent.Text($"Validation failed: {ex.Message}") });
                    }

                    // Risk check
                    var riskCheck = await riskManager.CheckOrderAsync(request, ct);
                    if (!riskCheck.IsApproved)
                    {
                        return new McpToolResult(IsError: true, Content: new[] { McpContent.Text($"Risk check failed: {riskCheck.Reason}") });
                    }

                    // Require confirmation for high-risk orders
                    if (riskCheck.RequiresConfirmation)
                    {
                        var confirmed = await confirmationService.RequestConfirmationAsync(
                            $"Place {side} {orderType} order for {args.Quantity} {symbol.Normalized} at {args.Price?.ToString() ?? "market"}?",
                            ct);

                        if (!confirmed)
                        {
                            return new McpToolResult(IsError: true, Content: new[] { McpContent.Text("Order cancelled by user") });
                        }
                    }

                    var order = await exchange.PlaceOrderAsync(request, ct);
                    logger.LogInformation("MCP: Order placed — {Order}", order);

                    return new McpToolResult(
                        IsError: false,
                        Content: new[] { McpContent.Text(JsonSerializer.Serialize(new
                        {
                            orderId = order.Id,
                            status = order.Status.ToString(),
                            symbol = order.Symbol.Normalized,
                            side = order.Side.ToString(),
                            quantity = order.Quantity.Value,
                            price = order.Price.Value,
                        })) });
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "MCP: place_order failed");
                    return new McpToolResult(IsError: true, Content: new[] { McpContent.Text(ex.Message) });
                }
            },
        });
    }

    private static void RegisterCancelOrder(IMcpServerHost host, ITradingAdapter exchange, ILogger logger)
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
                        return new McpToolResult(IsError: true, Content: new[] { McpContent.Text("Arguments required") });

                    var args = argsJson.Value.Deserialize<CancelOrderArgs>();
                    if (args?.OrderId is null || string.IsNullOrWhiteSpace(args.Symbol))
                        return new McpToolResult(IsError: true, Content: new[] { McpContent.Text("orderId and symbol are required") });

                    if (!Symbol.TryParseExchangeFormat(args.Symbol, exchange.Id, out var symbol) || symbol is null)
                        return new McpToolResult(IsError: true, Content: new[] { McpContent.Text($"Invalid symbol format: {args.Symbol}") });

                    await exchange.CancelOrderAsync(symbol, args.OrderId, ct);
                    logger.LogInformation("MCP: Order cancelled — {OrderId}", args.OrderId);

                    return new McpToolResult(IsError: false, Content: new[] { McpContent.Text($"Order {args.OrderId} cancelled") });
                }
                catch (Exception ex)
                {
                    return new McpToolResult(IsError: true, Content: new[] { McpContent.Text(ex.Message) });
                }
            },
        });
    }

    private static void RegisterGetPositions(IMcpServerHost host, ITradingAdapter exchange, ILogger logger)
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

                    return new McpToolResult(IsError: false, Content: new[] { McpContent.Text(JsonSerializer.Serialize(result)) });
                }
                catch (Exception ex)
                {
                    return new McpToolResult(IsError: true, Content: new[] { McpContent.Text(ex.Message) });
                }
            },
        });
    }

    private static void RegisterGetBalance(IMcpServerHost host, ITradingAdapter exchange, ILogger logger)
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

                    return new McpToolResult(IsError: false, Content: new[] { McpContent.Text(JsonSerializer.Serialize(result)) });
                }
                catch (Exception ex)
                {
                    return new McpToolResult(IsError: true, Content: new[] { McpContent.Text(ex.Message) });
                }
            },
        });
    }

    private static void RegisterGetMarketData(IMcpServerHost host, ITradingAdapter exchange, ILogger logger)
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
                    limit = new { type = "integer", description = "Number of candles (default: 100)" }
                },
                required = new[] { "symbol" }
            }.ToJsonElement(),
            Handler = async (argsJson, ct) =>
            {
                try
                {
                    if (argsJson is null)
                        return new McpToolResult(IsError: true, Content: new[] { McpContent.Text("Arguments required") });

                    var args = argsJson.Value.Deserialize<MarketDataArgs>();
                    if (string.IsNullOrWhiteSpace(args?.Symbol))
                        return new McpToolResult(IsError: true, Content: new[] { McpContent.Text("Symbol is required") });

                    if (!Symbol.TryParseExchangeFormat(args.Symbol, exchange.Id, out var symbol) || symbol is null)
                        return new McpToolResult(IsError: true, Content: new[] { McpContent.Text($"Invalid symbol format: {args.Symbol}") });

                    var tf = Enum.TryParse<TimeFrame>(args.Timeframe, true, out var timeFrame) ? timeFrame : TimeFrame.H1;
                    var limit = args.Limit ?? 100;

                    var bars = await exchange.GetBarsAsync(
                        symbol, tf,
                        DateTimeOffset.UtcNow.AddDays(-7),
                        limit: limit,
                        ct: ct);

                    var result = bars.Select(b => new
                    {
                        time = b.Time.ToString("O"),
                        b.Open, b.High, b.Low, b.Close, b.Volume,
                    });

                    return new McpToolResult(IsError: false, Content: new[] { McpContent.Text(JsonSerializer.Serialize(result)) });
                }
                catch (Exception ex)
                {
                    return new McpToolResult(IsError: true, Content: new[] { McpContent.Text(ex.Message) });
                }
            },
        });
    }

    // Argument DTOs
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
}
