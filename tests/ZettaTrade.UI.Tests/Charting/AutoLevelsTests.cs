namespace ZettaTrade.UI.Tests.Charting;

using System;
using System.Linq;
using FluentAssertions;
using Xunit;
using ZettaTrade.Charting;
using ZettaTrade.Core.Entities;

/// <summary>
/// Pins for the automatic S/R levels (T-249): the tolerance join, the
/// anchor-based (no-chaining) boundary, the exact-mean level, the
/// ascending output, the weights and the deterministic series-scoped
/// compute the pan stability rides. Pure facts — no feed, no canvas.
/// </summary>
public sealed class AutoLevelsTests
{
    /// <summary>The base time the crafted pivots' bars ascend from (minute steps).</summary>
    private static readonly DateTimeOffset Base = new(2026, 3, 2, 0, 0, 0, TimeSpan.Zero);

    /// <summary>A swing-high pivot at a price (the kind never enters the clustering — highs and lows cluster alike).</summary>
    private static SwingPivot Pivot(int index, decimal price, int strength = 1) =>
        new(index, Base.AddMinutes(index), price, SwingPivotKind.High, strength);

    /// <summary>
    /// The tolerance join: pivots within the tolerance of the cluster's
    /// anchor merge into ONE level at the members' exact mean; a pivot
    /// beyond the band seeds its own cluster.
    /// </summary>
    [Fact]
    public void Compute_PivotsWithinTheAnchorBand_JoinIntoOneLevel()
    {
        // The band around the anchor 100 at 0.005 reaches 100.5: both
        // followers join; the third pivot (101) sits beyond and seeds its
        // own level.
        var levels = SupportResistanceLevels.Compute(
            [Pivot(0, 100m), Pivot(1, 100.4m), Pivot(2, 100.5m), Pivot(3, 101m)],
            0.005m);

        levels.Levels.Should().HaveCount(2);
        levels.Levels[0].Price.Should().Be((100m + 100.4m + 100.5m) / 3m);
        levels.Levels[1].Price.Should().Be(101m);
    }

    /// <summary>
    /// The no-chaining pin: the tolerance speaks against the cluster's
    /// ANCHOR, never the previous member — a slow drift of mutually
    /// close pivots cannot walk a cluster across the whole axis.
    /// </summary>
    [Fact]
    public void Compute_DriftingPivots_NeverChain()
    {
        // 100.45 joins the anchor (100 * 1.005 = 100.5) but 100.9 would
        // ONLY join by chaining through 100.45 (100.45 * 1.005 = 100.95) —
        // the anchor's band refuses it.
        var levels = SupportResistanceLevels.Compute(
            [Pivot(0, 100m), Pivot(1, 100.45m), Pivot(2, 100.9m)],
            0.005m);

        levels.Levels.Should().HaveCount(2);
        levels.Levels[0].Price.Should().Be((100m + 100.45m) / 2m);
        levels.Levels[1].Price.Should().Be(100.9m);
    }

    /// <summary>
    /// The weights: a level's weight is its members' strengths summed —
    /// more or more-dominant pivots make a heavier level; a lone pivot's
    /// level answers its own strength.
    /// </summary>
    [Fact]
    public void Compute_ClusterWeight_SumsTheMemberStrengths()
    {
        var levels = SupportResistanceLevels.Compute(
            [Pivot(0, 100m, 1), Pivot(1, 100.2m, 2), Pivot(2, 100.4m, 3), Pivot(3, 110m, 4)],
            0.005m);

        levels.Levels.Should().HaveCount(2);
        levels.Levels[0].Weight.Should().Be(6);
        levels.Levels[1].Weight.Should().Be(4);
    }

    /// <summary>
    /// The output stays ascending by price regardless of the input's
    /// order, and the empty input answers the honest empty set.
    /// </summary>
    [Fact]
    public void Compute_OutputAscending_AndEmptyHonest()
    {
        var levels = SupportResistanceLevels.Compute(
            [Pivot(0, 110m), Pivot(1, 100m), Pivot(2, 90m)],
            0.005m);

        levels.Levels.Select(l => l.Price).Should().Equal([90m, 100m, 110m]);

        SupportResistanceLevels.Compute([], 0.005m).Levels.Should().BeEmpty();
    }

    /// <summary>
    /// The tolerance clamps at zero: a negative tolerance never merges
    /// distinct pivots — each stands as its own level (at its own price,
    /// since a one-member cluster's mean is the member itself).
    /// </summary>
    [Fact]
    public void Compute_NegativeTolerance_ClampsToEachPivotItsOwn()
    {
        var levels = SupportResistanceLevels.Compute(
            [Pivot(0, 100m), Pivot(1, 100.4m)],
            -1m);

        levels.Levels.Select(l => l.Price).Should().Equal([100m, 100.4m]);
    }

    /// <summary>
    /// The pan-stability pin: the same series answers the same levels on
    /// every recompute (the level records compare by value), and the
    /// FromBars composition answers exactly the detect-then-cluster
    /// pipeline — the visible window never enters the compute.
    /// </summary>
    [Fact]
    public void FromBars_SeriesScoped_IsDeterministic()
    {
        // A crafted series: two dominance-heavy peaks (50 and 50.2 — one
        // cluster under the 0.5% band), a mid shoulder (47) and a deep
        // valley (40), the tails far from every extreme.
        var prices = new[]
        {
            44m, 45m, 46m, 50m, 47m, 46m, 45m, 44m, 43m, 42m,
            41m, 40m, 41m, 42m, 43m, 44m, 46m, 50.2m, 48m, 46m,
            45m, 45m,
        };
        var bars = prices.Select((p, i) => new Bar(
            Base.AddMinutes(i), p, p, p, p, 1m)).ToArray();

        var composed = SupportResistanceLevels.FromBars(bars, lookback: 2, minStrength: 1, tolerance: 0.005m);
        var pipeline = SupportResistanceLevels.Compute(
            SwingPivots.Detect(bars, 2, 1),
            0.005m);
        var again = SupportResistanceLevels.FromBars(bars, lookback: 2, minStrength: 1, tolerance: 0.005m);

        composed.Levels.Should().Equal(pipeline.Levels, "the composition IS the pipeline");
        again.Levels.Should().Equal(composed.Levels, "the same series recomputes to the same levels");
        composed.Levels.Should().NotBeEmpty();
    }
}
