namespace ZettaTrade.App.Tests.Mcp;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Livery.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using ZettaTrade.AI.Mcp;
using ZettaTrade.App.Services;
using ZettaTrade.Charting;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.Infrastructure.Persistence;
using ZettaTrade.Infrastructure.Sound;
using ZettaTrade.Plugins.Abstractions;

/// <summary>
/// The workspace MCP surface's argument and data facts (T-208): the read
/// tools answer honestly over their stores and the workspace context, the
/// bounded scan states its bound (truncated), the alert pair parses its
/// enums strictly and rides the human confirmation before the roster, and
/// the resources/prompts render the snapshots they promise. The acceptance's
/// trading-adjacent fact rides the REAL alert engine: an alert placed via
/// MCP appears in the roster (the manager's source) and fires normally on
/// its edge. All exercises run OFFLINE: the fake adapter is the only tick
/// source.
/// </summary>
public sealed class McpWorkspaceToolsRegistrarTests
{
    #region Constants

    private static readonly Symbol BtcUsdt = Symbol.Parse("BTC/USDT");

    /// <summary>The UTC base the test clock pins.</summary>
    private static readonly DateTimeOffset Base = new(2026, 3, 2, 12, 0, 0, TimeSpan.Zero);

    /// <summary>How long a queued fire may take to surface (a wait budget, never wall-clock semantics).</summary>
    private static readonly TimeSpan EventWaitBudget = TimeSpan.FromSeconds(10);

    #endregion

    #region Registration shape

    /// <summary>The registrar registers exactly its two resources, two prompts and five tools.</summary>
    [Fact]
    public void RegisterAll_TheSurfaces_RegisterExactly()
    {
        var kit = CreateKit();

        kit.Host.Tools.Keys.Should().BeEquivalentTo([
            "get_order_history", "get_indicators", "get_trade_stats", "place_alert", "cancel_alert",
        ]);
        kit.Host.Resources.Keys.Should().BeEquivalentTo([
            "zettatrade://portfolio", "zettatrade://workspace",
        ]);
        kit.Host.Prompts.Keys.Should().BeEquivalentTo(["market_brief", "portfolio_review"]);
    }

    #endregion

    #region Resources

    /// <summary>The portfolio resource reads balances and positions off the trading surface.</summary>
    [Fact]
    public async Task PortfolioResource_TheRead_CarriesBalancesAndPositions()
    {
        var kit = CreateKit();

        var content = await kit.Host.Resources["zettatrade://portfolio"].Reader(CancellationToken.None);

        content.Should().ContainSingle();
        using var doc = JsonDocument.Parse(content[0].TextValue!);
        doc.RootElement.GetProperty("exchangeId").GetString().Should().Be("paper");
        var balances = doc.RootElement.GetProperty("balances");
        balances.GetArrayLength().Should().Be(2);
        balances.EnumerateArray().Should().Contain(level =>
            level.GetProperty("asset").GetString() == "USDT" &&
            level.GetProperty("amount").GetDecimal() == 1000m);
        var positions = doc.RootElement.GetProperty("positions");
        positions.GetArrayLength().Should().Be(1);
        var position = positions[0];
        position.GetProperty("symbol").GetString().Should().Be("BTC/USDT");
        position.GetProperty("side").GetString().Should().Be("Buy");
        position.GetProperty("quantity").GetDecimal().Should().Be(1m);
        position.GetProperty("entryPrice").GetDecimal().Should().Be(100m);
        position.GetProperty("currentPrice").GetDecimal().Should().Be(110m);
    }

    /// <summary>The workspace resource renders the snapshot it has: absent facts stay absent.</summary>
    [Fact]
    public async Task WorkspaceResource_AStillWorkspace_RendersTheAbsentPartsAbsent()
    {
        var kit = CreateKit();

        var content = await kit.Host.Resources["zettatrade://workspace"].Reader(CancellationToken.None);

        content.Should().ContainSingle();
        using var doc = JsonDocument.Parse(content[0].TextValue!);
        doc.RootElement.GetProperty("exchangeName").GetString().Should().Be("Demo");
        doc.RootElement.GetProperty("symbol").ValueKind.Should().Be(JsonValueKind.Null);
        doc.RootElement.GetProperty("lastPrice").ValueKind.Should().Be(JsonValueKind.Null);
        doc.RootElement.GetProperty("indicators").ValueKind.Should().Be(JsonValueKind.Null);
    }

