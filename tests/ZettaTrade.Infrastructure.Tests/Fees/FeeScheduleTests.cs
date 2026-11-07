namespace ZettaTrade.Infrastructure.Tests.Fees;

using Core.Enums;
using FluentAssertions;
using Infrastructure.Fees;
using Xunit;

/// <summary>
/// Goldens pinning the fee schedule's rounding policy (T-080): the raw
/// notional × rate/10 000 product rounds HALF-AWAY-FROM-ZERO at the eight
/// commission decimals, the minimum fee floors a computed fee — but only
/// once a fee was actually produced (a zero-rate fill never grows a fee out
/// of the floor), and the maker/taker branch selection reads the roles.
/// These tests are the rounding policy's canon: a change here is a stated
/// money-behavior change, never a silent drift.
/// </summary>
public sealed class FeeScheduleTests
{
    #region Selection

    [Fact]
    public void Commission_MakerAndTakerRoles_PriceDifferentBranches()
    {
        var schedule = new FeeSchedule(MakerRateBps: 10m, TakerRateBps: 20m, MinimumFee: 0m);

        schedule.Commission(FeeRole.Maker, NotionalOfTenThousand).Should().Be(10m);
        schedule.Commission(FeeRole.Taker, NotionalOfTenThousand).Should().Be(20m);
    }

    [Fact]
    public void RateBpsFor_MapsTheRoleOntoItsBranch()
    {
        var schedule = new FeeSchedule(MakerRateBps: 10m, TakerRateBps: 20m, MinimumFee: 0m);

        schedule.RateBpsFor(FeeRole.Maker).Should().Be(10m);
        schedule.RateBpsFor(FeeRole.Taker).Should().Be(20m);
    }

    #endregion

    #region Rounding policy

    [Fact]
    public void Commission_ExactHalfPoint_RoundsHalfAwayFromZero_AtTheCommissionDecimals()
    {
        // The raw product is exactly 0.000000025 — a true midpoint at the
        // ninth decimal digit: half-away-from-zero rounds it UP to
        // 0.00000003. Half-to-even would have kept the even 0.00000002, so
        // this golden cannot pass by accident.
        var schedule = new FeeSchedule(MakerRateBps: 10m, TakerRateBps: 20m, MinimumFee: 0m);

        schedule.Commission(FeeRole.Maker, 0.000025m).Should().Be(0.00000003m);
    }

    [Fact]
    public void Commission_RemainderPastTheNinthDigit_RoundsDown_NeverTruncates()
    {
        // raw = 0.0000000249999: the digits beyond the eighth decimal fall
        // short of the half point, so the value rounds down to
        // 0.00000002 — the full precision is kept; truncation would have
        // already settled on the same value only by dropping digits.
        var schedule = new FeeSchedule(MakerRateBps: 10m, TakerRateBps: 20m, MinimumFee: 0m);

        schedule.Commission(FeeRole.Maker, 0.0000249999m).Should().Be(0.00000002m);
    }

    [Fact]
    public void Commission_FractionPastTheHalfPoint_RoundsUp()
    {
        // raw = 0.20000011111111 — a taker 20 bps over the chosen notional;
        // the digits past the eighth decimal sit above the half point, so
        // the value rounds away (up).
        var schedule = new FeeSchedule(MakerRateBps: 10m, TakerRateBps: 20m, MinimumFee: 0m);

        schedule.Commission(FeeRole.Taker, 100.00005555555m).Should().Be(0.20000011m);
    }

    #endregion

    #region Minimum fee floor

    [Fact]
    public void Commission_AZeroRate_StaysFree_TheFloorNeverFabricatesACost()
    {
        var schedule = new FeeSchedule(MakerRateBps: 0m, TakerRateBps: 0m, MinimumFee: 25m);

        schedule.Commission(FeeRole.Maker, NotionalOfTenThousand).Should().Be(0m);
        schedule.Commission(FeeRole.Taker, NotionalOfTenThousand).Should().Be(0m);
    }

    [Fact]
    public void Commission_SmallComputedFee_LiftsToTheMinimum()
    {
        var schedule = new FeeSchedule(MakerRateBps: 10m, TakerRateBps: 20m, MinimumFee: 25m);

        // raw = 0.02 — computed, so the floor lifts it to the minimum.
        schedule.Commission(FeeRole.Taker, NotionalOfTenThousand).Should().Be(25m);
    }

    [Fact]
    public void Commission_ComputedFeeAboveTheMinimum_KeepsItsRoundedValue()
    {
        var schedule = new FeeSchedule(MakerRateBps: 10m, TakerRateBps: 20m, MinimumFee: 25m);

        // 1 000 000 notional at taker 20 bps computes 2 000, high above the
        // floor: the rounded product stands as-is.
        schedule.Commission(FeeRole.Taker, NotionalOfTenThousand * 100m).Should().Be(2_000m);
    }

    #endregion

    #region Estimation policy

    [Fact]
    public void ForOrderType_LimitsOfferLiquidity_EverythingElseConsumesIt()
    {
        FeeRoleEstimation.ForOrderType(OrderType.Limit).Should().Be(FeeRole.Maker);
        FeeRoleEstimation.ForOrderType(OrderType.Market).Should().Be(FeeRole.Taker);
        FeeRoleEstimation.ForOrderType(OrderType.StopMarket).Should().Be(FeeRole.Taker);
        FeeRoleEstimation.ForOrderType(OrderType.StopLimit).Should().Be(FeeRole.Taker);
        FeeRoleEstimation.ForOrderType(OrderType.TrailingStop).Should().Be(FeeRole.Taker);
    }

    #endregion

    #region Constants

    /// <summary>A round notional the goldens size against (0.2 base at 50 000).</summary>
    private const decimal NotionalOfTenThousand = 10_000m;

    #endregion
}
