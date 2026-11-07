namespace ZettaTrade.UI.Charting;

using System;
using System.Collections.Generic;
using System.Globalization;
using Livery.Localization;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.UI.Resources;

/// <summary>
/// Frame-ready drawing plan for one trade-tape snapshot: the visible rows
/// newest first with precomputed text and the paused hint. Pure math, shared
/// by <see cref="TradeTapeWidget"/> and its tests, so the 60-fps frame budget
/// is measurable without a renderer.
/// </summary>
public static class TradeTapePageLayout
{
    #region Constants

    /// <summary>The layout cannot produce fewer than this many rows.</summary>
    private const int MinRows = 1;

    /// <summary>Fraction digits for the price and size columns; trailing zeros are trimmed.</summary>
    private const string NumberFormat = "0.00########";

    /// <summary>The clock the time column prints: the local wall clock, hours to seconds.</summary>
    private const string TimeFormat = "HH:mm:ss";

    private const string TimeHeaderKey = "Tape.Time";
    private const string PriceHeaderKey = "Tape.Price";
    private const string SizeHeaderKey = "Tape.Size";
    private const string PausedHintKey = "Tape.PausedHint";

    #endregion

    #region Public Methods

    /// <summary>
    /// Plan one frame for the tape, cut to the rows the widget height can
    /// show. Per-frame cost is bounded by the visible row count, not by the
    /// ring's fill, so a 4096-trade ring costs the same as a fresh one.
    /// A paused page keeps the rows it froze with and carries the localized
    /// hint the header band shows while new trades stay buffered.
    /// </summary>
    public static TradeTapePage Build(
        IReadOnlyList<MarketTrade> newestFirst,
        double height,
        double rowHeight,
        double headerHeight,
        bool isPaused)
    {
        var bodyHeight = Math.Max(0, height - headerHeight);
        var visibleRows = Math.Max(MinRows, (int)(bodyHeight / rowHeight));
        var rows = BuildRows(newestFirst, visibleRows, rowHeight, headerHeight);

        return new TradeTapePage(
            isPaused ? Localized(PausedHintKey) : null,
            Localized(TimeHeaderKey),
            Localized(PriceHeaderKey),
            Localized(SizeHeaderKey),
            rows);
    }

    #endregion

    #region Row Planning

    /// <summary>
    /// Plan the rows: the newest trade sits at the top of the body, the list
    /// only ever extends below (the tape never reserves a mid line).
    /// </summary>
    private static IReadOnlyList<TradeTapeDrawRow> BuildRows(
        IReadOnlyList<MarketTrade> newestFirst,
        int visibleRows,
        double rowHeight,
        double headerHeight)
    {
        var visible = Math.Min(newestFirst.Count, visibleRows);
        var rows = new List<TradeTapeDrawRow>(visible);

        for (var i = 0; i < visible; i++)
        {
            var trade = newestFirst[i];
            var y = (float)(headerHeight + (i * rowHeight));

            rows.Add(new TradeTapeDrawRow(
                TimeText(trade.Timestamp),
                MoneyText(trade.Price.Value),
                MoneyText(trade.Quantity.Value),
                trade.Side,
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
/// One drawn trade row: its precomputed text columns, the aggressor side the
/// row is colored with (buys the bullish color, sells the bearish one) and
/// the top Y coordinate (the newest trade at the top of the body).
/// </summary>
public readonly record struct TradeTapeDrawRow(
    string TimeText,
    string PriceText,
    string SizeText,
    OrderSide Side,
    float Y);

/// <summary>
/// Everything <see cref="TradeTapeWidget"/>'s draw operation paints: the
/// column headers, the paused hint (null while running) and the planned
/// rows newest first.
/// </summary>
public sealed record TradeTapePage(
    string? PausedHint,
    string TimeHeader,
    string PriceHeader,
    string SizeHeader,
    IReadOnlyList<TradeTapeDrawRow> Rows);