    /// <summary>A symbol that applied and ticked shows up in the workspace resource.</summary>
    [Fact]
    public async Task WorkspaceResource_AppliedSymbolAndTick_RenderTheLiveFacts()
    {
        var kit = CreateKit();
        kit.Symbols.OnNext(BtcUsdt);
        await kit.Context.InitializeAsync();

        kit.Market.Tickers[BtcUsdt.Normalized].OnNext(
            new Ticker(BtcUsdt, 42000m, 41999m, 42001m, 12m, 1.5m, Base));

        var content = await kit.Host.Resources["zettatrade://workspace"].Reader(CancellationToken.None);

        using var doc = JsonDocument.Parse(content[0].TextValue!);
        doc.RootElement.GetProperty("symbol").GetString().Should().Be("BTC/USDT");
        doc.RootElement.GetProperty("lastPrice").GetDecimal().Should().Be(42000m);
        doc.RootElement.GetProperty("priceChange24hPercent").GetDecimal().Should().Be(1.5m);
        doc.RootElement.GetProperty("indicators").GetArrayLength().Should().Be(1, "the default rows loaded: one SMA(20)");
        doc.RootElement.GetProperty("indicators")[0].GetProperty("name").GetString().Should().Be("SMA");
    }

    #endregion

    #region Prompts

    /// <summary>The market brief renders the workspace facts into its template text.</summary>
    [Fact]
    public async Task MarketBriefPrompt_TheRender_CarriesTheWorkspaceFacts()
    {
        var kit = CreateKit();
        kit.Symbols.OnNext(BtcUsdt);
        kit.Market.Tickers[BtcUsdt.Normalized].OnNext(
            new Ticker(BtcUsdt, 42000m, 41999m, 42001m, 12m, 1.5m, Base));

        var text = await kit.Host.Prompts["market_brief"].Handler([], CancellationToken.None);

        text.Should().Contain("market brief").And.Contain("Workspace facts").And.Contain("Demo");
        using var facts = JsonDocument.Parse(text.Split("Workspace facts (JSON):\n")[1]);
        facts.RootElement.GetProperty("symbol").GetString().Should().Be("BTC/USDT");
        facts.RootElement.GetProperty("lastPrice").GetDecimal().Should().Be(42000m);
    }

    /// <summary>The portfolio review renders the current portfolio facts into its template text.</summary>
    [Fact]
    public async Task PortfolioReviewPrompt_TheRender_CarriesThePortfolioFacts()
    {
        var kit = CreateKit();

        var text = await kit.Host.Prompts["portfolio_review"].Handler([], CancellationToken.None);

        text.Should().Contain("portfolio").And.Contain("Portfolio snapshot").And.Contain("USDT");
        using var facts = JsonDocument.Parse(text.Split("Portfolio snapshot (JSON):\n")[1]);
        facts.RootElement.GetProperty("positions").GetArrayLength().Should().Be(1);
        facts.RootElement.GetProperty("balances").GetArrayLength().Should().Be(2);
    }

    #endregion

    #region get_order_history

    /// <summary>The tool reads the recent journal rows with the default limit.</summary>
    [Fact]
    public async Task GetOrderHistory_TheDefaultLimit_ReadsTwenty()
    {
        var kit = CreateKit();
        kit.History.GetRecentAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var result = await kit.Host.Tools["get_order_history"].Handler(Args("{}"), CancellationToken.None);

        result.IsError.Should().BeFalse();
        await kit.History.Received(1).GetRecentAsync(20, Arg.Any<CancellationToken>());
    }

    /// <summary>The limit clamps into the bounded window: never below 1, never above 200.</summary>
    [Fact]
    public async Task GetOrderHistory_TheLimit_ClampsIntoTheBound()
    {
        var kit = CreateKit();
        kit.History.GetRecentAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([]);

        await kit.Host.Tools["get_order_history"].Handler(Args("{\"limit\":0}"), CancellationToken.None);
        await kit.Host.Tools["get_order_history"].Handler(Args("{\"limit\":9999}"), CancellationToken.None);

        Received.InOrder(() =>
        {
            kit.History.GetRecentAsync(1, Arg.Any<CancellationToken>());
            kit.History.GetRecentAsync(200, Arg.Any<CancellationToken>());
        });
    }

