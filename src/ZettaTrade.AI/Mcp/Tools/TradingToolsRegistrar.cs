namespace ZettaTrade.AI.Mcp.Tools;

using System.Text.Json;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// Registers all ZettaTrade MCP tools with the server host.
/// These tools are exposed to AI models for trading operations.
/// </summary>
public static class TradingToolsRegistrar
{
    /// <summary>
    /// Register all trading-related MCP tools.
    /// </summary>
    public static void RegisterAll(
        IMcpServerHost host,
        IExchangeAdapter exchange,
        ILogger logger)
    {
        RegisterPlaceOrder(host, exchange, logger);
        RegisterCancelOrder(host, exchange, logger);
        RegisterGetPositions(host, exchange, logger);
        RegisterGetBalance(host, exchange, logger);
        RegisterGetMarketData(host, exchange, logger);
    }

    private static void RegisterPlaceOrder(IMcpServerHost host, IExchangeAdapter exchange, ILogger logger)
    {
        host.RegisterTool(new McpToolDefinition
        {
            Name = "place_order",
            Description = "Place a new trading order. Supports market, limit, stop orders.",
            InputSchema = new
            {
                type = "object",
                properties = new
                {
                    symbol = new { type = "string", description = "Trading pair (e.g., BTCUSDT)" },
                    side = new { type = "string", @enum = new[] { "buy", "sell" } },
                    orderType = new { type = "string", @enum = new[] { "market", "limit", "stop_market", "stop_limit" } },
                    quantity = new { type = "number", description = "Order quantity" },
                    price = new { type = "number", description = "Limit price (required for limit orders)" },
                    stopPrice = new { type = "number", description = "Stop trigger price" },
                    timeInForce = new { type = "string", @enum = new[] { "GTC", "IOC", "FOK" } },
                },
                required = new[] { "symbol", "side", "orderType", "quantity" },
            },
            Handler = async (argsJson, ct) =>
            {
                try
                {
                    var args = JsonSerializer.Deserialize<PlaceOrderArgs>(argsJson ?? "{}");
                    if (args is null)
                        return new McpToolResult(IsError: true, Content: "Invalid arguments");

                    var request = new OrderRequest
                    {
                        Symbol = Core.ValueObjects.Symbol.Parse(args.Symbol),
                        Side = args.Side == "buy"
                            ? Core.Enums.OrderSide.Buy
                            : Core.Enums.OrderSide.Sell,
                        Type = ParseOrderType(args.OrderType),
                        Quantity = new Core.ValueObjects.Quantity(args.Quantity),
                        Price = args.Price.HasValue
                            ? new Core.ValueObjects.Price(args.Price.Value)
                            : null,
                        StopPrice = args.StopPrice.HasValue
                            ? new Core.ValueObjects.Price(args.StopPrice.Value)
                            : null,
                    };

                    var order = await exchange.PlaceOrderAsync(request, ct);
                    logger.LogInformation("MCP: Order placed — {Order}", order);

                    return new McpToolResult(
                        IsError: false,
                        Content: JsonSerializer.Serialize(new
                        {
                            orderId = order.Id,
                            status = order.Status.ToString(),
                            symbol = order.Symbol.Raw,
                            side = order.Side.ToString(),
                            quantity = order.Quantity.Value,
                            price = order.Price.Value,
                        }));
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "MCP: place_order failed");
                    return new McpToolResult(IsError: true, Content: ex.Message);
                }
            },
        });
    }

    private static void RegisterCancelOrder(IMcpServerHost host, IExchangeAdapter exchange, ILogger logger)
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
                    orderId = new { type = "string", description = "The order ID to cancel" },
                },
                required = new[] { "orderId" },
            },
            Handler = async (argsJson, ct) =>
            {
                try
                {
                    var args = JsonSerializer.Deserialize<CancelOrderArgs>(argsJson ?? "{}");
                    if (args?.OrderId is null)
                        return new McpToolResult(IsError: true, Content: "orderId is required");

                    await exchange.CancelOrderAsync(args.OrderId, ct);
                    logger.LogInformation("MCP: Order cancelled — {OrderId}", args.OrderId);

                    return new McpToolResult(IsError: false, Content: $"Order {args.OrderId} cancelled");
                }
                catch (Exception ex)
                {
                    return new McpToolResult(IsError: true, Content: ex.Message);
                }
            },
        });
    }

    private static void RegisterGetPositions(IMcpServerHost host, IExchangeAdapter exchange, ILogger logger)
    {
        host.RegisterTool(new McpToolDefinition
        {
            Name = "get_positions",
            Description = "Get all currently open positions.",
            InputSchema = new { type = "object", properties = new { } },
            Handler = async (_, ct) =>
            {
                try
                {
                    var positions = await exchange.GetPositionsAsync(ct);
                    var result = positions.Select(p => new
                    {
                        symbol = p.Symbol.Raw,
                        side = p.Side.ToString(),
                        quantity = p.Quantity.Value,
                        entryPrice = p.EntryPrice.Value,
                        currentPrice = p.CurrentPrice.Value,
                        stopLoss = p.StopLoss?.Value,
                        takeProfit = p.TakeProfit?.Value,
                    });

                    return new McpToolResult(IsError: false, Content: JsonSerializer.Serialize(result));
                }
                catch (Exception ex)
                {
                    return new McpToolResult(IsError: true, Content: ex.Message);
                }
            },
        });
    }

    private static void RegisterGetBalance(IMcpServerHost host, IExchangeAdapter exchange, ILogger logger)
    {
        host.RegisterTool(new McpToolDefinition
        {
            Name = "get_balance",
            Description = "Get account balance for all assets.",
            InputSchema = new { type = "object", properties = new { } },
            Handler = async (_, ct) =>
            {
                try
                {
                    var balances = await exchange.GetBalancesAsync(ct);
                    var result = balances.ToDictionary(
                        kv => kv.Key,
                        kv => kv.Value.Amount);

                    return new McpToolResult(IsError: false, Content: JsonSerializer.Serialize(result));
                }
                catch (Exception ex)
                {
                    return new McpToolResult(IsError: true, Content: ex.Message);
                }
            },
        });
    }

    private static void RegisterGetMarketData(IMcpServerHost host, IExchangeAdapter exchange, ILogger logger)
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
                    limit = new { type = "integer", description = "Number of candles (default: 100)" },
                },
                required = new[] { "symbol" },
            },
            Handler = async (argsJson, ct) =>
            {
                try
                {
                    var args = JsonSerializer.Deserialize<MarketDataArgs>(argsJson ?? "{}");
                    var symbol = Core.ValueObjects.Symbol.Parse(args?.Symbol ?? "BTCUSDT");
                    var tf = Enum.Parse<Core.Enums.TimeFrame>(args?.Timeframe ?? "H1");
                    var limit = args?.Limit ?? 100;

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

                    return new McpToolResult(IsError: false, Content: JsonSerializer.Serialize(result));
                }
                catch (Exception ex)
                {
                    return new McpToolResult(IsError: true, Content: ex.Message);
                }
            },
        });
    }

    private static Core.Enums.OrderType ParseOrderType(string type) => type switch
    {
        "market" => Core.Enums.OrderType.Market,
        "limit" => Core.Enums.OrderType.Limit,
        "stop_market" => Core.Enums.OrderType.StopMarket,
        "stop_limit" => Core.Enums.OrderType.StopLimit,
        _ => Core.Enums.OrderType.Market,
    };

    // Argument DTOs
    private sealed record PlaceOrderArgs(
        string Symbol, string Side, string OrderType,
        decimal Quantity, decimal? Price, decimal? StopPrice, string? TimeInForce);

    private sealed record CancelOrderArgs(string OrderId);

    private sealed record MarketDataArgs(string Symbol, string? Timeframe, int? Limit);
}
