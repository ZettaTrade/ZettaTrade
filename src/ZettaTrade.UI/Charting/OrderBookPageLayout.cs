namespace ZettaTrade.UI.Charting;

using System;
using System.Collections.Generic;
using System.Globalization;
using ZettaTrade.Core.Entities;
using ZettaTrade.UI.Localization;
using ZettaTrade.UI.Resources;

/// <summary>
/// Frame-ready drawing plan for one order book snapshot: the visible rows of
/// both sides with their cumulative-volume fractions and precomputed text.
/// Pure math, shared by <see cref="OrderBookWidget"/> and its tests, so the
/// 60-fps frame budget is measurable without a renderer.
/// </summary>
public static class OrderBookPageLayout
{
    #region Constants

    /// <summary>The layout cannot produce fewer than this many rows per side.</summary>
    private const int MinRowsPerSide = 1;

    /// <summary>Fraction digits for money-like values; trailing zeros are trimmed.</summary>
    private const string NumberFormat = "0.00########";

    private const string PriceHeaderKey = "OrderBook.Price";
    private const string CumulativeVolumeHeaderKey = "OrderBook.CumulativeVolume";
    private const string SpreadFormatKey = "OrderBook.SpreadFormat";

    #endregion

    #region Public Methods

    /// <summary>
    /// Plan one frame for the book, cut to the rows the widget height can
    /// show. Per-frame cost is bounded by the visible row count, not by the
    /// incoming depth, so a 1000+ level book costs the same as a 100 level one.
    /// </summary>
    public static OrderBookPage Build(
        OrderBook book,
        double height,
        double rowHeight,
        double headerHeight)
    {
        var bodyHeight = Math.Max(0, height - headerHeight);
        var rowsPerSide = Math.Max(
            MinRowsPerSide,
            (int)(bodyHeight / (2 * rowHeight)));

        var midY = (float)(headerHeight + (rowsPerSide * rowHeight));
        var bids = BuildSide(book.Bids, rowsPerSide, rowHeight, midY, isBelowMid: true);
        var asks = BuildSide(book.Asks, rowsPerSide, rowHeight, midY, isBelowMid: false);

        return new OrderBookPage(
            book.Symbol.Normalized,
            SpreadText(book),
            Localized(PriceHeaderKey),
            Localized(CumulativeVolumeHeaderKey),
            asks,
            bids,
            midY);
    }

    #endregion

    #region Row Planning

    /// <summary>
    /// Plan one side. The best level sits closest to the mid line; the
    /// cumulative volume accumulates away from it: bids grow downward,
    /// asks grow upward.
    /// </summary>
    private static IReadOnlyList<OrderBookDrawRow> BuildSide(
        IReadOnlyList<PriceLevel> levels,
        int rowsPerSide,
        double rowHeight,
        float midY,
        bool isBelowMid)
    {
        var visible = Math.Min(levels.Count, rowsPerSide);
        var rows = new List<OrderBookDrawRow>(visible);

        // One pass for the deepest cumulative volume of the side; the bar of
        // each row is its share of that depth.
        var deepest = 0m;
        for (var i = 0; i < visible; i++)
        {
            deepest += levels[i].Quantity;
        }

        var denominator = deepest == 0m ? 1m : deepest;

        var cumulative = 0m;
        for (var i = 0; i < visible; i++)
        {
            var level = levels[i];
            cumulative += level.Quantity;

            var distance = i + 1;
            var y = isBelowMid
                ? midY + ((distance - 1) * (float)rowHeight)
                : midY - (distance * (float)rowHeight);

            rows.Add(new OrderBookDrawRow(
                MoneyText(level.Price),
                MoneyText(cumulative),
                (float)(cumulative / denominator),
                y));
        }

        return rows;
    }

    #endregion

    #region Header & Formatting

    private static string? SpreadText(OrderBook book) =>
        book.Spread is { } spread
            ? string.Format(
                CultureInfo.InvariantCulture,
                Localized(SpreadFormatKey),
                MoneyText(spread))
            : null;

    private static string MoneyText(decimal value) =>
        value.ToString(NumberFormat, CultureInfo.InvariantCulture);

    private static string Localized(string key)
    {
        if (LocalizationManager.IsInitialized)
        {
            return LocalizationManager.Instance.GetString(key);
        }

        // Design mode / manager not wired yet: raw resource lookup instead.
        return Strings.ResourceManager.GetString(
            key,
            CultureInfo.CurrentUICulture) ?? key;
    }

    #endregion
}

/// <summary>
/// One drawn row: its text values, the cumulative-volume bar fraction and
/// the top Y coordinate (bids below the mid, asks above it).
/// </summary>
public readonly record struct OrderBookDrawRow(
    string PriceText,
    string CumulativeText,
    float CumulativeFraction,
    float Y);

/// <summary>
/// Everything <see cref="OrderBookWidget"/>'s draw operation paints: header
/// strings, the planned rows of both sides and the mid-line position.
/// </summary>
public sealed record OrderBookPage(
    string SymbolLabel,
    string? SpreadText,
    string PriceHeader,
    string CumulativeHeader,
    IReadOnlyList<OrderBookDrawRow> Asks,
    IReadOnlyList<OrderBookDrawRow> Bids,
    float MidY);