    /// <summary>The rows carry the journal facts the journal rows hold.</summary>
    [Fact]
    public async Task GetOrderHistory_TheRows_CarryTheJournalFacts()
    {
        var kit = CreateKit();
        kit.History.GetRecentAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([JournalOrder("o1", OrderSide.Buy, OrderType.Limit, 100m, OrderStatus.Filled)]);

        var result = await kit.Host.Tools["get_order_history"].Handler(Args("{}"), CancellationToken.None);

        result.IsError.Should().BeFalse();
        using var doc = JsonDocument.Parse(result.Content[0].TextValue!);
        var row = doc.RootElement[0];
        row.GetProperty("orderId").GetString().Should().Be("o1");
        row.GetProperty("exchangeId").GetString().Should().Be("paper");
        row.GetProperty("symbol").GetString().Should().Be("BTC/USDT");
        row.GetProperty("side").GetString().Should().Be("Buy");
        row.GetProperty("type").GetString().Should().Be("Limit");
        row.GetProperty("quantity").GetDecimal().Should().Be(1m);
        row.GetProperty("price").GetDecimal().Should().Be(100m);
        row.GetProperty("status").GetString().Should().Be("Filled");
    }

    #endregion

    #region get_indicators

    /// <summary>The loaded setup rows ride the tool's answer.</summary>
    [Fact]
    public async Task GetIndicators_TheLoadedRows_RideTheAnswer()
    {
        var kit = CreateKit();
        await kit.Context.InitializeAsync();

        var result = await kit.Host.Tools["get_indicators"].Handler(Args("{}"), CancellationToken.None);

        result.IsError.Should().BeFalse();
        using var doc = JsonDocument.Parse(result.Content[0].TextValue!);
        doc.RootElement.GetArrayLength().Should().Be(1);
        doc.RootElement[0].GetProperty("name").GetString().Should().Be("SMA");
        doc.RootElement[0].GetProperty("period").GetInt32().Should().Be(20);
    }

    /// <summary>No settings source, no load: the honest empty answer, never a fabricated row.</summary>
    [Fact]
    public async Task GetIndicators_AnUnloadedSetup_AnswersTheHonestEmpty()
    {
        var kit = CreateKit(withSettings: false);

        var result = await kit.Host.Tools["get_indicators"].Handler(Args("{}"), CancellationToken.None);

        result.IsError.Should().BeFalse();
        using var doc = JsonDocument.Parse(result.Content[0].TextValue!);
        doc.RootElement.GetArrayLength().Should().Be(0);
    }

    #endregion

    #region get_trade_stats

    /// <summary>The stats scan reads the pages and answers the accumulator's summary.</summary>
    [Fact]
    public async Task GetTradeStats_OneOpeningAndOneClosingFill_AnswerTheTrade()
    {
        var kit = CreateKit();
        var opening = StatsOrder("o1", OrderSide.Buy, 100m);
        var closing = StatsOrder("o2", OrderSide.Sell, 110m);
        kit.Stats.ReadPageAsync(Arg.Any<OrderStatsFilter>(), Arg.Any<Order?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => callInfo.ArgAt<Order?>(1) is null
                ? Page([opening])
                : callInfo.ArgAt<Order?>(1)!.Id == "o1" ? Page([closing]) : Page([]));

        var result = await kit.Host.Tools["get_trade_stats"].Handler(Args("{}"), CancellationToken.None);

        result.IsError.Should().BeFalse();
        using var doc = JsonDocument.Parse(result.Content[0].TextValue!);
        doc.RootElement.GetProperty("trades").GetInt32().Should().Be(1);
        doc.RootElement.GetProperty("wins").GetInt32().Should().Be(1);
        doc.RootElement.GetProperty("losses").GetInt32().Should().Be(0);
        doc.RootElement.GetProperty("winRate").GetDecimal().Should().Be(1m);
        doc.RootElement.GetProperty("realizedTotal").GetDecimal().Should().Be(10m);
        doc.RootElement.GetProperty("volume").GetDecimal().Should().Be(210m);
        doc.RootElement.GetProperty("scanned").GetInt64().Should().Be(2);
        doc.RootElement.GetProperty("truncated").GetBoolean().Should().BeFalse();
    }

