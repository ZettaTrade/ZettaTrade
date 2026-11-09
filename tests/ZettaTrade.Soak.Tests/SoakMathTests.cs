namespace ZettaTrade.Soak.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Xunit;

/// <summary>
/// The soak math's own goldens (T-109): the distributions' exact values on
/// pinned series (the scale a soak report answers with), the per-hour leak
/// slope in both directions, the at-the-threshold verdict semantics, and
/// the full summary assembly over a manufactured run — including the loud
/// failure when the run carried no samples at all.
/// </summary>
public sealed class SoakMathTests
{
    [Fact]
    public void Stats_SingleSample_PinsEveryPoint()
    {
        var stats = SoakMath.Stats([42.0]);

        stats.Mean.Should().Be(42.0);
        stats.Max.Should().Be(42.0);
        stats.P95.Should().Be(42.0);
        stats.P99.Should().Be(42.0);
    }

    [Fact]
    public void Stats_KnownSeries_PinsTheHighTailExactly()
    {
        var stats = SoakMath.Stats([5.0, 1.0, 3.0, 2.0, 4.0]);

        stats.Mean.Should().Be(3.0);
        stats.Max.Should().Be(5.0);
        stats.P95.Should().BeApproximately(4.8, 1e-9);
        stats.P99.Should().BeApproximately(4.96, 1e-9);
    }

    [Fact]
    public void Stats_EmptySeries_ThrowsInsteadOfInventing()
    {
        var act = () => SoakMath.Stats([]);

        act.Should().Throw<ArgumentException>("a silently zero series would be a false proof.");
    }

    [Fact]
    public void Slope_FlatSeries_IsZero()
    {
        var slope = SoakMath.SlopePerHour(
            Samples(Enumerable.Range(0, 10).Select(i => (Seconds: i * 600.0, Value: 200.0))),
            s => s.WorkingSetMb);

        slope.Should().BeApproximately(0.0, 1e-9);
    }

    [Fact]
    public void Slope_TwoPoints_MapsSecondsToHours()
    {
        var slope = SoakMath.SlopePerHour(
            Samples([(Seconds: 0.0, Value: 100.0), (Seconds: 7200.0, Value: 140.0)]),
            s => s.PrivateMb);

        slope.Should().BeApproximately(20.0, 1e-9, "140 MB - 100 MB over 2 h = +20 MB/h.");
    }

    [Fact]
    public void Slope_DecliningSeries_IsNegative()
    {
        var slope = SoakMath.SlopePerHour(
            Samples([(Seconds: 0.0, Value: 100.0), (Seconds: 3600.0, Value: 80.0)]),
            s => s.PrivateMb);

        slope.Should().BeApproximately(-20.0, 1e-9);
    }

    [Fact]
    public void Slope_SingleSample_IsZero()
    {
        var slope = SoakMath.SlopePerHour(
            Samples([(Seconds: 0.0, Value: 100.0)]),
            s => s.WorkingSetMb);

        slope.Should().BeApproximately(0.0, 1e-9);
    }

    [Fact]
    public void Verdict_AtTheThreshold_Passes()
    {
        var verdict = SoakMath.Verdict("MemoryPeakMb", observed: 500.0, threshold: 500.0);

        verdict.Ok.Should().BeTrue("at-or-under is the budget's pass line.");
    }

    [Fact]
    public void Verdict_OverTheThreshold_Fails()
    {
        var verdict = SoakMath.Verdict("MemoryPeakMb", observed: 500.1, threshold: 500.0);

        verdict.Ok.Should().BeFalse();
    }

    [Fact]
    public void Summarize_ManufacturedRun_AssemblesTheFullSummary()
    {
        var run = new SoakSamples
        {
            GeneratedAt = "2026-11-10T12:00:00.0000000Z",
            RunSeconds = 3600.0,
            ColdStartMs = 2100.0,
            Samples = Samples([
                (Seconds: 0.0, Value: 100.0),
                (Seconds: 900.0, Value: 110.0),
                (Seconds: 1800.0, Value: 120.0),
                (Seconds: 2700.0, Value: 130.0),
                (Seconds: 3600.0, Value: 140.0),
            ], withGc: true),
        };
        var budgets = new SoakBudgets
        {
            ColdStartMs = 3000,
            MemoryMeanMb = 400,
            MemoryPeakMb = 500,
        };

        var summary = SoakMath.Summarize(run, budgets, generatedAt: "2026-11-10T13:00:00.0000000Z");

        summary.ToolStatus.Should().Be("ok");
        summary.SampleCount.Should().Be(5);
        summary.WorkingSetMb.Should().NotBeNull();
        summary.PrivateMb!.Mean.Should().Be(120.0);
        summary.PrivateMb!.Max.Should().Be(140.0);
        summary.WorkingSetSlopeMbPerHour.Should().BeApproximately(40.0, 1e-9);
        summary.Gc!
            .Sampled.Should()
            .BeTrue();

        summary.ColdStartMs.Should().NotBeNull();
        summary.ColdStartMs!.Value.Should().Be(2100.0);
        summary.Verdicts.Should().Contain(v => v.Name == "ColdStartMs" && v.Ok);
        summary.Verdicts.Should().Contain(v => v.Name == "MemoryMeanMb" && v.Observed == 120.0 && v.Ok);
        summary.OverallOk.Should().BeTrue();
    }

