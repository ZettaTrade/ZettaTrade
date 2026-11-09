namespace ZettaTrade.Soak;

using System.Text.Json.Serialization;

/// <summary>
/// The soak run's summary: the memory distributions, the leak slope over the
/// run, the GC tally, the cold start, the budget verdicts and the overall
/// call. The tool writes it as JSON; the driver and the docs page read it.
/// </summary>
public sealed record SoakSummary
{
    /// <summary>When the summary was assembled (ISO 8601).</summary>
    [JsonPropertyName("GeneratedAt")]
    public string GeneratedAt { get; init; } = string.Empty;

    /// <summary>How long the soak ran, in seconds (the driver's report).</summary>
    [JsonPropertyName("RunSeconds")]
    public double RunSeconds { get; init; }

    /// <summary>How many samples landed in the series.</summary>
    [JsonPropertyName("SampleCount")]
    public int SampleCount { get; init; }

    /// <summary>The working-set distribution over the run.</summary>
    [JsonPropertyName("WorkingSetMb")]
    public SoakStats? WorkingSetMb { get; init; } = null;

    /// <summary>The private-memory distribution over the run.</summary>
    [JsonPropertyName("PrivateMb")]
    public SoakStats? PrivateMb { get; init; } = null;

    /// <summary>The working set's growth slope over the run, in MB per hour.</summary>
    [JsonPropertyName("WorkingSetSlopeMbPerHour")]
    public double WorkingSetSlopeMbPerHour { get; init; }

    /// <summary>The private memory's growth slope over the run, in MB per hour.</summary>
    [JsonPropertyName("PrivateSlopeMbPerHour")]
    public double PrivateSlopeMbPerHour { get; init; }

    /// <summary>The per-generation GC tally over the run (unsampled when the lane could not observe it).</summary>
    [JsonPropertyName("Gc")]
    public SoakGcTally? Gc { get; init; } = null;

    /// <summary>The cold start measured for this run, in milliseconds (null when not measured).</summary>
    [JsonPropertyName("ColdStartMs")]
    public double? ColdStartMs { get; init; } = null;

    /// <summary>Every budget the run was held to, with its verdict.</summary>
    [JsonPropertyName("Verdicts")]
    public IReadOnlyList<SoakVerdict> Verdicts { get; init; } = [];

    /// <summary>Every verdict passed.</summary>
    [JsonPropertyName("OverallOk")]
    public bool OverallOk { get; init; }

    /// <summary>'ok' on a well-formed run; the input-error text otherwise.</summary>
    [JsonPropertyName("ToolStatus")]
    public string ToolStatus { get; init; } = "ok";
}