    /// <summary>An empty scope answers the honest zero state with a null win rate.</summary>
    [Fact]
    public async Task GetTradeStats_AnEmptyScope_AnswersTheHonestZeros()
    {
        var kit = CreateKit();
        kit.Stats.ReadPageAsync(Arg.Any<OrderStatsFilter>(), Arg.Any<Order?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Page([]));

        var result = await kit.Host.Tools["get_trade_stats"].Handler(Args("{}"), CancellationToken.None);

        result.IsError.Should().BeFalse();
        using var doc = JsonDocument.Parse(result.Content[0].TextValue!);
        doc.RootElement.GetProperty("trades").GetInt32().Should().Be(0);
        doc.RootElement.GetProperty("winRate").ValueKind.Should().Be(JsonValueKind.Null);
        doc.RootElement.GetProperty("truncated").GetBoolean().Should().BeFalse();
    }

    /// <summary>The scan stops at its page cap and discloses the truncation honestly.</summary>
    [Fact]
    public async Task GetTradeStats_TheScanCap_DisclosesTheTruncation()
    {
        var kit = CreateKit();
        var fullPage = Page(Enumerable.Range(0, 2000).Select(_ => StatsOrder(Guid.NewGuid().ToString(), OrderSide.Buy, 100m)).ToList());
        kit.Stats.ReadPageAsync(Arg.Any<OrderStatsFilter>(), Arg.Any<Order?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(fullPage);

        var result = await kit.Host.Tools["get_trade_stats"].Handler(Args("{}"), CancellationToken.None);

        result.IsError.Should().BeFalse();
        using var doc = JsonDocument.Parse(result.Content[0].TextValue!);
        doc.RootElement.GetProperty("scanned").GetInt64().Should().Be(20 * 2000);
        doc.RootElement.GetProperty("truncated").GetBoolean().Should().BeTrue();
    }

    #endregion

    #region place_alert

    /// <summary>A valid price alert asks the human confirmation, joins the roster and answers its facts.</summary>
    [Fact]
    public async Task PlaceAlert_AValidPriceDraft_ConfirmsThenJoinsTheRoster()
    {
        var kit = CreateKit();
        AlertDraft? submitted = null;
        kit.Alerts.CreateAsync(Arg.Do<AlertDraft>(draft => submitted = draft), Arg.Any<CancellationToken>())
            .Returns(callInfo => callInfo.ArgAt<AlertDraft>(0).ToCreated(Base));

        var result = await kit.Host.Tools["place_alert"].Handler(
            Args("{\"symbol\":\"BTC/USDT\",\"kind\":\"Price\",\"trigger\":\"Below\",\"price\":42000,\"note\":\"dip\"}"),
            CancellationToken.None);

        result.IsError.Should().BeFalse();
        await kit.Confirmations.Received(1).RequestConfirmationAsync(
            Arg.Is<ConfirmationRequest>(request => request.MessageKey == "Trading.ConfirmPlaceAlertFormat"),
            Arg.Any<CancellationToken>());
        submitted.Should().NotBeNull();
        submitted!.SymbolName.Should().Be("BTC/USDT");
        submitted.Kind.Should().Be(PriceAlertKind.Price);
        submitted.Trigger.Should().Be(AlertTrigger.Below);
        submitted.TargetPrice.Should().Be(42000m);
        submitted.Note.Should().Be("dip");
        using var doc = JsonDocument.Parse(result.Content[0].TextValue!);
        doc.RootElement.GetProperty("status").GetString().Should().Be("Armed");
    }

    /// <summary>A declined confirmation never reaches the roster.</summary>
    [Fact]
    public async Task PlaceAlert_ADeclinedConfirmation_NeverReachesTheRoster()
    {
        var kit = CreateKit(confirm: false);
        kit.Alerts.CreateAsync(Arg.Any<AlertDraft>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => callInfo.ArgAt<AlertDraft>(0).ToCreated(Base));

        var result = await kit.Host.Tools["place_alert"].Handler(
            Args("{\"symbol\":\"BTC/USDT\",\"kind\":\"Price\",\"trigger\":\"Below\",\"price\":42000}"),
            CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Content[0].TextValue.Should().Contain("cancelled by user");
        await kit.Alerts.DidNotReceive().CreateAsync(Arg.Any<AlertDraft>(), Arg.Any<CancellationToken>());
    }

    /// <summary>The draft validates BEFORE the confirmation: an invalid alert never bothers the human.</summary>
    [Fact]
    public async Task PlaceAlert_APriceDraftWithoutAPrice_RefusesBeforeTheConfirmation()
    {
        var kit = CreateKit();

        var result = await kit.Host.Tools["place_alert"].Handler(
            Args("{\"symbol\":\"BTC/USDT\",\"kind\":\"Price\",\"trigger\":\"Below\"}"),
            CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Content[0].TextValue.Should().Contain("Invalid alert").And.Contain("target price");
        await kit.Confirmations.DidNotReceive().RequestConfirmationAsync(Arg.Any<ConfirmationRequest>(), Arg.Any<CancellationToken>());
    }

    /// <summary>A numeric kind is an explicit error naming the allowed names.</summary>
    [Fact]
    public async Task PlaceAlert_ANumericKind_IsAnExplicitError()
    {
        var kit = CreateKit();

        var result = await kit.Host.Tools["place_alert"].Handler(
            Args("{\"symbol\":\"BTC/USDT\",\"kind\":\"42\",\"trigger\":\"Below\",\"price\":1}"),
            CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Content[0].TextValue.Should().Contain("Invalid kind").And.Contain("Price, PercentMove");
    }

    /// <summary>The indicator-cross kind parses but the surface refuses it explicitly (the schema's enum never offered it).</summary>
    [Fact]
    public async Task PlaceAlert_TheIndicatorCrossKind_IsAnExplicitSurfaceRefusal()
    {
        var kit = CreateKit();

        var result = await kit.Host.Tools["place_alert"].Handler(
            Args("{\"symbol\":\"BTC/USDT\",\"kind\":\"IndicatorCross\",\"trigger\":\"Above\",\"price\":1}"),
            CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Content[0].TextValue.Should().Contain("Price and PercentMove kinds only");
    }

    /// <summary>An undefined re-arm name refuses: "ReArm" is not a member (the members are OneShot, PerCross, Cooldown).</summary>
    [Fact]
    public async Task PlaceAlert_AnUndefinedReArmName_IsAnExplicitError()
    {
        var kit = CreateKit();

        var result = await kit.Host.Tools["place_alert"].Handler(
            Args("{\"symbol\":\"BTC/USDT\",\"kind\":\"Price\",\"trigger\":\"Below\",\"price\":1,\"reArm\":\"ReArm\"}"),
            CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Content[0].TextValue.Should().Contain("Invalid reArm").And.Contain("OneShot, PerCross, Cooldown");
    }

    /// <summary>A Cooldown re-arm with no positive window refuses at draft validation.</summary>
    [Fact]
    public async Task PlaceAlert_ACooldownWithoutAWindow_RefusesAtValidation()
    {
        var kit = CreateKit();

        var result = await kit.Host.Tools["place_alert"].Handler(
            Args("{\"symbol\":\"BTC/USDT\",\"kind\":\"Price\",\"trigger\":\"Below\",\"price\":1,\"reArm\":\"Cooldown\"}"),
            CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Content[0].TextValue.Should().Contain("cooldown");
    }

    /// <summary>An undefined sound name refuses exactly the same way.</summary>
    [Fact]
    public async Task PlaceAlert_AnUndefinedSoundName_IsAnExplicitError()
    {
        var kit = CreateKit();

        var result = await kit.Host.Tools["place_alert"].Handler(
            Args("{\"symbol\":\"BTC/USDT\",\"kind\":\"Price\",\"trigger\":\"Below\",\"price\":1,\"sound\":\"Buzz\"}"),
            CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Content[0].TextValue.Should().Contain("Invalid sound").And.Contain("Silent, Beep");
    }

    /// <summary>A valid percent-move draft passes validation and rides the same gates.</summary>
    [Fact]
    public async Task PlaceAlert_AValidPercentMoveDraft_RidesTheSameGates()
    {
        var kit = CreateKit();
        AlertDraft? submitted = null;
        kit.Alerts.CreateAsync(Arg.Do<AlertDraft>(draft => submitted = draft), Arg.Any<CancellationToken>())
            .Returns(callInfo => callInfo.ArgAt<AlertDraft>(0).ToCreated(Base));

        var result = await kit.Host.Tools["place_alert"].Handler(
            Args("{\"symbol\":\"BTC/USDT\",\"kind\":\"PercentMove\",\"trigger\":\"At\",\"thresholdPercent\":2.5,\"windowMinutes\":30}"),
            CancellationToken.None);

        result.IsError.Should().BeFalse();
        submitted!.Kind.Should().Be(PriceAlertKind.PercentMove);
        submitted.ThresholdPercent.Should().Be(2.5m);
        submitted.WindowMinutes.Should().Be(30);
    }

    #endregion

    #region cancel_alert

    /// <summary>A known alert id removes the definition from the engine.</summary>
    [Fact]
    public async Task CancelAlert_AKnownId_DeletesTheDefinition()
    {
        var kit = CreateKit();
        var knownId = Guid.NewGuid();

        var result = await kit.Host.Tools["cancel_alert"].Handler(
            Args($"{{\"id\":\"{knownId}\"}}"),
            CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Content[0].TextValue.Should().Contain("cancelled").And.Contain(knownId.ToString());
        await kit.Alerts.Received(1).DeleteAsync(knownId, Arg.Any<CancellationToken>());
    }

    /// <summary>An unparsable id refuses without touching the engine.</summary>
    [Fact]
    public async Task CancelAlert_AnUnparsableId_RefusesWithoutTouchingTheEngine()
    {
        var kit = CreateKit();

        var result = await kit.Host.Tools["cancel_alert"].Handler(
            Args("{\"id\":\"not-a-guid\"}"),
            CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Content[0].TextValue.Should().Contain("Invalid alert id");
        await kit.Alerts.DidNotReceive().DeleteAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    /// <summary>An unknown id surfaces the engine's honest error.</summary>
    [Fact]
    public async Task CancelAlert_AnUnknownId_SurfacesTheEnginesError()
    {
        var kit = CreateKit();
        kit.Alerts.When(alerts => alerts.DeleteAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()))
            .Do(_ => throw new InvalidOperationException("The alert does not exist."));

        var result = await kit.Host.Tools["cancel_alert"].Handler(
            Args($"{{\"id\":\"{Guid.NewGuid()}\"}}"),
            CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Content[0].TextValue.Should().Contain("does not exist");
    }

    #endregion

    #region The acceptance: an alert placed via MCP appears in the manager and fires normally

    /// <summary>
    /// The acceptance's teeth over the REAL alert engine: the MCP-placed
    /// alert joins the roster (the manager renders the engine's definitions)
    /// and the very next tick past its edge fires it normally.
    /// </summary>
    [Fact]
    public async Task PlaceAlert_ThroughMcp_AppearsInTheRoster_AndFiresOnItsEdge()
    {
        var rig = BuildEngineRig();
        await rig.Engine.InitializeAsync();

        var placed = await rig.Host.Tools["place_alert"].Handler(
            Args("{\"symbol\":\"BTC/USDT\",\"kind\":\"Price\",\"trigger\":\"Below\",\"price\":42000}"),
            CancellationToken.None);

        placed.IsError.Should().BeFalse();
        using var doc = JsonDocument.Parse(placed.Content[0].TextValue!);
        var alertId = Guid.Parse(doc.RootElement.GetProperty("alertId").GetString()!);
        rig.Engine.Definitions.Should().ContainSingle(
            definition => definition.Id == alertId,
            "the alert the MCP surface placed is in the roster the manager renders");

        var firstFire = new TaskCompletionSource<AlertEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Engine.Events.Subscribe(
            @event => { if (@event.Kind == AlertEventKind.Fired) firstFire.TrySetResult(@event); });

        rig.Market.Tickers[BtcUsdt.Normalized].OnNext(
            new Ticker(BtcUsdt, 41000m, 40999m, 41001m, 12m, 0m, Base));

        var fire = await AwaitFire(firstFire);
        fire.Definition.Should().NotBeNull();
        fire.Definition!.Id.Should().Be(alertId, "the fired alert is exactly the one MCP placed");
        fire.Definition.Status.Should().Be(AlertStatus.Fired);
    }

    #endregion

    #region Helpers

    /// <summary>Build the raw arguments element a host hands its tool handlers.</summary>
    private static JsonElement Args(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    /// <summary>Wait out the engine's queued fire within the budget.</summary>
    private static async Task<AlertEvent> AwaitFire(TaskCompletionSource<AlertEvent> firstFire)
    {
        var finished = await Task.WhenAny(firstFire.Task, Task.Delay(EventWaitBudget));
        finished.Should().Be(firstFire.Task, "the fire must surface within the wait budget");
        return await firstFire.Task;
    }

    private static Order JournalOrder(string id, OrderSide side, OrderType type, decimal price, OrderStatus status) => new()
    {
        Id = id,
        ExchangeId = "paper",
        Symbol = BtcUsdt,
        Side = side,
        Type = type,
        Quantity = new Quantity(1m),
        Price = new Price(price),
        Status = status,
        TimeInForce = TimeInForce.GTC,
        CreatedAt = Base,
    };

    private static Order StatsOrder(string id, OrderSide side, decimal price) => new()
    {
        Id = id,
        ExchangeId = "paper",
        Symbol = BtcUsdt,
        Side = side,
        Type = OrderType.Market,
        Quantity = new Quantity(1m),
        Price = new Price(price),
        Status = OrderStatus.Filled,
        TimeInForce = TimeInForce.GTC,
        CreatedAt = Base,
        FilledQuantity = new Quantity(1m),
    };

    /// <summary>One stats-scan page row: the journal order plus its (unset)
    /// user annotations (T-211).</summary>
    private static IReadOnlyList<OrderPageRow> Page(List<Order> rows) =>
        [.. rows.Select(order => new OrderPageRow(order, null, null))];

    #endregion

    #region The stub-engine kit

    /// <summary>The registrar's kit over stub seams: the assertions judge what reached the seams.</summary>
    private sealed class WorkspaceKit
    {
        public RecordingHost Host { get; set; } = null!;

        public ITradingAdapter TradingSurface { get; set; } = null!;

        public IAlertEngine Alerts { get; set; } = null!;

        public ChatContextService Context { get; set; } = null!;

        public IOrderHistoryStore History { get; set; } = null!;

        public IOrderStatsQuery Stats { get; set; } = null!;

        public IConfirmationService Confirmations { get; set; } = null!;

        public Subject<Symbol> Symbols { get; set; } = null!;

        public Subject<TimeFrame> TimeFrames { get; set; } = null!;

        public TestMarketAdapter Market { get; set; } = null!;

        public TestSettings Settings { get; set; } = null!;

        public McpWorkspaceToolsRegistrar Registrar { get; set; } = null!;
    }

    private static WorkspaceKit CreateKit(bool confirm = true, bool withSettings = true)
    {
        var kit = new WorkspaceKit
        {
            Host = new RecordingHost(),
            Symbols = new Subject<Symbol>(),
            TimeFrames = new Subject<TimeFrame>(),
            Market = new TestMarketAdapter(),
            Settings = new TestSettings(),
        };

        kit.TradingSurface = Substitute.For<ITradingAdapter>();
        kit.TradingSurface.Id.Returns("paper");
        kit.TradingSurface.GetBalancesAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, Money>
            {
                ["USDT"] = new Money(1000m, "USDT"),
                ["BTC"] = new Money(0.5m, "USDT"),
            });
        kit.TradingSurface.GetPositionsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Position>
            {
                new()
                {
                    Id = "p1",
                    ExchangeId = "paper",
                    Symbol = BtcUsdt,
                    Side = OrderSide.Buy,
                    Quantity = new Quantity(1m),
                    EntryPrice = new Price(100m),
                    CurrentPrice = new Price(110m),
                    OpenedAt = Base,
                },
            });

        kit.Alerts = Substitute.For<IAlertEngine>();
        kit.History = Substitute.For<IOrderHistoryStore>();
        kit.Stats = Substitute.For<IOrderStatsQuery>();
        kit.Confirmations = Substitute.For<IConfirmationService>();
        kit.Confirmations
            .RequestConfirmationAsync(Arg.Any<ConfirmationRequest>(), Arg.Any<CancellationToken>())
            .Returns(confirm);

        kit.Context = new ChatContextService(
            kit.Market,
            kit.Symbols,
            kit.TimeFrames,
            NullLogger<ChatContextService>.Instance,
            tradingAdapter: kit.TradingSurface,
            settings: withSettings ? kit.Settings : null);

        kit.Registrar = new McpWorkspaceToolsRegistrar(NullLogger<McpWorkspaceToolsRegistrar>.Instance);
        kit.Registrar.RegisterAll(
            kit.Host,
            kit.TradingSurface,
            kit.Alerts,
            kit.Context,
            kit.History,
            kit.Stats,
            kit.Confirmations,
            Substitute.For<ILocalizationManager>());

        return kit;
    }

    #endregion

    #region The real-engine rig (the acceptance)

    /// <summary>The acceptance's rig: the REAL engine over the offline seams, plus the registered surface.</summary>
    private sealed class EngineRig
    {
        public AlertEngine Engine { get; set; } = null!;

        public RecordingHost Host { get; set; } = null!;

        public TestMarketAdapter Market { get; set; } = null!;
    }

    private static EngineRig BuildEngineRig()
    {
        var settings = new TestSettings();
        var market = new TestMarketAdapter();
        var engine = new AlertEngine(
            settings,
            market,
            Substitute.For<IIndicatorCatalog>(),
            Substitute.For<IAlertSoundPlayer>(),
            new NotificationCenter(settings, NullLogger<NotificationCenter>.Instance),
            new FixedClock(),
            NullLogger<AlertEngine>.Instance);

        var host = new RecordingHost();
        var registrar = new McpWorkspaceToolsRegistrar(NullLogger<McpWorkspaceToolsRegistrar>.Instance);
        var confirmations = Substitute.For<IConfirmationService>();
        confirmations
            .RequestConfirmationAsync(Arg.Any<ConfirmationRequest>(), Arg.Any<CancellationToken>())
            .Returns(true);
        registrar.RegisterAll(
            host,
            Substitute.For<ITradingAdapter>(),
            engine,
            new ChatContextService(
                market,
                Observable.Empty<Symbol>(),
                Observable.Empty<TimeFrame>(),
                NullLogger<ChatContextService>.Instance),
            Substitute.For<IOrderHistoryStore>(),
            Substitute.For<IOrderStatsQuery>(),
            confirmations,
            Substitute.For<ILocalizationManager>());

        return new EngineRig { Engine = engine, Host = host, Market = market };
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Base;
    }

    #endregion

    #region Fixtures

    /// <summary>The settings fake the engine, the context and the notification center ride.</summary>
    private sealed class TestSettings : ISettingsService
    {
        private readonly Dictionary<string, string> _store = [];

        public Task<T> GetAsync<T>(string section, string key, T defaultValue = default!)
        {
            _store.TryGetValue($"{section}\0{key}", out var value);
            return Task.FromResult(value is null ? defaultValue : (T)(object)value);
        }

        public Task SetAsync<T>(string section, string key, T value)
        {
            _store[$"{section}\0{key}"] = (string)(object?)value!;
            return Task.CompletedTask;
        }

        public IObservable<SettingsChange> Changes => Observable.Empty<SettingsChange>();

        public Task SaveAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task ReloadAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>The offline market-data source: the per-symbol ticker subjects are the only push lane.</summary>
    private sealed class TestMarketAdapter : IMarketDataAdapter
    {
        public string Id => "demo";

        public string Name => "Demo";

        public bool IsConnected => true;

        public ExchangeCapabilities Capabilities => ExchangeCapabilities.MarketData;

        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<IReadOnlyList<Symbol>> GetSymbolsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Symbol>>([BtcUsdt]);

        public IObservable<Ticker> SubscribeTicker(Symbol symbol) =>
            Tickers.GetOrAdd(symbol.Normalized, _ => new Subject<Ticker>());

        public IObservable<OrderBook> SubscribeOrderBook(Symbol symbol, int depth = 20) => Observable.Empty<OrderBook>();

        public IObservable<MarketTrade> SubscribeTrades(Symbol symbol) => Observable.Empty<MarketTrade>();

        public Task<IReadOnlyList<Bar>> GetBarsAsync(
            Symbol symbol,
            TimeFrame timeFrame,
            DateTimeOffset from,
            DateTimeOffset? to = null,
            int limit = 500,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Bar>>([]);

        public ConcurrentDictionary<string, Subject<Ticker>> Tickers { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>An IMcpServerHost stand-in that records the registered surfaces by their keys.</summary>
    private sealed class RecordingHost : IMcpServerHost
    {
        public Dictionary<string, McpToolDefinition> Tools { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, McpResourceDefinition> Resources { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, McpPromptDefinition> Prompts { get; } = new(StringComparer.Ordinal);

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
        public void RegisterResource(McpResourceDefinition resource) => Resources[resource.Uri] = resource;

        /// <inheritdoc/>
        public void RegisterPrompt(McpPromptDefinition prompt) => Prompts[prompt.Name] = prompt;
    }

    #endregion
}
