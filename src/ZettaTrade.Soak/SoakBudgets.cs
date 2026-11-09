namespace ZettaTrade.Soak;

using System.Text.Json.Serialization;

/// <summary>
/// The budgets a soak run answers to: the performance targets from AGENTS.md
/// (cold start under 3 s, typical memory under 500 MB) as plain data the
/// gate and the soak driver share. A key PRESENT in the file is enforced
/// (its verdict carries the run's exit code); a key absent or null is
/// advisory — the summary still reports the measurement, never the
/// enforcement. The committed <c>scripts/soak-budgets.json</c> holds the
/// landing numbers; <c>scripts/soak-ci-budgets.json</c> scopes the CI lane.
/// </summary>
public sealed record SoakBudgets
{
    /// <summary>The cold start budget in milliseconds; null = advisory only.</summary>
    [JsonPropertyName("ColdStartMs")]
    public double? ColdStartMs { get; init; } = null;

    /// <summary>The mean memory budget in megabytes; null = advisory only.</summary>
    [JsonPropertyName("MemoryMeanMb")]
    public double? MemoryMeanMb { get; init; } = null;

    /// <summary>The peak memory budget in megabytes; null = advisory only.</summary>
    [JsonPropertyName("MemoryPeakMb")]
    public double? MemoryPeakMb { get; init; } = null;
}
