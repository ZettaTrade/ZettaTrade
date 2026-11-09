namespace ZettaTrade.App.Tests.Services;

using FluentAssertions;
using Xunit;
using ZettaTrade.App.Services;

/// <summary>
/// The gauge math's goldens (T-108): the exact rates, bucket indices, bird
/// bars and memory figures the overlay prints — the functions are pure, so
/// the goldens pin them for good.
/// </summary>
public sealed class HudMathTests
{
    /// <summary>The histogram bounds the facts share (the sampler's shape).</summary>
    private static double[] Bounds() => [1, 5, 20, 100, 500];

    [Theory]
    [InlineData(60, 1.0, 60.0)] // the budget target: a steady 60 fps
    [InlineData(120, 0.5, 240.0)]
    [InlineData(30, 2.0, 15.0)]
    [InlineData(0, 1.0, 0.0)] // a quiet chart reads zero
    [InlineData(-3, 1.0, 0.0)] // a negative count is not a reading
    [InlineData(10, 0.0, 0.0)] // a zero window guards the division
    public void FramesPerSecond_DividesTheDrainedCount_ByTheWindow(int frames, double elapsedSeconds, double expected)
    {
        HudMath.FramesPerSecond(frames, elapsedSeconds).Should().Be(expected);
    }

    [Theory]
    [InlineData(0.5, 0)] // under the first edge
    [InlineData(1.0, 0)] // the edges are inclusive upper bounds
    [InlineData(1.0001, 1)]
    [InlineData(5.0, 1)]
    [InlineData(5.1, 2)]
    [InlineData(99.9, 3)]
    [InlineData(100.0, 3)]
    [InlineData(500.0, 4)]
    [InlineData(500.1, 5)] // the overflow bucket (bounds.Length)
    [InlineData(-0.4, 0)] // clock skew reads as no visible lag
    public void HistogramBucketIndex_PlacesTheSample_AtItsInclusiveEdge(double valueMs, int expected)
    {
        HudMath.HistogramBucketIndex(valueMs, Bounds()).Should().Be(expected);
    }

    [Fact]
    public void HistogramBucketIndex_WithEmptyBounds_PutsEverythingIntoTheOverflow()
    {
        HudMath.HistogramBucketIndex(0.1, []).Should().Be(0);
    }

    [Theory]
    [InlineData(0.0, 8, "░░░░░░░░")] // an idle gauge
    [InlineData(1.0, 8, "████████")] // a full gauge
    [InlineData(0.5, 8, "████░░░░")] // the half renders (midpoint away from zero)
    [InlineData(0.25, 8, "██░░░░░░")]
    [InlineData(0.125, 8, "█░░░░░░░")]
    [InlineData(1.5, 8, "████████")] // the clamp keeps a probe glitch honest
    [InlineData(-0.2, 8, "░░░░░░░░")]
    [InlineData(0.5, 0, "")] // a zero-width gauge prints nothing
    public void GaugeBar_RendersTheClampedFraction_IntoBirdSegments(double fraction, int segments, string expected)
    {
        HudMath.GaugeBar(fraction, segments).Should().Be(expected);
    }

    [Theory]
    [InlineData(0, "0.0")]
    [InlineData(52428800, "50.0")] // exactly 50 MiB
    [InlineData(432542764, "412.5")]
    public void FormatMegabytes_PrintsOneInvariantDecimal(long bytes, string expected)
    {
        HudMath.FormatMegabytes(bytes).Should().Be(expected);
    }
}