    [Fact]
    public void Summarize_RunOverBudget_FailsTheVerdictsAndTheOverall()
    {
        var run = new SoakSamples
        {
            GeneratedAt = "2026-11-10T12:00:00.0000000Z",
            RunSeconds = 60.0,
            ColdStartMs = 3500.0,
            Samples = Samples([(Seconds: 0.0, Value: 600.0), (Seconds: 60.0, Value: 610.0)]),
        };
        var budgets = new SoakBudgets
        {
            ColdStartMs = 3000,
            MemoryMeanMb = 400,
            MemoryPeakMb = 500,
        };

        var summary = SoakMath.Summarize(run, budgets, generatedAt: "2026-11-10T12:01:00.0000000Z");

        summary.Verdicts.Single(v => v.Name == "ColdStartMs").Ok.Should().BeFalse("3500 ms exceeds the 3000 ms budget.");
        summary.Verdicts.Single(v => v.Name == "MemoryPeakMb").Ok.Should().BeFalse("610 MB exceeds the 500 MB budget.");
        summary.OverallOk.Should().BeFalse();
    }

    [Fact]
    public void Summarize_AdvisoryBudget_PinsTheMeasurementWithoutEnforcingIt()
    {
        var run = new SoakSamples
        {
            GeneratedAt = "2026-11-10T12:00:00.0000000Z",
            RunSeconds = 60.0,
            ColdStartMs = 3500.0,
            Samples = Samples([(Seconds: 0.0, Value: 100.0), (Seconds: 60.0, Value: 110.0)]),
        };
        var budgets = new SoakBudgets
        {
            ColdStartMs = null,                        // advisory: the CI lane's shape
            MemoryMeanMb = 400,
            MemoryPeakMb = 500,
        };

        var summary = SoakMath.Summarize(run, budgets, generatedAt: "2026-11-10T12:01:00.0000000Z");

        summary.ColdStartMs.Should().NotBeNull("the measurement itself is always reported.");
        summary.Verdicts.Select(v => v.Name).Should().NotContain("ColdStartMs", "a null budget is advisory, never enforced");
        summary.OverallOk.Should().BeTrue("the memory verdicts hold; the advisory cold start does not gate.");
    }

    [Fact]
    public void Summarize_EmptyRun_FailsLoudlyWithNoSamples()
    {
        var run = new SoakSamples { GeneratedAt = "2026-11-10T12:00:00.0000000Z", RunSeconds = 60.0 };
        var budgets = new SoakBudgets { MemoryMeanMb = 400, MemoryPeakMb = 500 };

        var summary = SoakMath.Summarize(run, budgets, generatedAt: "2026-11-10T12:01:00.0000000Z");

        summary.SampleCount.Should().Be(0);
        summary.WorkingSetMb.Should().BeNull();
        summary.PrivateMb.Should().BeNull();
        summary.ToolStatus.Should().Be("input-error: no samples");
        summary.OverallOk.Should().BeFalse("an empty soak must fail loudly, never pass silently.");
        summary.Verdicts.All(v => v.Ok).Should().BeFalse("every verdict reports a failed observation.");
    }

    private static List<SoakSample> Samples(IEnumerable<(double Seconds, double Value)> points, bool withGc = false) =>
        points.Select((entry, index) => new SoakSample(
            ElapsedSeconds: entry.Seconds,
            WorkingSetMb: entry.Value,
            PrivateMb: entry.Value,
            Gen0: withGc ? 10 + index : null,
            Gen1: withGc ? 2 + index : null,
            Gen2: withGc ? index : null,
            CpuPercent: null)).ToList();
}
