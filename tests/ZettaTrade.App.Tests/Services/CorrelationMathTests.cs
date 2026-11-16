namespace ZettaTrade.App.Tests.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Xunit;
using ZettaTrade.App.Services;
using ZettaTrade.Core.Entities;

/// <summary>
/// The correlation math's pins (T-252): the log returns key by the bar
/// open time (the honest alignment axis — two venues pair only where both
/// traded the bar), Pearson answers the honest dash when the pairing
/// cannot carry one (fewer than two pairs, a vanished variance), the
/// matrix pairs series over their SHARED keys with the overlap floor and
/// answers bit-identically on every recompute. Reference source:
/// hand-computed log ratios over crafted closes (the ratio 1.1 steps keep
/// every expectation a known constant, the varied mid-series break keeps
/// the stretches' variance alive).
/// </summary>
public sealed class CorrelationMathTests
{
    #region Helpers

    private static Bar Bar(int minutesFromEpoch, decimal close) => new()
    {
        Time = DateTimeOffset.UnixEpoch.AddMinutes(minutesFromEpoch),
        Open = close,
        High = close,
        Low = close,
        Close = close,
        Volume = 1m,
    };

    /// <summary>A series whose bars open every 15 minutes from the epoch.</summary>
    private static IReadOnlyList<Bar> Series(params decimal[] closes) => SeriesAt(0, closes);

    /// <summary>The same closes shifted <paramref name="barsLater"/> open slots later in time.</summary>
    private static IReadOnlyList<Bar> SeriesAt(int barsLater, decimal[] closes) =>
        [.. closes.Select((close, i) => Bar((i + barsLater) * 15, close))];

    private static Dictionary<DateTimeOffset, double> ReturnsOf(IReadOnlyList<Bar> series) =>
        CorrelationMath.LogReturns(series);

    /// <summary>
    /// The varied-closes craft: the ratios 1.1, 1.1, 100/121, 1.1, 1.1 —
    /// the mid-series break keeps the returns' variance alive (a constant
    /// ratio alone would read zero variance and dash every golden).
    /// </summary>
    private static readonly decimal[] VariedCloses = [100m, 110m, 121m, 100m, 110m, 121m];

    #endregion Helpers

    [Fact]
    public void LogReturns_AnAscendingSeries_AnswersTheLogRatiosKeyedByOpenTime()
    {
        // The closes 100 -> 110 -> 121: the ratios 1.1 each step, so every
        // return is the same known constant ln(1.1); the return stamps at
        // the LATER bar's open (bar 1 and bar 2 — the first bar carries no
        // prior close and no return).
        var returns = ReturnsOf(Series(100m, 110m, 121m));

        returns.Should().HaveCount(2);
        var times = returns.Keys.OrderBy(static key => key).ToArray();
        times[0].Should().Be(DateTimeOffset.UnixEpoch.AddMinutes(15));
        times[1].Should().Be(DateTimeOffset.UnixEpoch.AddMinutes(30));
        returns[times[0]].Should().BeApproximately(Math.Log(1.1), 1e-12);
        returns[times[1]].Should().BeApproximately(Math.Log(1.1), 1e-12);
    }

    [Fact]
    public void LogReturns_AShortOrFlatSeries_AnswersTheHonestShape()
    {
        // A one-bar series carries no return; a flat run carries zero
        // returns (a vanished variance — Pearson's later dash territory).
        CorrelationMath.LogReturns(Series(100m)).Should().BeEmpty();
        CorrelationMath.LogReturns(Series(100m, 100m, 100m)).Should()
            .OnlyContain(pair => pair.Value == 0.0);
    }

    [Fact]
    public void LogReturns_ANonPositiveClose_RefusesTheReturnInsteadOfThrowing()
    {
        // A zero close is corrupt data, not a price: the return refuses
        // (the key stays absent) — and it poisons BOTH adjacent returns
        // (bar 1's close and bar 2's prior close), so the whole series
        // answers empty. No exception, no fabricated return.
        var returns = CorrelationMath.LogReturns(Series(100m, 0m, 110m));

        returns.Should().BeEmpty();
    }

    [Fact]
    public void Pearson_PerfectlyAlignedStretches_AnswerThePerfectCoefficients()
    {
        var up = new[] { 0.1, 0.2, 0.3, 0.4 };
        var down = new[] { -0.1, -0.2, -0.3, -0.4 };
        var doubled = new[] { 0.2, 0.4, 0.6, 0.8 };

        CorrelationMath.Pearson(up, up)!.Value.Should().BeApproximately(1.0, 1e-9);
        CorrelationMath.Pearson(up, doubled)!.Value.Should()
            .BeApproximately(1.0, 1e-9, "a positive scaling leaves the coefficient untouched");
        CorrelationMath.Pearson(up, down)!.Value.Should().BeApproximately(-1.0, 1e-9);
    }

