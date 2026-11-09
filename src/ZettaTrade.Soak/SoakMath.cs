namespace ZettaTrade.Soak;

/// <summary>
/// The soak run's pure analysis: distributions over the memory series, the
/// leak slopes over the run and the budget verdict assembly. No I/O, no
/// clock — the tool's entry point feeds it facts and renders the summary.
/// </summary>
public static class SoakMath
{
    /// <summary>The millisecond count of one hour (slope units, seconds to hours).</summary>
    private const double SecondsPerHour = 3600.0;

    /// <summary>
    /// The distribution of one sampled series: the arithmetic mean, the
    /// extreme and the high tail (P95, P99) by linear interpolation between
    /// the order statistics — never an inflated rank.
    /// </summary>
    public static SoakStats Stats(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
        {
            throw new ArgumentException("Cannot stat an empty series.", nameof(values));
        }

        var sorted = values.OrderBy(v => v).ToArray();

        return new SoakStats(
            Mean: Mean(sorted),
            Max: sorted[^1],
            P95: Percentile(sorted, 0.95),
            P99: Percentile(sorted, 0.99));
    }

    /// <summary>The least-squares slope of one series across the run, in MB per hour.</summary>
    public static double SlopePerHour(IReadOnlyList<SoakSample> samples, Func<SoakSample, double> value)
    {
        if (samples.Count < 2)
        {
            return 0.0;
        }

        var meanX = samples.Average(s => s.ElapsedSeconds);
        var meanY = samples.Average(s => value(s));

        double covariance = 0.0;
        double variance = 0.0;
        foreach (var sample in samples)
        {
            covariance += (sample.ElapsedSeconds - meanX) * (value(sample) - meanY);
            variance += Math.Pow(sample.ElapsedSeconds - meanX, 2);
        }

        return variance <= 0.0
            ? 0.0
            : covariance / variance * SecondsPerHour;
    }

    /// <summary>One budget's verdict: at-or-under the threshold passes.</summary>
    public static SoakVerdict Verdict(string name, double observed, double threshold) =>
        new(Name: name, Observed: observed, Threshold: threshold, Ok: observed <= threshold);

    /// <summary>
    /// The full summary assembly: distributions, slopes, the GC tally (a
    /// per-generation delta over the run when the driver sampled the
    /// counters), the cold start and the budget verdicts. A run without
    /// samples gets no verdicts and fails the overall call loudly — a
    /// silently green soak would be a false proof.
    /// </summary>
    public static SoakSummary Summarize(SoakSamples run, SoakBudgets budgets, string generatedAt, string toolStatus = "ok")
    {
        var workingSet = run.Samples.Select(s => s.WorkingSetMb).ToArray();
        var privateMb = run.Samples.Select(s => s.PrivateMb).ToArray();

        if (run.Samples.Count == 0)
        {
            return Empty(run, budgets, generatedAt, "input-error: no samples");
        }

        var workingSetStats = Stats(workingSet);
        var privateStats = Stats(privateMb);
        var gcTally = GcTally(run.Samples);

        var verdicts = new List<SoakVerdict>();

        if (budgets.MemoryMeanMb is { } meanLimit)
        {
            verdicts.Add(Verdict("MemoryMeanMb", privateStats.Mean, meanLimit));
        }

        if (budgets.MemoryPeakMb is { } peakLimit)
        {
            verdicts.Add(Verdict("MemoryPeakMb", privateStats.Max, peakLimit));
        }

        if (run.ColdStartMs is { } coldStart && budgets.ColdStartMs is { } coldLimit)
        {
            verdicts.Add(Verdict("ColdStartMs", coldStart, coldLimit));
        }

        return new SoakSummary
        {
            GeneratedAt = generatedAt,
            RunSeconds = run.RunSeconds,
            SampleCount = run.Samples.Count,
            WorkingSetMb = workingSetStats,
            PrivateMb = privateStats,
            WorkingSetSlopeMbPerHour = SlopePerHour(run.Samples, s => s.WorkingSetMb),
            PrivateSlopeMbPerHour = SlopePerHour(run.Samples, s => s.PrivateMb),
            Gc = gcTally,
            ColdStartMs = run.ColdStartMs,
            Verdicts = verdicts,
            OverallOk = verdicts.All(v => v.Ok),
            ToolStatus = toolStatus,
        };
    }

    /// <summary>The degenerate summary for an unusable run: nothing asserted, everything called false.</summary>
    private static SoakSummary Empty(SoakSamples run, SoakBudgets budgets, string generatedAt, string status)
    {
        var verdicts = new List<SoakVerdict>();

        if (budgets.MemoryMeanMb is { } meanLimit)
        {
            verdicts.Add(Verdict("MemoryMeanMb", double.PositiveInfinity, meanLimit));
        }

        if (budgets.MemoryPeakMb is { } peakLimit)
        {
            verdicts.Add(Verdict("MemoryPeakMb", double.PositiveInfinity, peakLimit));
        }

        if (run.ColdStartMs is { } coldStart && budgets.ColdStartMs is { } coldLimit)
        {
            verdicts.Add(Verdict("ColdStartMs", Math.Max(coldStart, coldLimit + 1.0), coldLimit));
        }

        return new SoakSummary
        {
            GeneratedAt = generatedAt,
            RunSeconds = run.RunSeconds,
            SampleCount = 0,
            WorkingSetMb = null,
            PrivateMb = null,
            WorkingSetSlopeMbPerHour = 0.0,
            PrivateSlopeMbPerHour = 0.0,
            Gc = new SoakGcTally(Sampled: false),
            ColdStartMs = run.ColdStartMs,
            Verdicts = verdicts,
            OverallOk = false,
            ToolStatus = status,
        };
    }

    /// <summary>The GC cohort delta over the run when the counters were sampled; an unsampled lane reports its limit.</summary>
    private static SoakGcTally GcTally(IReadOnlyList<SoakSample> samples)
    {
        if (samples.Any(s => s.Gen0 is null || s.Gen1 is null || s.Gen2 is null))
        {
            return new SoakGcTally(Sampled: false);
        }

        var first = samples[0];
        var last = samples[^1];

        return new SoakGcTally(
            Sampled: true,
            Gen0: last.Gen0!.Value - first.Gen0!.Value,
            Gen1: last.Gen1!.Value - first.Gen1!.Value,
            Gen2: last.Gen2!.Value - first.Gen2!.Value);
    }

    /// <summary>The arithmetic mean of one asc-sorted series.</summary>
    private static double Mean(double[] sorted) => sorted.Average();

    /// <summary>
    /// The linear-interpolation percentile over an asc-sorted series: the
    /// order statistic at integer ranks, between them the fractional blend.
    /// </summary>
    private static double Percentile(double[] sorted, double level)
    {
        var index = (sorted.Length - 1) * level;
        var lower = Math.Floor(index);
        var upper = Math.Ceiling(index);

        return (int)lower == (int)upper
            ? sorted[(int)index]
            : sorted[(int)lower] + (index - lower) * (sorted[(int)upper] - sorted[(int)lower]);
    }
}
