namespace ZettaTrade.UI.Charting;

using System;
using System.Collections.Generic;
using System.Globalization;
using Livery.Localization;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.UI.Resources;

/// <summary>
/// Frame-ready drawing plan for one liquidation-tape snapshot: the visible
/// rows newest first with precomputed text, the column captions, the paused
/// hint and the open-cascade badge. Pure math, shared by
/// <see cref="LiquidationsWidget"/> and its tests, so the 60-fps frame budget
/// is measurable without a renderer.
/// <para>
/// The three columns mirror the trade tape's (time, price, size — the
/// venue's served facts); the panel's AGGREGATE (the cascade's summed
/// notional and order count) rides the badge in the header band, not a
/// fourth column — the tape column's minimum width is 160 px and a fourth
/// column would overlap at it. The side colors by the force-closed
/// POSITION: a liquidated long was force-sold (the bearish color), a
/// liquidated short force-bought (the bullish one).
/// </para>
/// </summary>
public static class LiquidationsPageLayout
{
    #region Constants

    /// <summary>The layout cannot produce fewer than this many rows.</summary>
    private const int MinRows = 1;

    /// <summary>Fraction digits for the price and size columns; trailing zeros are trimmed.</summary>
    private const string NumberFormat = "0.00########";

    /// <summary>The clock the time column prints: the local wall clock, hours to seconds.</summary>
    private const string TimeFormat = "HH:mm:ss";

    /// <summary>The badge's notional format: grouped integers (a magnitude read, never a precision claim).</summary>
    private const string NotionalFormat = "#,0";

    private const string TimeHeaderKey = "Tape.Liquidations.Time";
    private const string PriceHeaderKey = "Tape.Liquidations.Price";
    private const string SizeHeaderKey = "Tape.Liquidations.Size";
    private const string PausedHintKey = "Tape.Liquidations.PausedHint";
    private const string CascadeBadgeKey = "Tape.Liquidations.CascadeHint";

    #endregion

    #region Public Methods

    /// <summary>
    /// Plan one frame for the panel, cut to the rows the widget height can
    /// show. Per-frame cost is bounded by the visible row count, not by the
    /// ring's fill, so a full ring costs the same as a fresh one. A paused
    /// page keeps the rows it froze with and carries the localized hint the
    /// header band shows while new events stay buffered. An open cascade
    /// (null when none) formats into the header band's badge.
    /// </summary>
    public static LiquidationsPage Build(
        IReadOnlyList<LiquidationEvent> newestFirst,
        double height,
        double rowHeight,
        double headerHeight,
        bool isPaused,
        LiquidationCascade? cascade)
    {
        var bodyHeight = Math.Max(0, height - headerHeight);
        var visibleRows = Math.Max(MinRows, (int)(bodyHeight / rowHeight));
        var rows = BuildRows(newestFirst, visibleRows, rowHeight, headerHeight);

        return new LiquidationsPage(
            BadgeText(cascade),
            isPaused ? Localized(PausedHintKey) : null,
            Localized(TimeHeaderKey),
            Localized(PriceHeaderKey),
            Localized(SizeHeaderKey),
            rows);
    }

    #endregion

    #region Row Planning

    /// <summary>
    /// Plan the rows: the newest event sits at the top of the body, the list
    /// only ever extends below (the panel never reserves a mid line).
    /// </summary>
    private static IReadOnlyList<LiquidationDrawRow> BuildRows(
        IReadOnlyList<LiquidationEvent> newestFirst,
        int visibleRows,
        double rowHeight,
        double headerHeight)
    {
        var visible = Math.Min(newestFirst.Count, visibleRows);
        var rows = new List<LiquidationDrawRow>(visible);

        for (var i = 0; i < visible; i++)
        {
            var ev = newestFirst[i];
            var y = (float)(headerHeight + (i * rowHeight));

            rows.Add(new LiquidationDrawRow(
                TimeText(ev.Timestamp),
                MoneyText(ev.Price.Value),
                MoneyText(ev.Quantity.Value),
                ev.LiquidatedSide,
                y));
        }

        return rows;
    }

    #endregion

    #region Formatting

    private static string TimeText(DateTimeOffset timestamp) =>
        timestamp.LocalDateTime.ToString(TimeFormat, CultureInfo.InvariantCulture);

    private static string MoneyText(decimal value) =>
        value.ToString(NumberFormat, CultureInfo.InvariantCulture);

    /// <summary>
    /// The badge's text for an open cascade: the localized template with the
    /// served order count and the summed served notional (grouped integers —
    /// a magnitude read). Both are FLOORS (the venues publish only the first
    /// forced order of a cascade — the panel's note says so); the badge
    /// never claims more than the served facts carry. No cascade, no badge.
    /// </summary>
    private static string? BadgeText(LiquidationCascade? cascade) =>
        cascade is null
            ? null
            : string.Format(
                CultureInfo.CurrentCulture,
                Localized(CascadeBadgeKey),
                cascade.OrderCount,
                cascade.TotalNotional.ToString(NotionalFormat, CultureInfo.InvariantCulture));

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
/// One drawn liquidation row: its precomputed text columns, the
/// force-closed position side the row is colored with (a liquidated long
/// paints the bearish color, a liquidated short the bullish one) and the
/// top Y coordinate (the newest event at the top of the body).
/// </summary>
public readonly record struct LiquidationDrawRow(
    string TimeText,
    string PriceText,
    string SizeText,
    LiquidationSide Side,
    float Y);

/// <summary>
/// Everything <see cref="LiquidationsWidget"/>'s draw operation paints: the
/// open-cascade badge (null while none), the paused hint (null while
/// running), the column headers and the planned rows newest first.
/// </summary>
public sealed record LiquidationsPage(
    string? Badge,
    string? PausedHint,
    string TimeHeader,
    string PriceHeader,
    string SizeHeader,
    IReadOnlyList<LiquidationDrawRow> Rows);
