namespace ZettaTrade.App.Services;

using System.Globalization;
using System.Text.Json;
using Livery.Localization;
using Microsoft.Extensions.Logging;
using ZettaTrade.AI.Mcp;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Infrastructure.Persistence;

/// <summary>
/// Registers the workspace-facing MCP surface (T-208) with the server host:
/// the read-only resources (the portfolio snapshot, the workspace state),
/// the prompt templates (the market brief, the portfolio review) and the
/// workspace tools (order history, the enabled indicator setups, the trade
/// statistics, and the price-alert place/cancel pair). All messages returned
/// to the AI are English by design (model-facing); the human-visible
/// confirmation dialog is localized via <see cref="ILocalizationManager"/>.
/// <para>
/// The trading-adjacent gates hold: an AI-authored alert reaches the roster
/// only through the human confirmation (the same gate an MCP order rides),
/// while a cancellation removes without one — removal is the explicit
/// forgetting, the same posture the alert manager's own delete keeps. The
/// read tools never journal (a read creates no fill — the T-133 discipline
/// governs fills only) and every bounded read states its bound.
/// </para>
/// </summary>
public sealed class McpWorkspaceToolsRegistrar(ILogger<McpWorkspaceToolsRegistrar> logger)
{
    #region Constants

    /// <summary>The portfolio resource's URI (read-only balances + positions).</summary>
    public const string PortfolioResourceUri = "zettatrade://portfolio";

    /// <summary>The workspace resource's URI (read-only workspace state).</summary>
    public const string WorkspaceResourceUri = "zettatrade://workspace";

    /// <summary>The market-brief prompt's name.</summary>
    public const string MarketBriefPromptName = "market_brief";

    /// <summary>The portfolio-review prompt's name.</summary>
    public const string PortfolioReviewPromptName = "portfolio_review";

    /// <summary>Default recent-order row count for the get_order_history tool.</summary>
    public const int DefaultOrderHistoryLimit = 20;

    /// <summary>The get_order_history limit's ceiling (a bounded read states its bound).</summary>
    public const int MaxOrderHistoryLimit = 200;

    /// <summary>The stats scan's page size: the trade-statistics panel's own page (2000 rows).</summary>
    public const int StatsScanPageSize = 2000;

    /// <summary>The stats scan's page ceiling: twenty pages bound the tool's
    /// answer time (the panel itself scans unbounded with progressive
    /// publishes; a tool call must answer, so it caps and discloses).</summary>
    public const int MaxStatsScanPages = 20;

    /// <summary>Resx key of the confirmation template for placing an alert.</summary>
    private const string ConfirmPlaceAlertKey = "Trading.ConfirmPlaceAlertFormat";

    /// <summary>
    /// The tool args' deserialization to the model's keys: the schema's keys
    /// are camelCase, so the binding rides the camel-naming policy with
    /// case-insensitive member matching (the TradingToolsRegistrar's shape).
    /// </summary>
    private static readonly JsonSerializerOptions ToolArgsOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    #endregion

    #region Registration

    /// <summary>
    /// Register all workspace-facing MCP resources, prompts and tools.
    /// </summary>
    /// <param name="host">The server host the surface registers with.</param>
    /// <param name="tradingSurface">
    /// The trading surface the portfolio facts ride (the same journaling
    /// wrapper the trading tools execute against, so every consumer of the
    /// MCP surface sees one venue).
    /// </param>
    /// <param name="alerts">The alert engine (the roster's sole owner).</param>
    /// <param name="chatContext">The workspace context service (the snapshot source).</param>
    /// <param name="orderHistory">The persistent order journal (reads only).</param>
    /// <param name="orderStats">The trade-statistics read query (reads only).</param>
    /// <param name="confirmations">The human-confirmation gate the alert placements ride.</param>
    /// <param name="localization">The localization the confirmation template resolves through.</param>
    public void RegisterAll(
        IMcpServerHost host,
        ITradingAdapter tradingSurface,
        IAlertEngine alerts,
        ChatContextService chatContext,
        IOrderHistoryStore orderHistory,
        IOrderStatsQuery orderStats,
        IConfirmationService confirmations,
        ILocalizationManager localization)
    {
        RegisterPortfolioResource(host, tradingSurface);
        RegisterWorkspaceResource(host, chatContext);
        RegisterMarketBriefPrompt(host, chatContext);
        RegisterPortfolioReviewPrompt(host, tradingSurface);
        RegisterGetOrderHistory(host, orderHistory);
        RegisterGetIndicators(host, chatContext);
        RegisterGetTradeStats(host, orderStats);
        RegisterPlaceAlert(host, alerts, confirmations, localization);
        RegisterCancelAlert(host, alerts);
    }

