namespace ZettaTrade.Soak;

using System.Text.Json.Serialization;

/// <summary>
/// The distribution of one memory metric the soak sampled: the mean, the
/// extreme and the high tail the budget answers to.
/// </summary>
public sealed record SoakStats(
    [property: JsonPropertyName("Mean")] double Mean,
    [property: JsonPropertyName("Max")] double Max,
    [property: JsonPropertyName("P95")] double P95,
    [property: JsonPropertyName("P99")] double P99);
