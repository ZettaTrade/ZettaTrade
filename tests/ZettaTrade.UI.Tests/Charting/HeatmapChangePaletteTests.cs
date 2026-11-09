namespace ZettaTrade.UI.Tests.Charting;

using System.Globalization;
using FluentAssertions;
using SkiaSharp;
using Xunit;
using ZettaTrade.UI.Charting;

/// <summary>
/// Pins for the heatmap's tile coloring and caption text (T-121): the
/// neutral fill for unpriced and flat tiles, the channel blend toward the
/// bullish/bearish endpoint with the magnitude the number carries, the
/// saturation at <see cref="HeatmapChangePalette.FullSaturationPercent"/>
/// (never a stronger swing than the data), a sign the palette never
/// flips, and the caption's signed two-decimal percent shape.
/// </summary>
public sealed class HeatmapChangePaletteTests
{
    private const decimal Saturation = HeatmapChangePalette.FullSaturationPercent;

    /// <summary>A null change (the pair has not ticked) keeps the neutral fill.</summary>
    [Fact]
    public void ANullChange_KeepsTheNeutralFill()
    {
        HeatmapChangePalette.ColorFor(null, Neutral(), Up(), Down())
            .Should().Be(Neutral());
    }

    /// <summary>A zero change is a resting tile, neutral.</summary>
    [Fact]
    public void AZeroChange_KeepsTheNeutralFill()
    {
        HeatmapChangePalette.ColorFor(0m, Neutral(), Up(), Down())
            .Should().Be(Neutral());
    }

    /// <summary>A positive change blends toward the up endpoint by its magnitude.</summary>
    /// <remarks>
    /// The pinned literals assume the palette's own AwayFromZero rounding:
    /// (45 + 34) / 2 = 39.5 rounds to 40, (45 + 197) / 2 = 121 exactly,
    /// (60 + 94) / 2 = 77 exactly.
    /// </remarks>
    [Fact]
    public void APositiveChange_BlendsTowardTheUpEndpoint()
    {
        // Half of the saturation blends exactly halfway to the endpoint.
        HeatmapChangePalette.ColorFor(Saturation / 2m, Neutral(), Up(), Down())
            .Should().Be(new SKColor(40, 121, 77),
                "half saturation is the channels' AwayFromZero midpoint");
    }

    /// <summary>A negative change blends toward the down endpoint, never inverts its sign.</summary>
    [Fact]
    public void ANegativeChange_BlendsTowardTheDownEndpoint()
    {
        HeatmapChangePalette.ColorFor(-Saturation, Neutral(), Up(), Down())
            .Should().Be(Down(), "full magnitude of a losing change is the bearish endpoint");
    }

    /// <summary>The palette never invents a stronger swing than the number carries.</summary>
    [Fact]
    public void AFarBeyondSaturationChange_SaturatesAtTheEndpoint()
    {
        HeatmapChangePalette.ColorFor(Saturation + 40m, Neutral(), Up(), Down())
            .Should().Be(Up(), "the magnitude saturates at the pure endpoint");
    }

    /// <summary>Small magnitudes shade only barely away from the neutral.</summary>
    /// <remarks>(45 - 11 * 0.1 = 43.9 → 44; 45 + 152 * 0.1 = 60.2 → 60; 60 + 34 * 0.1 = 63.4 → 63.)</remarks>
    [Fact]
    public void ASmallGain_StylesOnlyABareShadeTowardGrowth()
    {
        HeatmapChangePalette.ColorFor(0.5m, Neutral(), Up(), Down())
            .Should().Be(new SKColor(44, 60, 63));
    }

    /// <summary>The caption is a signed two-decimal percent, explicit for gains.</summary>
    [Fact]
    public void TheCaption_IsASignedTwoDecimalPercent()
    {
        HeatmapTileCaptions.FormatChangePercent(2.3m, CultureInfo.InvariantCulture)
            .Should().Be("+2.30%");
        HeatmapTileCaptions.FormatChangePercent(-2.3m, CultureInfo.InvariantCulture)
            .Should().Be("-2.30%");
        HeatmapTileCaptions.FormatChangePercent(0m, CultureInfo.InvariantCulture)
            .Should().Be("0.00%");
    }

    private static SKColor Neutral() => new(45, 45, 60);

    private static SKColor Up() => new(34, 197, 94);

    private static SKColor Down() => new(239, 68, 68);
}
