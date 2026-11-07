namespace ZettaTrade.App.Tests.Services;

using FluentAssertions;
using Xunit;
using ZettaTrade.App.Services;

/// <summary>
/// Golden tests of the position sizing math: the floor-onto-step rounding
/// policy (the deliberate direction — proposals never overshoot a balance
/// or a risk budget), the balance-share presets (base custody for sells,
/// quote-converted for buys) and the risk-percent proposal. Every invalid
/// input is an error, never a substituted default.
/// </summary>
public sealed class PositionSizingMathTests
{
    [Fact]
    public void FloorToStep_OnExactStepMultiples_StaysExact()
    {
        PositionSizingMath.FloorToStep(0.35m, 0.01m)
            .Should().Be(0.35m);

        PositionSizingMath.FloorToStep(2m, 0.5m)
            .Should().Be(2m);
    }

    [Fact]
    public void FloorToStep_BetweenSteps_FloorsDown()
    {
        PositionSizingMath.FloorToStep(0.12345m, 0.00001m)
            .Should().Be(0.12345m);

        PositionSizingMath.FloorToStep(0.99999m, 0.01m)
            .Should().Be(0.99m);

        PositionSizingMath.FloorToStep(1.749m, 1m)
            .Should().Be(1m);
    }

    [Fact]
    public void FloorToStep_NeverExceedsTheRawValue()
    {
        PositionSizingMath.FloorToStep(105.15m, 0.01m)
            .Should().Be(105.15m);

        // The invariant the policy owns: 0 ≤ result ≤ raw, raw − result < step.
        var raw = 0.1m;
        var step = 0.03m;
        var floored = PositionSizingMath.FloorToStep(raw, step);
        floored.Should().Be(0.09m, "0.1 divided by 0.03 floors to three steps, exactly 0.09");
        floored.Should().BeLessThanOrEqualTo(raw);
        (raw - floored).Should().BeLessThan(step);
    }

    [Fact]
    public void FloorToStep_ZeroRaw_StaysZero() =>
        PositionSizingMath.FloorToStep(0m, 0.01m).Should().Be(0m);

    [Fact]
    public void FloorToStep_NonPositiveStep_Throws() =>
        FluentActions.Invoking(() => PositionSizingMath.FloorToStep(1m, 0m))
            .Should().Throw<ArgumentOutOfRangeException>();

    [Fact]
    public void FloorToStep_NegativeRaw_Throws() =>
        FluentActions.Invoking(() => PositionSizingMath.FloorToStep(-1m, 0.01m))
            .Should().Throw<ArgumentOutOfRangeException>();

    [Fact]
    public void BalanceSellQuantity_IsAFractionOfTheCapturedBaseCustody()
    {
        PositionSizingMath.BalanceSellQuantity(0.8m, 0.25m)
            .Should().Be(0.2m);

        PositionSizingMath.BalanceSellQuantity(10m, 1m)
            .Should().Be(10m);
    }

    [Fact]
    public void BalanceBuyQuantity_ConvertsTheQuoteCustodyAtTheEntryPrice() =>
        PositionSizingMath.BalanceBuyQuantity(50_000m, 0.5m, 50_000m)
            .Should().Be(0.5m);

    /// <summary>
    /// Fractional prices matter: the conversion stays a decimal division,
    /// the preset keeps its share, not a rounded half.
    /// </summary>
    [Fact]
    public void BalanceBuyQuantity_WithFractionalPrice_KeepsDecimalPrecision() =>
        PositionSizingMath.BalanceBuyQuantity(1000m, 0.25m, 4.2m)
            .Should().Be(59.523809523809523809523809524m);

    [Fact]
    public void BalanceQuantities_RejectUnderdeterminedInputs()
    {
        FluentActions.Invoking(() => PositionSizingMath.BalanceSellQuantity(-1m, 0.5m))
            .Should().Throw<ArgumentOutOfRangeException>();

        FluentActions.Invoking(() => PositionSizingMath.BalanceSellQuantity(1m, 0m))
            .Should().Throw<ArgumentOutOfRangeException>();

        FluentActions.Invoking(() => PositionSizingMath.BalanceSellQuantity(1m, 1.5m))
            .Should().Throw<ArgumentOutOfRangeException>();

        FluentActions.Invoking(() => PositionSizingMath.BalanceBuyQuantity(1m, 0.5m, 0m))
            .Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(1000, 1, 50, 0.2)]        // 1 % of 1000 = 10 quote risked across a 50-unit distance
    [InlineData(1000, 0.5, 50, 0.1)]      // half a percent halves the proposal
    [InlineData(25000, 2, 125, 4)]        // 2 % of 25000 = 500 over a 125-unit distance
    public void RiskQuantity_RidesThePercentOfTheQuoteBalanceAcrossTheDistance(
        decimal availableQuote,
        decimal riskPercent,
        decimal stopDistance,
        decimal expected) =>
        PositionSizingMath.RiskQuantity(availableQuote, riskPercent, stopDistance)
            .Should().Be(expected);

    [Fact]
    public void RiskQuantity_RejectsUnderdeterminedInputs()
    {
        FluentActions.Invoking(() => PositionSizingMath.RiskQuantity(-1m, 1m, 1m))
            .Should().Throw<ArgumentOutOfRangeException>();

        FluentActions.Invoking(() => PositionSizingMath.RiskQuantity(1000m, 0m, 1m))
            .Should().Throw<ArgumentOutOfRangeException>();

        FluentActions.Invoking(() => PositionSizingMath.RiskQuantity(1000m, 1m, 0m))
            .Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ToProposalText_DropsOnlyTheStepScale_SpuriousZeros()
    {
        // The step's scale stands (0.00001 keeps five decimals), not a
        // bigger size — its trailing zeros are bookkeeping: drop them.
        PositionSizingMath.ToProposalText(0.20000m).Should().Be("0.2");
        PositionSizingMath.ToProposalText(59.52380m).Should().Be("59.5238");
        PositionSizingMath.ToProposalText(100.00000m).Should().Be("100");
    }

    [Fact]
    public void ToProposalText_KeepsExactAndInformationalDigits()
    {
        PositionSizingMath.ToProposalText(0.00001m).Should().Be("0.00001");
        PositionSizingMath.ToProposalText(8m).Should().Be("8");
        PositionSizingMath.ToProposalText(0.2m).Should().Be("0.2");
    }

    [Fact]
    public void ToProposalText_NegativeQuantity_Throws() =>
        FluentActions.Invoking(() => PositionSizingMath.ToProposalText(-0.00001m))
            .Should().Throw<ArgumentOutOfRangeException>();
}
