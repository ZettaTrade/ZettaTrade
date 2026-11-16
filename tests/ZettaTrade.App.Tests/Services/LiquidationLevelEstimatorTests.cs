namespace ZettaTrade.App.Tests.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Xunit;
using ZettaTrade.App.Services;
using ZettaTrade.Charting;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Infrastructure.Exchange;

/// <summary>
/// The liquidation-level estimator's goldens (T-243): the OI delta times
/// the bucket's close is the notional, the notional picks exactly one
/// bracket tier (the first cap that bounds it, the top-tier clamp past
/// every cap), the tier's leverage and margin scale the close into
/// <c>entry × (1 ∓ 1/L + MMR)</c>, and the entry-distribution assumption
/// holds — growth buckets only, side from the bucket's direction, flat and
/// shrinking buckets unattributed, honest gaps and stale alignments
/// contribute nothing.
/// </summary>
public sealed class LiquidationLevelEstimatorTests
{
    #region Constants

    private static readonly DateTimeOffset Base = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The fixture's bracket table (ascending by cap, chained floors).</summary>
    private static readonly LeverageBracket[] Brackets =
    [
        new(0m, 50_000m, 50m, 0.005m),
        new(50_000m, 250_000m, 20m, 0.01m),
        new(250_000m, 1_000_000m, 10m, 0.025m),
    ];

    #endregion

    #region Test Data

    private static Bar Bar(int index, decimal open, decimal close) => new(
        Base.AddMinutes(index),
        open,
        Math.Max(open, close),
        Math.Min(open, close),
        close,
        10m);

    #endregion

    #region The Hand-Computed Ladder

    [Fact]
    public void TheLadder_ComputesTheExactBands()
    {
        // The hand-worked ladder (OI in coin units):
        //   bar 1  up   102→103  delta +2      → notional   206   → T1 long:  103 × (1 − .02 + .005) = 101.455
        //   bar 2  down 103→101  delta +3      → notional   303   → T1 short: 101 × (1 + .02 − .005) = 102.515
        //   bar 3  flat 101→101  delta +3      → no side → unattributed
        //   bar 4  down 101→100  delta  0      → no growth → nothing
        //   bar 5  up   100→100.5 delta +1300  → notional 130 650 → T2 long:  100.5 × (1 − .05 + .01) = 96.48
        //   bar 6  down 100.5→100 delta +1000  → notional 100 000 → T2 short: 100 × (1 + .05 − .01) = 104
        //   bar 7  down 100→99   delta −1338   → shrinking → unattributed
        //   bar 8  down 99→98    delta +30 020 → notional 2 941 960 → past every cap → T3 clamp short: 98 × (1 + .1 − .025) = 105.35
        //   bar 9  down 98→97    delta  0      → nothing
        var bars = new[]
        {
            Bar(0, 100m, 102m),
            Bar(1, 102m, 103m),
            Bar(2, 103m, 101m),
            Bar(3, 101m, 101m),
            Bar(4, 101m, 100m),
            Bar(5, 100m, 100.5m),
            Bar(6, 100.5m, 100m),
            Bar(7, 100m, 99m),
            Bar(8, 99m, 98m),
            Bar(9, 98m, 97m),
        };
        decimal?[] openInterest = [10m, 12m, 15m, 18m, 18m, 1_318m, 2_318m, 980m, 31_000m, 31_000m];

        var overlay = LiquidationLevelEstimator.Compute(bars, Brackets, openInterest);

        overlay.State.Should().Be(LiquidationLevelsOverlayState.Estimated);
        overlay.Bands.Select(b => (b.Price, b.Notional, b.Side)).Should().BeEquivalentTo(
            [
                (96.48m, 130_650m, LiquidationSide.Long),
                (101.455m, 206m, LiquidationSide.Long),
                (102.515m, 303m, LiquidationSide.Short),
                (104m, 100_000m, LiquidationSide.Short),
                (105.35m, 2_941_960m, LiquidationSide.Short),
            ]);
    }

    #endregion

    #region Tier Boundaries

