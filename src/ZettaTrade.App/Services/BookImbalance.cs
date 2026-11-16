namespace ZettaTrade.App.Services;

using System;
using ZettaTrade.Core.Entities;

/// <summary>
/// The book imbalance indicator's pure math (T-246): a bid/ask volume ratio
/// over a configurable depth window — a computation over ONE served book
/// snapshot, no transport, no clock, no scheduler. Static and side-effect
/// free so the goldens pin the semantics without a feed (the
/// <see cref="CvdAggregation"/> discipline).
/// <para>
/// The math, exactly: the ratio weighs the first <c>window</c> price levels
/// of each side (the window truncates at the served depth — the feed caps
/// the ladder at its own depth, the window never demands more than the
/// snapshot carries); <c>(sumBid - sumAsk) / (sumBid + sumAsk)</c> in
/// decimal — bounded to [−1, +1], +1 = the whole window rides the bid side,
/// −1 = the ask side, 0 = the balanced book. A window below one level names
/// no window — the read is neutral zero (the honest off, the T-245
/// threshold's own zero shape). An empty book is balanced by definition
/// (nothing to weigh, never a fabricated lean); a one-sided book reads the
/// full lean. The hot path is allocation-free: indexers and decimal
/// arithmetic only, no LINQ, no intermediate collections — the 60 fps /
/// 1000-level target holds with the indicator live.
/// </para>
/// <para>
/// The optional smoothing is a per-update exponential moving average with
/// the fixed <see cref="SmoothingAlpha"/> weight of the fresh snapshot: the
/// first read passes through raw, every next read blends
/// <c>previous + alpha * (raw - previous)</c>. The caller owns the previous
/// read's state and its lifecycle — a symbol switch resets it (the next
/// symbol's depth distribution must never blend with the old one's).
/// </para>
/// </summary>
public static class BookImbalance
{
    /// <summary>The default depth window (price levels per side) when no configuration says otherwise.</summary>
    public const int DefaultWindow = 10;

    /// <summary>
    /// The smoothed read's fixed weight of the fresh snapshot (the
    /// per-update EMA): each update quarters the step toward the new read.
    /// A constant, not a setting — the smoothing shape is pinned, only its
    /// on/off rides the knob.
    /// </summary>
    public const decimal SmoothingAlpha = 0.25m;

    /// <summary>
    /// The window's imbalance ratio over one snapshot.
    /// </summary>
    /// <param name="book">The served snapshot; its own depth caps the window.</param>
    /// <param name="window">Price levels per side; below one level reads neutral.</param>
    /// <returns>The ratio in [−1, +1]; 0 for a no-window or no-volume book.</returns>
    public static decimal Ratio(OrderBook book, int window)
    {
        ArgumentNullException.ThrowIfNull(book);

        if (window < 1)
        {
            return 0m;
        }

        var bidCount = Math.Min(window, book.Bids.Count);
        var askCount = Math.Min(window, book.Asks.Count);

        decimal bids = 0m;
        for (var i = 0; i < bidCount; i++)
        {
            bids += book.Bids[i].Quantity;
        }

        decimal asks = 0m;
        for (var i = 0; i < askCount; i++)
        {
            asks += book.Asks[i].Quantity;
        }

        var total = bids + asks;
        return total == 0m ? 0m : (bids - asks) / total;
    }

    /// <summary>
    /// The smoothing step: with the smoothing on, the previous read blends
    /// toward the fresh raw ratio; with it off, the raw passes through (and
    /// the caller's state keeps the raw, so re-enabling continues the EMA
    /// from the honest last read, never a stale one).
    /// </summary>
    /// <param name="previous">The caller's carried previous read (null = the stream's first read).</param>
    /// <param name="raw">The fresh window ratio.</param>
    /// <param name="enabled">Whether the smoothing rides.</param>
    /// <returns>The read to publish and carry.</returns>
    public static decimal Smoothed(decimal? previous, decimal raw, bool enabled) =>
        enabled && previous is { } prev
            ? prev + SmoothingAlpha * (raw - prev)
            : raw;
}
