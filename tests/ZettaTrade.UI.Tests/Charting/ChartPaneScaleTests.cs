namespace ZettaTrade.UI.Tests.Charting;

using System;
using System.Collections.Generic;
using FluentAssertions;
using Xunit;
using ZettaTrade.UI.Charting;

/// <summary>
/// Pins for the oscillator panes' own scale math (T-199): a declared usable
/// fixed bound pair pins the ladder exactly (RSI's 0-100 — no padding, the
/// canonical levels sit where the scale says), an unusable pair degrades to
/// the auto scale, and the auto scale is the main plot's own padded-spread
/// discipline applied to the pane's visible values. Pure facts.
/// </summary>
public sealed class ChartPaneScaleTests
{
    #region Fixed Bounds

    /// <summary>A declared ordered bound pair maps exactly — the canonical levels own the ladder.</summary>
    [Fact]
    public void Resolve_AUsableFixedBoundPair_PinsTheScaleExactly()
    {
        var (min, max, range) = ChartPaneScale.Resolve(0m, 100m, [50m]);

        min.Should().Be(0m, "the declared lower bound owns the scale");
        max.Should().Be(100m);
        range.Should().Be(100m, "the fixed ladder is never padded");
    }

    /// <summary>A declared pair with min >= max is unusable — the scale degrades to the auto discipline, never a negative range.</summary>
    [Fact]
    public void Resolve_AnInvertedFixedBoundPair_DegradesToTheAutoScale()
    {
        var (min, _, range) = ChartPaneScale.Resolve(100m, 0m, [10m, 20m]);

        min.Should().BeLessThan(10m, "the padded auto scale grows past the slice's own low");
        range.Should().BeGreaterThan(10m);
    }

    /// <summary>A half-declared pair (one bound null) is unusable too — the auto scale takes over.</summary>
    [Fact]
    public void Resolve_AHalfDeclaredPair_DegradesToTheAutoScale()
    {
        var (min, _, _) = ChartPaneScale.Resolve(0m, null, [10m, 20m]);

        min.Should().BeLessThan(10m);
    }

    /// <summary>A fixed bound never clips the data: the mapping holds the pane, values outside the band resolve THROUGH the declared scale (the draw pass runs them off the edge).</summary>
    [Fact]
    public void Resolve_ValuesOutsideTheFixedBand_KeepTheDeclaredScale()
    {
        var (min, max, range) = ChartPaneScale.Resolve(0m, 100m, [-50m, 150m]);

        min.Should().Be(0m);
        max.Should().Be(100m);
        range.Should().Be(100m, "the declared scale never stretches to contain the data");
    }

    #endregion

    #region Auto Scale

    /// <summary>The auto scale is the main plot's padded-spread discipline: ten percent on both ends of the slice's own spread.</summary>
    [Fact]
    public void Resolve_AnUnboundedPane_RidesThePaddedSpread()
    {
        var (min, max, range) = ChartPaneScale.Resolve(null, null, [10m, 20m]);

        min.Should().Be(9m, "the padded spread grows both ends by ten percent of the raw spread");
        max.Should().Be(21m);
        range.Should().Be(12m);
    }

    /// <summary>The warmup nulls never drive the scale: only the slice's non-null values do.</summary>
    [Fact]
    public void Resolve_WarmupNulls_AreSkipped()
    {
        var (min, _, _) = ChartPaneScale.Resolve(null, null, [null, null, 10m, 20m]);

        min.Should().Be(9m);
    }

    /// <summary>An all-warmup slice (the indicator's whole head) maps the neutral 0-1 unit — the pass never divides by zero and the pane draws its frame, no line yet.</summary>
    [Fact]
    public void Resolve_AnAllWarmupSlice_MapsTheNeutralUnit()
    {
        var (min, max, range) = ChartPaneScale.Resolve(null, null, [null, null]);

        min.Should().Be(0m);
        max.Should().Be(1m);
        range.Should().Be(1m);
    }

    /// <summary>An empty slice (no bars at all) rides the same neutral unit.</summary>
    [Fact]
    public void Resolve_AnEmptySlice_MapsTheNeutralUnit()
    {
        var (min, _, range) = ChartPaneScale.Resolve(null, null, Array.Empty<decimal?>());

        min.Should().Be(0m);
        range.Should().Be(1m);
    }

    #endregion
}
