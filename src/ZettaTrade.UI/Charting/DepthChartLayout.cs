namespace ZettaTrade.UI.Charting;

using System;
using System.Collections.Generic;
using System.Globalization;
using ZettaTrade.Core.Entities;

/// <summary>
/// Frame-ready drawing plan for one order book snapshot in depth-chart form:
/// the cumulative bid/ask areas around the midspread as per-band staircase
/// slices with precomputed pixel geometry. Pure math, shared by
/// <see cref="DepthChartWidget"/> and its tests, so the 60 fps frame budget
/// is measurable without a renderer.
/// </summary>
/// <remarks>
/// Depth resolution discipline (the T-072 served vs displayed contract): the
/// plan consumes ONLY the book it is handed — the same served snapshot the
/// order book ladder binds (the panels' shared <c>OrderBook.Book</c>
/// property) — and never re-requests depth. What it displays is the served
/// depth cut to the visible geometry, and the cut is bounded by panel pixels
/// only: one band per served level at the panel's band pitch, bands filled
/// from the best level outward until the served list or the band budget the
/// height can show runs out. Levels beyond the band budget stay served but
/// undisplayed; the plan never fabricates levels (a book shallower than the
/// budget ends mid-panel, it is never stretched to fill it). A planned band
/// mirrors a planned ladder row one for one — same cut, same normalization —
/// so the two components agree on the same resolved depth (pinned by the
/// cross-component fact in the tests). The plan is a pure function of its
/// arguments: nothing here re-requests depth.
/// </remarks>
public static class DepthChartLayout
{
    #region Constants

    /// <summary>The layout cannot produce fewer than this many bands per side.</summary>
    private const int MinBandsPerSide = 1;

    /// <summary>Horizontal padding between the panel edge and the area fill.</summary>
    private const float HorizontalPaddingPx = 8;

    /// <summary>Substitute denominator for a side without visible volume.</summary>
    private const decimal ZeroVolumeDenominator = 1m;

    /// <summary>Fraction digits for money-like values; trailing zeros are trimmed.</summary>
    private const string NumberFormat = "0.00########";

    #endregion

    #region Public Methods

    /// <summary>
    /// Plan one frame for the book, cut to the bands the widget height can
    /// show. Per-frame cost is bounded by the visible band count, not by the
    /// incoming depth, so a 1000+ level book costs the same as a 100 level
    /// one.
    /// </summary>
    public static DepthChartPage Build(
        OrderBook book,
        double width,
        double height,
        double headerHeight,
        double bandHeight)
    {
        var bodyHeight = Math.Max(0, height - headerHeight);
        var bandsPerSide = Math.Max(
            MinBandsPerSide,
            (int)(bodyHeight / (2 * bandHeight)));

        var midY = (float)(headerHeight + (bandsPerSide * bandHeight));
        var leftX = HorizontalPaddingPx;
        var interior = Math.Max(0f, (float)width - (2 * HorizontalPaddingPx));

        // Bids grow downward from the mid line, asks mirrored upward; both
        // sides share the same pixel-derived cut as the ladder, so the
        // deeper served levels never reach the plan (see the type's remarks
        // for the contract).
        var bids = BuildSide(book.Bids, bandsPerSide, leftX, interior, midY, isBelowMid: true, bandHeight);
        var asks = BuildSide(book.Asks, bandsPerSide, leftX, interior, midY, isBelowMid: false, bandHeight);

        return new DepthChartPage(
            MidLabel(book),
            asks,
            bids,
            midY,
            leftX,
            (float)bandHeight);
    }

    #endregion

    #region Band Planning

    /// <summary>
    /// Plan one side. The best level sits on the mid line; each further
    /// level owns one band away from it (bids downward, asks upward) and the
    /// band's fill width is this level's cumulative share of the side's
    /// VISIBLE volume — the same fraction the ladder's cumulative bar draws
    /// for the level on the same resolved cut — so the deepest planned band
    /// of a side always spans the full interior width.
    /// </summary>
    private static IReadOnlyList<DepthSlice> BuildSide(
        IReadOnlyList<PriceLevel> levels,
        int bandsPerSide,
        float leftX,
        float interior,
        float midY,
        bool isBelowMid,
        double bandHeight)
    {
        var visible = Math.Min(levels.Count, bandsPerSide);
        var slices = new List<DepthSlice>(visible);

        if (visible == 0)
        {
            return slices;
        }

        // One pass for the side's visible depth; every band's fill width is
        // its cumulative share of that depth.
        var deepest = 0m;
        for (var i = 0; i < visible; i++)
        {
            deepest += levels[i].Quantity;
        }

        var denominator = deepest == 0m ? ZeroVolumeDenominator : deepest;

        var cumulative = 0m;
        for (var i = 0; i < visible; i++)
        {
            var level = levels[i];
            cumulative += level.Quantity;

            // The slice's step edge sits on the boundary shared with the
            // previous band: the mid line for the best level, the own
            // band's upper edge (bids) / lower edge (asks) for the deeper
            // ones. Sign flips on both distance and the outgoing edge.
            var distance = i;
            var stepY = isBelowMid
                ? midY + ((float)(distance * bandHeight))
                : midY - ((float)(distance * bandHeight));
            var fraction = (float)(cumulative / denominator);

            slices.Add(new DepthSlice(
                stepY,
                leftX + (interior * fraction),
                fraction));
        }

        return slices;
    }

    #endregion

    #region Mid Label

    private static string? MidLabel(OrderBook book) =>
        book.MidPrice is { } mid
            ? mid.ToString(NumberFormat, CultureInfo.InvariantCulture)
            : null;

    #endregion
}

/// <summary>
/// One staircase slice: the Y of its step edge (the band boundary it fills
/// from, measured from the mid line outward), the pixel right edge of the
/// fill and the cumulative fraction that width encodes (the same fraction
/// the ladder bar draws for the level on the same resolved cut).
/// </summary>
public readonly record struct DepthSlice(
    float Y,
    float X,
    float CumulativeFraction);

/// <summary>
/// Everything <see cref="DepthChartWidget"/>'s draw operation paints: the
/// staircase slices of both sides around the mid line with precomputed pixel
/// geometry, the mid-line position, the fill's left edge and the formatted
/// mid price (null for a one-sided book — the areas still draw).
/// </summary>
public sealed record DepthChartPage(
    string? MidLabel,
    IReadOnlyList<DepthSlice> Asks,
    IReadOnlyList<DepthSlice> Bids,
    float MidY,
    float LeftX,
    float SliceHeightPx);
