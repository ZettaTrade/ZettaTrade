namespace ZettaTrade.Soak;

using System.Text.Json.Serialization;

/// <summary>
/// The soak run's header: how long the driver ran, when it wrapped up, the
/// measured cold start (null when the driver did not measure one), and the
/// memory samples themselves.
/// </summary>
public sealed record SoakSamples
{
    /// <summary>When the samples file was written (ISO 8601, the driver's clock).</summary>
    [JsonPropertyName("GeneratedAt")]
    public string GeneratedAt { get; init; } = string.Empty;

    /// <summary>How long the driver kept the app alive, in seconds.</summary>
    [JsonPropertyName("RunSeconds")]
    public double RunSeconds { get; init; }

    /// <summary>The measured cold start in milliseconds; null when not measured.</summary>
    [JsonPropertyName("ColdStartMs")]
    public double? ColdStartMs { get; init; } = null;

    /// <summary>The samples, in chronological order.</summary>
    [JsonPropertyName("Samples")]
    public IReadOnlyList<SoakSample> Samples { get; init; } = [];
}