    [Fact]
    public void Pearson_ADegeneratePairing_IsTheHonestDash()
    {
        var up = new[] { 0.1, 0.2, 0.3 };

        // Fewer than two pairs never defines a coefficient.
        CorrelationMath.Pearson(up, up[..1]).Should().BeNull();

        // A zero-variance stretch (all-flat returns) dashes the same way.
        var flat = new[] { 0.0, 0.0, 0.0 };
        CorrelationMath.Pearson(up, flat).Should().BeNull("a flat run's denominator vanishes");
        CorrelationMath.Pearson(flat, flat).Should().BeNull();

        // Mismatched lengths refuse (the pairing is index-strict).
        CorrelationMath.Pearson(up, [.. up, 0.4]).Should().BeNull();
    }

    [Fact]
    public void Matrix_TheSharedKeysDriveThePairs_TheDiagonalIsTheSelfCorrelation()
    {
        // Series B squares A's closes: its returns double A's — the pair
        // correlates perfectly over the shared bar times. Series C (two
        // bars) owns a single return — its pairs dash below the floor and
        // its diagonal dashes (no self-correlation without variance).
        var seriesA = Series(VariedCloses);
        var seriesB = Series(VariedCloses.Select(static close => close * close).ToArray());
        var seriesC = Series(100m, 110m);

        var matrix = CorrelationMath.Matrix(
            [ReturnsOf(seriesA), ReturnsOf(seriesB), ReturnsOf(seriesC)],
            CorrelationMath.MinOverlapReturns(4));

        matrix.Rank.Should().Be(2);
        matrix[0, 1]!.Value.Should().BeApproximately(1.0, 1e-9, "B's returns double A's");
        matrix[1, 0]!.Value.Should().BeApproximately(matrix[0, 1]!.Value, 1e-9, "the pairing is symmetric");
        matrix[0, 0]!.Value.Should().BeApproximately(1.0, 1e-9, "a varied series self-correlates perfectly");
        matrix[1, 1]!.Value.Should().BeApproximately(1.0, 1e-9);
        matrix[0, 2].Should().BeNull("one shared return sits below the floor of two");
        matrix[2, 0].Should().BeNull();
        matrix[2, 2].Should().BeNull("a one-return series defines no self-correlation");
    }

    [Fact]
    public void Matrix_APartialOverlapBelowTheFloor_IsTheHonestDash()
    {
        // Series B continues A's own walk one open slot later (the same
        // closes, minus the head and tail bars): its returns repeat A's
        // EXACTLY over the shared stretch (three of five). A floor of
        // five dashes the pair; a floor of two answers the coefficient
        // over exactly the shared three.
        var seriesA = Series(VariedCloses);
        var seriesB = SeriesAt(1, VariedCloses.Skip(1).Take(4).ToArray());

        var tight = CorrelationMath.Matrix([ReturnsOf(seriesA), ReturnsOf(seriesB)], 5);
        tight[0, 1].Should().BeNull("three shared returns sit below the floor of five");

        var loose = CorrelationMath.Matrix([ReturnsOf(seriesA), ReturnsOf(seriesB)], 2);
        loose[0, 1].Should().NotBeNull("three shared returns clear the floor of two");
        loose[0, 1]!.Value.Should().BeApproximately(1.0, 1e-9, "the shared stretch repeats A's returns exactly");
    }

    [Fact]
    public void Matrix_TheSameInputs_AnswerBitIdentically()
    {
        var seriesA = Series(VariedCloses);
        var seriesB = Series(VariedCloses.Select(static close => close / 2m).ToArray());

        var first = CorrelationMath.Matrix([ReturnsOf(seriesA), ReturnsOf(seriesB)], 2);
        var second = CorrelationMath.Matrix([ReturnsOf(seriesA), ReturnsOf(seriesB)], 2);

        first.Rank.Should().Be(2);
        for (var i = 0; i < 2; i++)
        {
            for (var j = 0; j < 2; j++)
            {
                first[i, j].Should().Be(second[i, j], "the pinned summation order recomputes bit-identically");
            }
        }
    }

    [Fact]
    public void MinOverlapReturns_AShortWindow_ClampsToPearsonMinimum()
    {
        CorrelationMath.MinOverlapReturns(1).Should().Be(2, "Pearson's own two-pair floor");
        CorrelationMath.MinOverlapReturns(4).Should().Be(2);
        CorrelationMath.MinOverlapReturns(100).Should().Be(50, "half the window");
    }
}
