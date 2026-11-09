namespace ZettaTrade.Soak;

using System.Text.Json.Serialization;

/// <summary>
/// One process sample the soak driver takes on its fixed cadence: the working
/// set and the private memory in megabytes, optional per-generation GC counts
/// and CPU percentage (null when the lane cannot observe that counter).
/// The driver writes the series as JSON; this record is the contract.
/// </summary>
public sealed record SoakSample(
    [property: JsonPropertyName("ElapsedSeconds")] double ElapsedSeconds,
    [property: JsonPropertyName("WorkingSetMb")] double WorkingSetMb,
    [property: JsonPropertyName("PrivateMb")] double PrivateMb,
    [property: JsonPropertyName("Gen0")] long? Gen0 = null,
    [property: JsonPropertyName("Gen1")] long? Gen1 = null,
    [property: JsonPropertyName("Gen2")] long? Gen2 = null,
    [property: JsonPropertyName("CpuPercent")] double? CpuPercent = null);