    #endregion Registration

    #region Resources

    private void RegisterPortfolioResource(IMcpServerHost host, ITradingAdapter tradingSurface)
    {
        host.RegisterResource(new McpResourceDefinition
        {
            Uri = PortfolioResourceUri,
            Name = "Portfolio snapshot (read-only)",
            MimeType = "application/json",
            Reader = async ct =>
            {
                var balances = await tradingSurface.GetBalancesAsync(ct);
                var positions = await tradingSurface.GetPositionsAsync(ct);

                var snapshot = new
                {
                    exchangeId = tradingSurface.Id,
                    generatedAtUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    balances = balances.Select(kv => new { asset = kv.Key, amount = kv.Value.Amount }),
                    positions = positions.Select(p => new
                    {
                        id = p.Id,
                        exchangeId = p.ExchangeId,
                        symbol = p.Symbol.Normalized,
                        side = p.Side.ToString(),
                        quantity = p.Quantity.Value,
                        entryPrice = p.EntryPrice.Value,
                        currentPrice = p.CurrentPrice.Value,
                        openedAtUtc = p.OpenedAt.ToString("O", CultureInfo.InvariantCulture),
                        stopLoss = p.StopLoss?.Value,
                        takeProfit = p.TakeProfit?.Value,
                    }),
                };

                logger.LogDebug("MCP resource read: the portfolio snapshot ({Balances} balances, {Positions} positions)",
                    balances.Count, positions.Count);
                return [McpContent.CreateText(JsonSerializer.Serialize(snapshot))];
            },
        });
    }

    private void RegisterWorkspaceResource(IMcpServerHost host, ChatContextService chatContext)
    {
        host.RegisterResource(new McpResourceDefinition
        {
            Uri = WorkspaceResourceUri,
            Name = "Workspace state (read-only)",
            MimeType = "application/json",
            Reader = ct =>
            {
                var snapshot = WorkspaceSnapshotJson(chatContext);
                logger.LogDebug("MCP resource read: the workspace state");
                return Task.FromResult<IReadOnlyList<McpContent>>([McpContent.CreateText(snapshot)]);
            },
        });
    }

    #endregion Resources

    #region Prompts

    private void RegisterMarketBriefPrompt(IMcpServerHost host, ChatContextService chatContext)
    {
        host.RegisterPrompt(new McpPromptDefinition
        {
            Name = MarketBriefPromptName,
            Description = "A market brief for the workspace's currently open market, composed from the live snapshot.",
            Arguments = [],
            Handler = (_, ct) =>
            {
                var snapshot = WorkspaceSnapshotJson(chatContext);
                var brief =
                    "Produce a concise market brief for the trader. Work only from the facts below; " +
                    "a fact that is absent stays absent - say so instead of inventing it. " +
                    "End with one sentence of practical outlook; never an instruction to trade.\n\n" +
                    "Workspace facts (JSON):\n" + snapshot;
                return Task.FromResult(brief);
            },
        });
    }

    private void RegisterPortfolioReviewPrompt(IMcpServerHost host, ITradingAdapter tradingSurface)
    {
        host.RegisterPrompt(new McpPromptDefinition
        {
            Name = PortfolioReviewPromptName,
            Description = "A portfolio review prompt carrying the current balances and open positions.",
            Arguments = [],
            Handler = async (_, ct) =>
            {
                var snapshot = await PortfolioSnapshotJsonAsync(tradingSurface, ct);
                var review =
                    "Review the trader's portfolio from the snapshot below. Report: the exposure per asset, " +
                    "the concentration risks, and anything that looks anomalous. Work only from the facts below; " +
                    "a fact that is absent stays absent - say so instead of inventing it. " +
                    "Never an instruction to trade.\n\n" +
                    "Portfolio snapshot (JSON):\n" + snapshot;
                return review;
            },
        });
    }

    #endregion Prompts

    #region Tool Registration

