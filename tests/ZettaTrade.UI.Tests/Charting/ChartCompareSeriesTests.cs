namespace ZettaTrade.UI.Tests.Charting;

using System;
using System.Collections.Generic;
using FluentAssertions;
using Xunit;
using ZettaTrade.Core.Entities;
using ZettaTrade.UI.Charting;

/// <summary>
/// Pins for the symbol-compare overlay's pure helpers (T-124): the exact
/// open-time alignment (an unshared slot is an honest null — never a
/// nearest-neighbor substitute), the layer's own percent anchor, and the
/// axis-domain candidates the shared price scale unions. Pure facts —
/// the canvas itself is UIA-invisible here.
/// </summary>
public sealed class ChartCompareSeriesTests
{
    /// <summary>Open times a fact addresses by hour index (ascending by construction).</summary>
    private static DateTimeOffset Hour(int index) =>
        new DateTimeOffset(2026, 11, 9, 0, 0, 0, TimeSpan.Zero).AddHours(index);

    private static Bar MainBar(int index, decimal close) => new(
        Hour(index),
        close - 1m,
        close + 1m,
        close - 2m,
        close,
        5m);

    private static Bar LayerBar(int index, decimal close) => new(
        Hour(index),
        close - 1m,
        close + 1m,
        close - 2m,
        close,
        3m);

    #region Alignment

    /// <summary>
    /// The alignment maps the layer's close onto the visible slot that
    /// shares the EXACT open time; every other slot is a null gap the
    /// renderer leaves blank — a walk that advances each pointer once,
    /// never a quadratic search.
    /// </summary>
    [Fact]
    public void Align_MapsExactOpenTimes_AndLeavesUnsharedSlotsNull()
    {
        var layerBars = new[]
        {
            LayerBar(1, 200m),
            LayerBar(2, 202m),
            LayerBar(3, 204m),
        };
        var visibleBars = new[]
        {
            MainBar(0, 105m),
            MainBar(1, 106m),
            MainBar(2, 107m),
            MainBar(4, 108m),
        };

        var aligned = ChartCompareSeries.Align(layerBars, visibleBars);

        aligned.Should().HaveCount(4, "one slot per visible bar");
        aligned[0].Should().BeNull("hour 0 is not on the layer's own grid — an honest gap");
        aligned[1].Should().Be(200m);
        aligned[2].Should().Be(202m);
        aligned[3].Should().BeNull("hour 4 is not on the layer's own grid");
    }

    /// <summary>A layer that carried nothing aligns to an all-null window (the renderer then drops the layer wholesale).</summary>
    [Fact]
    public void Align_EmptyLayer_IsAllNull()
    {
        var visibleBars = new[] { MainBar(0, 105m), MainBar(1, 106m) };

        var aligned = ChartCompareSeries.Align([], visibleBars);

        aligned.Should().HaveCount(2, "one slot per visible bar");
        aligned.Should().OnlyContain(value => value == null);
    }

    #endregion

    #region Anchor

    /// <summary>The layer's percent anchor is its FIRST aligned close — each series rebases to its own zero.</summary>
    [Fact]
    public void AxisAnchor_TakesTheFirstAlignedClose()
    {
        var aligned = new decimal?[] { null, 200m, 204m };

        ChartCompareSeries.AxisAnchor(aligned).Should().Be(200m);
    }

    /// <summary>An alignment without any shared slot has no anchor; the fallback keeps the transform defined while the resolve drops the layer.</summary>
    [Fact]
    public void AxisAnchor_WithoutAnySharedSlot_FallsBackToZero()
    {
        ChartCompareSeries.AxisAnchor([null, null]).Should().Be(0m);
    }

    #endregion

    #region Axis Candidates

    /// <summary>
    /// The percent candidates map through the layer's OWN anchor — the
    /// (100→110) ≡ (1000→1100) = +10% equivalence — so two compared
    /// symbols of different magnitudes swing on the same axis shoulder.
    /// </summary>
    [Fact]
    public void PercentCandidates_MapThroughTheLayerOwnAnchor()
    {
        var aligned = new decimal?[] { 100m, 110m, 121m, null };

        var candidates = ChartCompareSeries.LayerAxisCandidates(
            aligned,
            PriceScaleMode.Percent,
            anchor: 100m,
            floor: 1m);

        candidates.Should().Equal([0m, 10m, 21m], "each shared close becomes its own percent over the layer zero");
    }

    /// <summary>A small-symbol series and a large-symbol series live on the SAME percent shoulder for equal relative moves.</summary>
    [Fact]
    public void PercentCandidates_AreIndependentOfTheRawMagnitude()
    {
        var small = ChartCompareSeries.LayerAxisCandidates(
            [110m],
            PriceScaleMode.Percent,
            anchor: 100m,
            floor: 1m);
        var large = ChartCompareSeries.LayerAxisCandidates(
            [1_100m],
            PriceScaleMode.Percent,
            anchor: 1_000m,
            floor: 1m);

        small.Should().Equal(large, "(100→110) ≡ (1000→1100) = +10%");
    }

    /// <summary>The log candidates map the raw closes through the SHARED floor — the anchor is meaningless there.</summary>
    [Fact]
    public void LogCandidates_MapThroughTheSharedFloor_RegardlessOfTheAnchor()
    {
        var aligned = new decimal?[] { 100m, null, 1_000m };

        var withOneAnchor = ChartCompareSeries.LayerAxisCandidates(
            aligned,
            PriceScaleMode.Logarithmic,
            anchor: 100m,
            floor: 1m);
        var withAnotherAnchor = ChartCompareSeries.LayerAxisCandidates(
            aligned,
            PriceScaleMode.Logarithmic,
            anchor: 555m,
            floor: 1m);

        withOneAnchor.Should().HaveCount(2, "the null gap never contributes");
        withOneAnchor.Should().Equal(withAnotherAnchor, "the log domain ignores the layer anchor");
        withOneAnchor[0].Should().Be(
            ChartPriceTransform.ToAxisValue(100m, PriceScaleMode.Logarithmic, anchor: 0m, floor: 1m),
            "the raw close rides the shared log domain");
    }

    /// <summary>The linear candidates carry the raw closes themselves — the identity transform.</summary>
    [Fact]
    public void LinearCandidates_CarryTheRawCloses()
    {
        var aligned = new decimal?[] { 97.5m, null, 103m };

        var candidates = ChartCompareSeries.LayerAxisCandidates(
            aligned,
            PriceScaleMode.Linear,
            anchor: 100m,
            floor: 1m);

        candidates.Should().Equal([97.5m, 103m], "linear keeps raw prices, gaps stay out");
    }

    #endregion

    #region Legend Casus

    /// <summary>
    /// The theme slots, the fallback colors and the layer cap count the
    /// same — the two arrays and the cap are one list modulo the same
    /// index, so a fifth color key could not exist while the cap held four.
    /// </summary>
    [Fact]
    public void TheLegendCap_TheColorKeys_AndTheFallbackColors_CountTheSame()
    {
        ChartCompareSeries.MaxCompareLayers.Should().Be(
            ChartCompareSeries.ColorKeys.Length,
            "the color keys and the cap are one list length");
        ChartCompareSeries.MaxCompareLayers.Should().Be(
            ChartCompareSeries.FallbackColors.Length,
            "the fallbacks and the cap are one list length");
    }

    #endregion
}
