namespace ZettaTrade.App.Tests.Services;

using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ZettaTrade.App.Services;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// Risk gate tests. The hard cap must reject regardless of confirmation,
/// oversize (below cap) orders must require human confirmation, and an
/// order with no anchor to estimate its value from must be rejected
/// explicitly — never slipped through, never silently confirmed.
/// </summary>
public sealed class DefaultRiskManagerTests
{
    /// <summary>Hard cap from <see cref="DefaultRiskManager"/>; must reject, never confirm.</summary>
    private const decimal HardCapUsd = 100_000m;

    /// <summary>Confirmation threshold from <see cref="DefaultRiskManager"/>.</summary>
    private const decimal ConfirmationThresholdUsd = 10_000m;

    private static DefaultRiskManager CreateManager() =>
        new(NullLogger<DefaultRiskManager>.Instance);

    private static OrderRequest LimitOrder(decimal quantity, decimal price) => new()
    {
        Symbol = Symbol.Parse("BTC/USDT"),
        Side = OrderSide.Buy,
        Type = OrderType.Limit,
        Quantity = new Quantity(quantity),
        Price = new Price(price),
    };

    private static OrderRequest StopMarketOrder(decimal quantity, decimal stopPrice) => new()
    {
        Symbol = Symbol.Parse("BTC/USDT"),
        Side = OrderSide.Sell,
        Type = OrderType.StopMarket,
        Quantity = new Quantity(quantity),
        StopPrice = new Price(stopPrice),
    };

    [Fact]
    public async Task CheckOrderAsync_AboveHardCap_RejectsUnconditionally()
    {
        // Arrange: 2 BTC at 90_000 = 180_000 USD, far above confirmation level.
        var manager = CreateManager();
        var request = LimitOrder(2m, 90_000m);

        // Act
        var result = await manager.CheckOrderAsync(request);

        // Assert
        result.IsApproved.Should().BeFalse();
        result.RequiresConfirmation.Should().BeFalse();
        result.Reason.Should().Contain("hard cap");
    }

    [Fact]
    public async Task CheckOrderAsync_JustAboveHardCap_Rejects()
    {
        // Boundary probe from above: cap + smallest step must still reject.
        var result = await CreateManager().CheckOrderAsync(
            LimitOrder(0.01m, HardCapUsd / 0.01m + 1_000m));

        result.IsApproved.Should().BeFalse();
    }

    [Fact]
    public async Task CheckOrderAsync_AboveConfirmationThreshold_ApprovesWithConfirmation()
    {
        // Arrange: 0.3 BTC at 50_000 = 15_000 USD: above confirmation,
        // below the hard cap.
        var result = await CreateManager().CheckOrderAsync(
            LimitOrder(0.3m, 50_000m));

        result.IsApproved.Should().BeTrue();
        result.RequiresConfirmation.Should().BeTrue();
    }

    [Fact]
    public async Task CheckOrderAsync_ExactThresholdValue_Approves()
    {
        // 1 BTC at exactly 10_000 USD (= the threshold) stays inside the
        // strict ">" rule: confirmed without extra confirmation.
        var result = await CreateManager().CheckOrderAsync(
            LimitOrder(1m, ConfirmationThresholdUsd));

        result.IsApproved.Should().BeTrue();
        result.RequiresConfirmation.Should().BeFalse();
    }

    /// <summary>
    /// BUG-026: a market order carries neither a limit price nor a trigger,
    /// so its value cannot be estimated from the request. The pre-fix
    /// manager approved it with a required confirmation — an order of ANY
    /// size never met the hard cap. Risk cannot be evaluated: the order is
    /// rejected explicitly, fail-closed.
    /// </summary>
    [Fact]
    public async Task CheckOrderAsync_NoPriceAnchor_RejectsWithCannotEvaluateRisk()
    {
        var marketOrder = LimitOrder(0.01m, 50_000m) with { Type = OrderType.Market, Price = null };

        var result = await CreateManager().CheckOrderAsync(marketOrder);

        result.IsApproved.Should().BeFalse(
            "an order of any size must never bypass the hard cap on confirmation alone");
        result.RequiresConfirmation.Should().BeFalse("a rejected order reaches no dialog");
        result.Reason.Should().Contain("cannot evaluate risk");
    }

    /// <summary>
    /// The stop trigger prices the order: a StopMarket is valued against
    /// ITS OWN trigger, so the cap matrix applies to it exactly as to a
    /// limit order. Case: inside the confirmation band (15_000 USD).
    /// </summary>
    [Fact]
    public async Task CheckOrderAsync_StopMarket_ValuedAgainstTheStopTrigger_InsideTheConfirmationBand()
    {
        var result = await CreateManager().CheckOrderAsync(StopMarketOrder(0.3m, 50_000m));

        result.IsApproved.Should().BeTrue("the stop order's value was estimated: 15_000 USD");
        result.RequiresConfirmation.Should().BeTrue();
    }

    /// <summary>
    /// The stop matrix's reject case: 0.5 BTC with the trigger at 300_000
    /// is 150_000 USD — above the hard cap, rejected-unconditionally even
    /// though no limit price exists on the request.
    /// </summary>
    [Fact]
    public async Task CheckOrderAsync_StopMarket_AboveTheHardCap_RejectsUnconditionally()
    {
        var result = await CreateManager().CheckOrderAsync(StopMarketOrder(0.5m, 300_000m));

        result.IsApproved.Should().BeFalse();
        result.RequiresConfirmation.Should().BeFalse();
        result.Reason.Should().Contain("hard cap");
    }
}
