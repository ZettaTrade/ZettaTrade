namespace ZettaTrade.Soak;

using System.Text.Json.Serialization;

/// <summary>
/// The per-generation GC tally over the run: the increase each generation's
/// collection count grew by (last sample minus the first). Sampled=false
/// when the lane could not observe the counters at all — a lane limit that
/// is reported, never invented.
/// </summary>
public sealed record SoakGcTally(
    [property: JsonPropertyName("Sampled")] bool Sampled,
    [property: JsonPropertyName("Gen0")] long Gen0 = 0,
    [property: JsonPropertyName("Gen1")] long Gen1 = 0,
    [property: JsonPropertyName("Gen2")] long Gen2 = 0);
