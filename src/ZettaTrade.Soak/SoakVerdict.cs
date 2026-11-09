namespace ZettaTrade.Soak;

using System.Text.Json.Serialization;

/// <summary>
/// One budget verdict the summary carries: the budget's name, the observed
/// value, the threshold it answers to and the call (at-or-under passes).
/// </summary>
public sealed record SoakVerdict(
    [property: JsonPropertyName("Name")] string Name,
    [property: JsonPropertyName("Observed")] double Observed,
    [property: JsonPropertyName("Threshold")] double Threshold,
    [property: JsonPropertyName("Ok")] bool Ok);
