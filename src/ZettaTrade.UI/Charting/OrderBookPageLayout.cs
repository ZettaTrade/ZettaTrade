namespace ZettaTrade.UI.Charting;

using System;
using System.Collections.Generic;
using System.Globalization;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
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

    #region Hit Resolution

    /// <summary>
    /// The inverse of <see cref="Build"/> for level clicks: the book level
    /// under a cursor height, planned with the same constants the widget
    /// drew with. Half-open row spans match the planned bands; the header
    /// band and the stretches beyond the planned rows yield no level; the
    /// mid-line pixel belongs to the best bid.
    /// </summary>
    public static BookLevelHit? ResolveLevel(
        OrderBook? book,
        double widgetHeight,
        double cursorY,
        double rowHeight,
        double headerHeight)
    {
        if (book is null)
        {
            return null;
        }

        var bodyHeight = Math.Max(0, widgetHeight - headerHeight);
        var rowsPerSide = Math.Max(
            MinRowsPerSide,
            (int)(bodyHeight / (2 * rowHeight)));
        var midY = headerHeight + (rowsPerSide * rowHeight);

        var visibleBids = Math.Min(book.Bids.Count, rowsPerSide);
        var visibleAsks = Math.Min(book.Asks.Count, rowsPerSide);

        if (cursorY < midY)
        {
            // Asks stack upward from the mid line: row i spans
            // [midY - (i+1)h, midY - i*h), so a distance of exactly the row
            // height (the row's own top edge) belongs to that row — the
            // ceiling-based index. The header band above carries no rows.
            if (cursorY < headerHeight)
            {
                return null;
            }

            var askIndex = (int)Math.Ceiling((midY - cursorY) / rowHeight) - 1;
            return askIndex < visibleAsks
                ? new BookLevelHit(OrderSide.Sell, book.Asks[askIndex].Price)
                : null;
        }

        // Bids stack downward from the mid line: row i spans
        // [midY + i*h, midY + (i+1)*h), so the floor-based index maps the
        // mid-line pixel to the best bid.
        var bidIndex = (int)((cursorY - midY) / rowHeight);
        return bidIndex < visibleBids
            ? new BookLevelHit(OrderSide.Buy, book.Bids[bidIndex].Price)
            : null;
    }

    #endregion Hit Resolution

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

/// <summary>
/// The book level a click resolved to: the side the user would trade AT
/// that price (a bid is bought at, an ask is sold at) and the level's price.
/// </summary>
public readonly record struct BookLevelHit(OrderSide Side, decimal Price);