    private void RegisterGetOrderHistory(IMcpServerHost host, IOrderHistoryStore orderHistory)
    {
        host.RegisterTool(new McpToolDefinition
        {
            Name = "get_order_history",
            Description =
                $"Get the most recent orders from the persistent journal, newest first. " +
                $"The optional limit defaults to {DefaultOrderHistoryLimit} and is clamped to 1..{MaxOrderHistoryLimit}.",
            InputSchema = new
            {
                type = "object",
                properties = new
                {
                    limit = new { type = "integer", description = $"Row count (default: {DefaultOrderHistoryLimit}, max: {MaxOrderHistoryLimit})" },
                },
            }.ToJsonElement(),
            Handler = async (argsJson, ct) =>
            {
                try
                {
                    var args = argsJson.HasValue
                        ? argsJson.Value.Deserialize<OrderHistoryArgs>(ToolArgsOptions)
                        : null;
                    var limit = Math.Clamp(args?.Limit ?? DefaultOrderHistoryLimit, 1, MaxOrderHistoryLimit);

                    var rows = await orderHistory.GetRecentAsync(limit, ct);
                    var result = rows.Select(o => new
                    {
                        orderId = o.Id,
                        exchangeId = o.ExchangeId,
                        symbol = o.Symbol.Normalized,
                        side = o.Side.ToString(),
                        type = o.Type.ToString(),
                        quantity = o.Quantity.Value,
                        filledQuantity = o.FilledQuantity.Value,
                        price = o.Price.Value,
                        stopPrice = o.StopPrice?.Value,
                        status = o.Status.ToString(),
                        timeInForce = o.TimeInForce.ToString(),
                        createdAtUtc = o.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
                        updatedAtUtc = o.UpdatedAt?.ToString("O", CultureInfo.InvariantCulture),
                    });

                    return new McpToolResult(IsError: false, Content: [McpContent.CreateText(JsonSerializer.Serialize(result))]);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "MCP: get_order_history failed");
                    return new McpToolResult(IsError: true, Content: [McpContent.CreateText(ex.Message)]);
                }
            },
        });
    }

    private void RegisterGetIndicators(IMcpServerHost host, ChatContextService chatContext)
    {
        host.RegisterTool(new McpToolDefinition
        {
            Name = "get_indicators",
            Description =
                "Get the workspace chart's enabled indicator setups (name + period). " +
                "An empty list means none are enabled (or none are known yet).",
            InputSchema = new { type = "object", properties = new { } }.ToJsonElement(),
            Handler = (_, _) =>
            {
                try
                {
                    var snapshot = chatContext.Current;
                    var result = (snapshot.Indicators ?? []).Select(row => new
                    {
                        name = row.Name,
                        period = row.Period,
                    });

                    return Task.FromResult(new McpToolResult(
                        IsError: false,
                        Content: [McpContent.CreateText(JsonSerializer.Serialize(result))]));
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "MCP: get_indicators failed");
                    return Task.FromResult(new McpToolResult(IsError: true, Content: [McpContent.CreateText(ex.Message)]));
                }
            },
        });
    }

    private void RegisterGetTradeStats(IMcpServerHost host, IOrderStatsQuery orderStats)
    {
        host.RegisterTool(new McpToolDefinition
        {
            Name = "get_trade_stats",
            Description =
                "Get the trade statistics over the persistent journal (the trade count, the outcome counts, " +
                "the win rate, the profit factor, the realized total, the volume, the max drawdown). " +
                $"Optional symbol and exchange filters; the scan reads at most {MaxStatsScanPages} pages of " +
                $"{StatsScanPageSize} rows and reports truncated honestly when more may exist. " +
                "The equity curve is not part of the answer.",
            InputSchema = new
            {
                type = "object",
                properties = new
                {
                    symbol = new { type = "string", description = "Optional symbol filter (e.g. BTC/USDT)" },
                    exchange = new { type = "string", description = "Optional exchange id filter (e.g. paper)" },
                },
            }.ToJsonElement(),
            Handler = async (argsJson, ct) =>
            {
                try
                {
                    var args = argsJson.HasValue
                        ? argsJson.Value.Deserialize<TradeStatsArgs>(ToolArgsOptions)
                        : null;
                    var symbol = string.IsNullOrWhiteSpace(args?.Symbol) ? null : args!.Symbol!.Trim();
                    var exchange = string.IsNullOrWhiteSpace(args?.Exchange) ? null : args!.Exchange!.Trim();
                    var filter = new OrderStatsFilter(symbol, exchange, From: null, To: null, IsPaper: null);

                    var accumulator = new TradeStatsAccumulator();
                    Order? after = null;
                    long scanned = 0;
                    var pages = 0;
                    var lastPageFull = false;

                    while (pages < MaxStatsScanPages)
                    {
                        var page = await orderStats.ReadPageAsync(filter, after, StatsScanPageSize, ct);
                        if (page.Count == 0)
                        {
                            lastPageFull = false;
                            break;
                        }

                        accumulator.Ingest(page);
                        after = page[^1];
                        scanned += page.Count;
                        pages++;
                        lastPageFull = page.Count == StatsScanPageSize;
                    }

                    var summary = accumulator.Snapshot();
                    var result = new
                    {
                        trades = summary.Trades,
                        wins = summary.Wins,
                        losses = summary.Losses,
                        winRate = summary.WinRate,
                        profitFactor = summary.ProfitFactor,
                        realizedTotal = summary.RealizedTotal,
                        maxDrawdown = summary.MaxDrawdown,
                        volume = summary.Volume,
                        averageWinner = summary.AverageWinner,
                        averageLoser = summary.AverageLoser,
                        scanned = scanned,
                        truncated = lastPageFull,
                    };

                    return new McpToolResult(IsError: false, Content: [McpContent.CreateText(JsonSerializer.Serialize(result))]);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "MCP: get_trade_stats failed");
                    return new McpToolResult(IsError: true, Content: [McpContent.CreateText(ex.Message)]);
                }
            },
        });
    }

    private void RegisterPlaceAlert(
        IMcpServerHost host,
        IAlertEngine alerts,
        IConfirmationService confirmations,
        ILocalizationManager localization)
    {
        host.RegisterTool(new McpToolDefinition
        {
            Name = "place_alert",
            Description =
                "Place a price alert. Kind Price watches one level (price required); kind PercentMove watches " +
                "a relative move (thresholdPercent + windowMinutes required). The trigger reads Above, Below or At " +
                "(At is meaningless for PercentMove). Optional reArm (OneShot, PerCross, Cooldown), " +
                "cooldownMinutes (Cooldown requires a positive one) and sound (Silent, Beep). " +
                "Requires human confirmation before the alert joins the roster.",
            InputSchema = new
            {
                type = "object",
                properties = new
                {
                    symbol = new { type = "string", description = "Trading pair (e.g., BTC/USDT)" },
                    kind = new { type = "string", @enum = new[] { "Price", "PercentMove" } },
                    trigger = new { type = "string", @enum = new[] { "Above", "Below", "At" } },
                    price = new { type = "number", description = "The Price kind's level (required for Price)" },
                    thresholdPercent = new { type = "number", description = "The PercentMove kind's percent (required for PercentMove)" },
                    windowMinutes = new { type = "integer", description = "The PercentMove kind's trailing window in minutes (required for PercentMove)" },
                    note = new { type = "string", description = "Optional free-text note" },
                    reArm = new { type = "string", @enum = new[] { "OneShot", "PerCross", "Cooldown" } },
                    cooldownMinutes = new { type = "integer", description = "The Cooldown re-arm's window in minutes" },
                    sound = new { type = "string", @enum = new[] { "Silent", "Beep" } },
                },
                required = new[] { "symbol", "kind", "trigger" },
            }.ToJsonElement(),
            Handler = async (argsJson, ct) =>
            {
                try
                {
                    if (argsJson is null)
                        return new McpToolResult(IsError: true, Content: [McpContent.CreateText("Arguments required")]);

                    var args = argsJson.Value.Deserialize<PlaceAlertArgs>(ToolArgsOptions);
                    if (args is null)
                        return new McpToolResult(IsError: true, Content: [McpContent.CreateText("Invalid arguments")]);

                    if (string.IsNullOrWhiteSpace(args.Symbol))
                        return new McpToolResult(IsError: true, Content: [McpContent.CreateText("Symbol is required")]);

                    // Trade-critical enums: only a defined member passes
                    // (the strict-parse discipline; never a default for an
                    // unparsable value).
                    if (!ToolEnumParser.TryParseDefined(args.Kind, out PriceAlertKind kind))
                        return new McpToolResult(IsError: true, Content: [McpContent.CreateText($"Invalid kind: {args.Kind}. Must be one of: Price, PercentMove")]);

                    if (kind == PriceAlertKind.IndicatorCross)
                        return new McpToolResult(IsError: true, Content: [McpContent.CreateText("The place_alert surface carries the Price and PercentMove kinds only")]);

                    if (!ToolEnumParser.TryParseDefined(args.Trigger, out AlertTrigger trigger))
                        return new McpToolResult(IsError: true, Content: [McpContent.CreateText($"Invalid trigger: {args.Trigger}. Must be one of: {ToolEnumParser.AllowedNames<AlertTrigger>()}")]);

                    AlertReArmMode reArm = AlertReArmMode.OneShot;
                    if (args.ReArm is not null && !ToolEnumParser.TryParseDefined(args.ReArm, out reArm))
                        return new McpToolResult(IsError: true, Content: [McpContent.CreateText($"Invalid reArm: {args.ReArm}. Must be one of: {ToolEnumParser.AllowedNames<AlertReArmMode>()}")]);

                    AlertSoundChoice sound = AlertSoundChoice.Beep;
                    if (args.Sound is not null && !ToolEnumParser.TryParseDefined(args.Sound, out sound))
                        return new McpToolResult(IsError: true, Content: [McpContent.CreateText($"Invalid sound: {args.Sound}. Must be one of: {ToolEnumParser.AllowedNames<AlertSoundChoice>()}")]);

                    var symbol = args.Symbol.Trim();
                    var draft = new AlertDraft(
                        symbol,
                        kind,
                        trigger,
                        TargetPrice: args.Price ?? 0m,
                        ThresholdPercent: args.ThresholdPercent ?? 0m,
                        WindowMinutes: args.WindowMinutes ?? 0,
                        Note: args.Note?.Trim() ?? string.Empty,
                        ReArm: reArm,
                        CooldownMinutes: args.CooldownMinutes ?? 0,
                        Sound: sound);

                    try
                    {
                        draft.Validate();
                    }
                    catch (ArgumentException ex)
                    {
                        return new McpToolResult(IsError: true, Content: [McpContent.CreateText($"Invalid alert: {ex.Message}")]);
                    }

                    // The trading-adjacent gate: an AI-authored alert joins
                    // the roster only through the human confirmation (the
                    // same gate an MCP order rides).
                    var confirmed = await confirmations.RequestConfirmationAsync(
                        BuildPlaceAlertConfirmation(localization, kind, trigger, args, symbol),
                        ct);
                    if (!confirmed)
                    {
                        return new McpToolResult(IsError: true, Content: [McpContent.CreateText("Alert cancelled by user")]);
                    }

                    var definition = await alerts.CreateAsync(draft, ct);
                    logger.LogInformation("MCP: Alert placed — {Alert}", definition.Id);

                    return new McpToolResult(
                        IsError: false,
                        Content: [McpContent.CreateText(JsonSerializer.Serialize(new
                        {
                            alertId = definition.Id,
                            symbol = definition.SymbolName,
                            kind = definition.Kind.ToString(),
                            trigger = definition.Trigger.ToString(),
                            targetPrice = definition.TargetPrice,
                            thresholdPercent = definition.ThresholdPercent,
                            windowMinutes = definition.WindowMinutes,
                            status = definition.Status.ToString(),
                            reArm = definition.ReArm.ToString(),
                        }))]);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "MCP: place_alert failed");
                    return new McpToolResult(IsError: true, Content: [McpContent.CreateText(ex.Message)]);
                }
            },
        });
    }

    private void RegisterCancelAlert(IMcpServerHost host, IAlertEngine alerts)
    {
        host.RegisterTool(new McpToolDefinition
        {
            Name = "cancel_alert",
            Description = "Cancel (delete) an alert by its id. An unknown id is an error, never a quiet no-op.",
            InputSchema = new
            {
                type = "object",
                properties = new
                {
                    id = new { type = "string", description = "The alert id (as place_alert answered it)" },
                },
                required = new[] { "id" },
            }.ToJsonElement(),
            Handler = async (argsJson, ct) =>
            {
                try
                {
                    if (argsJson is null)
                        return new McpToolResult(IsError: true, Content: [McpContent.CreateText("Arguments required")]);

                    var args = argsJson.Value.Deserialize<CancelAlertArgs>(ToolArgsOptions);
                    if (args?.Id is null || string.IsNullOrWhiteSpace(args.Id))
                        return new McpToolResult(IsError: true, Content: [McpContent.CreateText("id is required")]);

                    if (!Guid.TryParse(args.Id, out var id))
                        return new McpToolResult(IsError: true, Content: [McpContent.CreateText($"Invalid alert id: {args.Id}")]);

                    await alerts.DeleteAsync(id, ct);
                    logger.LogInformation("MCP: Alert cancelled — {AlertId}", id);

                    return new McpToolResult(IsError: false, Content: [McpContent.CreateText($"Alert {id} cancelled")]);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "MCP: cancel_alert failed");
                    return new McpToolResult(IsError: true, Content: [McpContent.CreateText(ex.Message)]);
                }
            },
        });
    }

    #endregion Tool Registration

    #region Internals

    /// <summary>
    /// Compose a structured, localized confirmation request: the UI resolves
    /// the template in the current language; numbers are pre-formatted with
    /// InvariantCulture. The level rides the kind's own fact: the Price
    /// kind's target, or the PercentMove kind's percent + window.
    /// </summary>
    private static ConfirmationRequest BuildPlaceAlertConfirmation(
        ILocalizationManager localization,
        PriceAlertKind kind,
        AlertTrigger trigger,
        PlaceAlertArgs args,
        string symbol)
    {
        var levelText = kind == PriceAlertKind.Price
            ? (args.Price ?? 0m).ToString(CultureInfo.InvariantCulture)
            : $"{(args.ThresholdPercent ?? 0m).ToString(CultureInfo.InvariantCulture)}% / {(args.WindowMinutes ?? 0)}m";

        return new ConfirmationRequest
        {
            MessageKey = ConfirmPlaceAlertKey,
            Arguments =
            [
                kind.ToString(),
                trigger.ToString(),
                symbol,
                levelText,
            ],
        };
    }

    /// <summary>The workspace snapshot's compact JSON (the honest absent parts stay absent).</summary>
    private static string WorkspaceSnapshotJson(ChatContextService chatContext)
    {
        var context = chatContext.Current;
        var snapshot = new
        {
            exchangeName = context.ExchangeName,
            venueId = context.VenueId,
            isPaperVenue = context.IsPaperVenue,
            symbol = context.Symbol,
            timeFrame = context.TimeFrame?.ToString(),
            lastPrice = context.LastPrice,
            priceChange24hPercent = context.PriceChange24hPercent,
            bestBids = context.BestBids?.Select(level => new { price = level.Price, quantity = level.Quantity }),
            bestAsks = context.BestAsks?.Select(level => new { price = level.Price, quantity = level.Quantity }),
            indicators = context.Indicators?.Select(row => new { name = row.Name, period = row.Period }),
        };

        return JsonSerializer.Serialize(snapshot);
    }

    /// <summary>The portfolio snapshot's compact JSON (balances + open positions).</summary>
    private static async Task<string> PortfolioSnapshotJsonAsync(ITradingAdapter tradingSurface, CancellationToken ct)
    {
        var balances = await tradingSurface.GetBalancesAsync(ct);
        var positions = await tradingSurface.GetPositionsAsync(ct);
        var snapshot = new
        {
            exchangeId = tradingSurface.Id,
            generatedAtUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            balances = balances.Select(kv => new { asset = kv.Key, amount = kv.Value.Amount }),
            positions = positions.Select(p => new
            {
                id = p.Id,
                exchangeId = p.ExchangeId,
                symbol = p.Symbol.Normalized,
                side = p.Side.ToString(),
                quantity = p.Quantity.Value,
                entryPrice = p.EntryPrice.Value,
                currentPrice = p.CurrentPrice.Value,
                openedAtUtc = p.OpenedAt.ToString("O", CultureInfo.InvariantCulture),
                stopLoss = p.StopLoss?.Value,
                takeProfit = p.TakeProfit?.Value,
            }),
        };

        return JsonSerializer.Serialize(snapshot);
    }

    #endregion Internals

    #region Tool Arguments

    private sealed record OrderHistoryArgs(int? Limit);

    private sealed record TradeStatsArgs(string? Symbol, string? Exchange);

    private sealed record PlaceAlertArgs(
        string? Symbol,
        string? Kind,
        string? Trigger,
        decimal? Price,
        decimal? ThresholdPercent,
        int? WindowMinutes,
        string? Note,
        string? ReArm,
        int? CooldownMinutes,
        string? Sound);

    private sealed record CancelAlertArgs(string? Id);

    #endregion Tool Arguments
}
