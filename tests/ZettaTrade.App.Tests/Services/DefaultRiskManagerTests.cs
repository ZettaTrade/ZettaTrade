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
/// oversize (below cap) orders must require human confirmation, and market
/// orders without an explicit price must not slip through with a zero
/// no-confirmation estimate.
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

    [Fact]
    public async Task CheckOrderAsync_MarketOrderWithoutPrice_RequiresConfirmation()
    {
        // Market orders carry no explicit price: a zero estimate must never
        // silently bypass the human confirmation gate.
        var request = LimitOrder(0.01m, 50_000m) with { Type = OrderType.Market, Price = null };

        var result = await CreateManager().CheckOrderAsync(request);

        result.IsApproved.Should().BeTrue();
        result.RequiresConfirmation.Should().BeTrue();
    }
}