    [Fact]
    public void TheTierBoundaries_PickTheExactTiers()
    {
        // All buckets are up and close at 100 (an entry-at-close bucket):
        //   delta   500 → notional    50 000 → T1 (cap ≥ notional): 100 × (1 − .02 + .005) = 98.5
        //   delta  2500 → notional   250 000 → T2: 100 × (1 − .05 + .01) = 96
        //   delta 10 000 → notional 1 000 000 → T3: 100 × (1 − .1 + .025) = 92.5
        //   delta 10 001 → notional 1 000 100 → past every cap → the T3 clamp: 92.5
        var bars = new[]
        {
            Bar(0, 99m, 99.5m),
            Bar(1, 99m, 100m),
            Bar(2, 99m, 100m),
            Bar(3, 99m, 100m),
            Bar(4, 99m, 100m),
        };
        decimal?[] openInterest = [0m, 500m, 3_000m, 13_000m, 23_001m];

        var overlay = LiquidationLevelEstimator.Compute(bars, Brackets, openInterest);

        overlay.State.Should().Be(LiquidationLevelsOverlayState.Estimated);
        overlay.Bands.Select(b => (b.Price, b.Notional)).Should().BeEquivalentTo(
            [
                (92.5m, 1_000_100m),
                (92.5m, 1_000_000m),
                (96m, 250_000m),
                (98.5m, 50_000m),
            ]);
    }

    #endregion

    #region Honesty States

    [Fact]
    public void NoBrackets_IsTheExplicitAbsence()
    {
        var overlay = LiquidationLevelEstimator.Compute(
            [Bar(0, 100m, 101m), Bar(1, 101m, 102m)],
            null,
            [10m, 12m]);

        overlay.State.Should().Be(LiquidationLevelsOverlayState.NoBrackets);
        overlay.Bands.Should().BeEmpty();

        var empty = LiquidationLevelEstimator.Compute(
            [Bar(0, 100m, 101m), Bar(1, 101m, 102m)],
            [],
            [10m, 12m]);

        empty.State.Should().Be(LiquidationLevelsOverlayState.NoBrackets);
        empty.Bands.Should().BeEmpty();
    }

    [Fact]
    public void AStaleAlignment_ContributesNothing()
    {
        var overlay = LiquidationLevelEstimator.Compute(
            [Bar(0, 100m, 101m), Bar(1, 101m, 102m)],
            Brackets,
            [10m, 12m, 15m]);

        overlay.State.Should().Be(LiquidationLevelsOverlayState.NoDeltas);
        overlay.Bands.Should().BeEmpty();
    }

    [Fact]
    public void AnEmptySeries_IsNoDeltas()
    {
        var overlay = LiquidationLevelEstimator.Compute([], Brackets, []);

        overlay.State.Should().Be(LiquidationLevelsOverlayState.NoDeltas);
        overlay.Bands.Should().BeEmpty();
    }

    [Fact]
    public void FlatOpenInterest_IsNoDeltas()
    {
        var bars = new[] { Bar(0, 100m, 101m), Bar(1, 101m, 102m), Bar(2, 102m, 103m) };

        var overlay = LiquidationLevelEstimator.Compute(bars, Brackets, [10m, 10m, 10m]);

        overlay.State.Should().Be(LiquidationLevelsOverlayState.NoDeltas);
        overlay.Bands.Should().BeEmpty();
    }

    [Fact]
    public void AnHonestGap_BreaksOnlyThePairsAcrossIt()
    {
        // The null slot is the alignment's gap: the pair BEFORE the gap
        // stands (bar 1: delta 2 × close 102 = 204 → T1 long: 102 × 0.985 =
        // 100.47), the two pairs ACROSS the null have no second standing
        // value and contribute nothing.
        var bars = new[]
        {
            Bar(0, 100m, 101m),
            Bar(1, 101m, 102m),
            Bar(2, 102m, 103m),
            Bar(3, 103m, 104m),
        };
        decimal?[] openInterest = [10m, 12m, null, 15m];

        var overlay = LiquidationLevelEstimator.Compute(bars, Brackets, openInterest);

        overlay.State.Should().Be(LiquidationLevelsOverlayState.Estimated);
        overlay.Bands.Should().ContainSingle();
        overlay.Bands[0].Price.Should().Be(100.47m);
        overlay.Bands[0].Notional.Should().Be(204m);
        overlay.Bands[0].Side.Should().Be(LiquidationSide.Long);
    }

    [Fact]
    public void AFlatPriceBucket_IsUnattributed()
    {
        var bars = new[] { Bar(0, 100m, 100m), Bar(1, 100m, 100m) };

        var overlay = LiquidationLevelEstimator.Compute(bars, Brackets, [10m, 20m]);

        overlay.State.Should().Be(LiquidationLevelsOverlayState.NoDeltas);
        overlay.Bands.Should().BeEmpty();
    }

    [Fact]
    public void AShrinkingBucket_IsUnattributed()
    {
        var bars = new[] { Bar(0, 100m, 99m), Bar(1, 99m, 98m) };

        var overlay = LiquidationLevelEstimator.Compute(bars, Brackets, [20m, 10m]);

        overlay.State.Should().Be(LiquidationLevelsOverlayState.NoDeltas);
        overlay.Bands.Should().BeEmpty();
    }

    [Fact]
    public void TheWarmupHead_ContributesNoDelta()
    {
        // One bar: no previous OI to differ against — the first slot's
        // delta is unknowable, never a zero.
        var overlay = LiquidationLevelEstimator.Compute([Bar(0, 100m, 101m)], Brackets, [10m]);

        overlay.State.Should().Be(LiquidationLevelsOverlayState.NoDeltas);
        overlay.Bands.Should().BeEmpty();
    }

    #endregion

    #region Degenerate Inputs

    [Fact]
    public void AZeroFloorTier_IsSkippedNotFabricated()
    {
        // L = 1 with a zero margin rate floors the long multiplier at
        // exactly zero: the "level" would be the zero price — skipped.
        var brackets = new[] { new LeverageBracket(0m, 50_000m, 1m, 0m) };

        var overlay = LiquidationLevelEstimator.Compute(
            [Bar(0, 100m, 101m), Bar(1, 101m, 102m)],
            brackets,
            [10m, 12m]);

        overlay.State.Should().Be(LiquidationLevelsOverlayState.NoDeltas);
        overlay.Bands.Should().BeEmpty();
    }

    [Fact]
    public void OnlyDegenerateTiers_IsNoEstimate()
    {
        var brackets = new[] { new LeverageBracket(0m, 50_000m, 0m, 0.005m) };

        var overlay = LiquidationLevelEstimator.Compute(
            [Bar(0, 100m, 101m), Bar(1, 101m, 102m)],
            brackets,
            [10m, 12m]);

        overlay.State.Should().Be(LiquidationLevelsOverlayState.NoDeltas);
        overlay.Bands.Should().BeEmpty();
    }

    #endregion

    #region The Band Cap

    [Fact]
    public void TheBandCap_KeepsTheLargestNotional()
    {
        // 40 qualifying buckets: the cap keeps 32, and the smallest-notional
        // 8 stay off the pane.
        var bars = new Bar[LiquidationLevelEstimator.MaxBands + 8];
        decimal?[] openInterest = new decimal?[bars.Length];
        openInterest[0] = 0m;
        for (var i = 1; i < bars.Length; i++)
        {
            // Every bucket up 100→101 with a growth of one coin: the
            // notional rides the bucket's index so the tail is the smallest.
            bars[i] = Bar(i, 100m + i, 101m + i);
            openInterest[i] = openInterest[i - 1] + i;
        }

        var overlay = LiquidationLevelEstimator.Compute(bars, Brackets, openInterest);

        overlay.State.Should().Be(LiquidationLevelsOverlayState.Estimated);
        overlay.Bands.Should().HaveCount(LiquidationLevelEstimator.MaxBands);
        overlay.Bands.Select(b => b.Notional).Min().Should()
            .BeGreaterThan(openInterest.Last()!.Value - openInterest[^2]!.Value, "the dropped tail is the smallest growth");
        overlay.Bands.Select(b => b.Price).Should().BeInAscendingOrder();
    }

    #endregion

    #region Ordering

    [Fact]
    public void APriceTie_RanksTheLargerNotionalFirst()
    {
        // Two buckets compute the same long level 101 × 0.985 = 99.485 with
        // different notionals; the tie ranks the larger first.
        var bars = new[]
        {
            Bar(0, 100m, 100m),
            Bar(1, 100m, 101m),
            Bar(2, 100m, 101m),
        };
        decimal?[] openInterest = [0m, 1m, 3m];

        var overlay = LiquidationLevelEstimator.Compute(bars, Brackets, openInterest);

        overlay.State.Should().Be(LiquidationLevelsOverlayState.Estimated);
        overlay.Bands.Should().HaveCount(2);
        overlay.Bands[0].Price.Should().Be(99.485m);
        overlay.Bands[0].Notional.Should().Be(202m);
        overlay.Bands[0].Side.Should().Be(LiquidationSide.Long);
        overlay.Bands[1].Price.Should().Be(99.485m);
        overlay.Bands[1].Notional.Should().Be(101m);
        overlay.Bands[1].Side.Should().Be(LiquidationSide.Long);
    }

    #endregion
}
