namespace ZettaTrade.Infrastructure.Exchange;

using System;
using System.Collections.Generic;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// Outcome of an <see cref="BybitOrderBookAssembler"/> apply.
/// </summary>
internal enum BybitOrderBookOutcome
{
    /// <summary>The frame applied and the rebuilt book is published.</summary>
    Applied,

    /// <summary>A delta whose sequence already passed (a benign server race) — the state is untouched.</summary>
    StaleSkipped,

    /// <summary>A sequence gap (topology or restart): the state is discarded and the topic is re-subscribed.</summary>
    GapDetected,
}

/// <summary>An apply outcome plus the rebuilt book (<see cref="BybitOrderBookOutcome.Applied"/> only).</summary>
internal sealed record BybitOrderBookApply(
    BybitOrderBookOutcome Outcome,
    OrderBook? Book);

/// <summary>
/// Order book state machine for the Bybit v5 <c>orderbook.{depth}</c> stream
/// over ONE symbol: a <c>snapshot</c> frame replaces the whole state, a
/// <c>delta</c> frame applies only when its sequence number continues the
/// previous one (<c>u == last + 1</c>; each level carries the new full
/// quantity, quantity <c>"0"</c> deletes the price level), and a detected gap
/// discards the state — the adapter then re-subscribes the topic and the
/// server's fresh snapshot rebuilds from scratch. Every applied frame
/// publishes the FULL rebuilt depth, capped at the subscribed depth (the
/// best-priced levels win: highest bids, lowest asks).
/// </summary>
/// <param name="Symbol">The symbol this state machine belongs to.</param>
/// <param name="MaxDepth">Published level cap per side.</param>
internal sealed class BybitOrderBookAssembler(Symbol symbol, int maxDepth)
{
    private readonly SortedDictionary<decimal, decimal> _bids = new(DescendingComparer);
    private readonly SortedDictionary<decimal, decimal> _asks = new(Comparer<decimal>.Default);
    private long _lastSequence;
    private bool _hasSnapshot;

    /// <summary>Bids iterate best-first: descending by price.</summary>
    private static readonly IComparer<decimal> DescendingComparer =
        Comparer<decimal>.Create((left, right) => right.CompareTo(left));

    #region Methods

    /// <summary>
    /// Apply a <c>snapshot</c> frame: replace the whole state and publish the
    /// rebuilt book. Snapshots are idempotent — any stale or interleaved one
    /// simply wins as the new state.
    /// </summary>
    /// <param name="receivedAt">The frame's local receive moment at the
    /// adapter's ingestion point (T-142) — carried verbatim into the
    /// rebuilt book's <see cref="OrderBook.Timestamp"/>.</param>
    public BybitOrderBookApply ApplySnapshot(
        long sequence,
        IReadOnlyList<PriceLevel> bids,
        IReadOnlyList<PriceLevel> asks,
        DateTimeOffset receivedAt)
    {
        _bids.Clear();
        _asks.Clear();
        _hasSnapshot = true;
        _lastSequence = sequence;

        // A snapshot level with a non-positive quantity carries no volume —
        // an add of nothing, not a guessable level.
        foreach (var level in bids)
        {
            if (level.Quantity > 0m)
            {
                _bids[level.Price] = level.Quantity;
            }
        }

        foreach (var level in asks)
        {
            if (level.Quantity > 0m)
            {
                _asks[level.Price] = level.Quantity;
            }
        }

        return BuildApplied(receivedAt);
    }

    /// <summary>
    /// Apply a <c>delta</c> frame: only when the sequence continues the
    /// previous frame. A stale sequence is skipped silently (a server race,
    /// far too frequent to log), a gap discards the state and reports
    /// <see cref="BybitOrderBookOutcome.GapDetected"/> so the adapter can
    /// re-subscribe the topic.
    /// </summary>
    /// <param name="receivedAt">The frame's local receive moment at the
    /// adapter's ingestion point (T-142) — carried verbatim into the
    /// rebuilt book's <see cref="OrderBook.Timestamp"/>.</param>
    public BybitOrderBookApply ApplyDelta(
        long sequence,
        IReadOnlyList<PriceLevel> bids,
        IReadOnlyList<PriceLevel> asks,
        DateTimeOffset receivedAt)
    {
        if (!_hasSnapshot || sequence > _lastSequence + 1)
        {
            Reset();
            return new(BybitOrderBookOutcome.GapDetected, null);
        }

        if (sequence <= _lastSequence)
        {
            return new(BybitOrderBookOutcome.StaleSkipped, null);
        }

        // Quantity 0 is Bybit's "delete this price level"; every other value
        // replaces the level's full remaining quantity.
        foreach (var level in bids)
        {
            ApplyLevelChange(_bids, level);
        }

        foreach (var level in asks)
        {
            ApplyLevelChange(_asks, level);
        }

        _lastSequence = sequence;
        return BuildApplied(receivedAt);
    }

    /// <summary>Drop the whole state (after a detected gap; a fresh snapshot rebuilds it).</summary>
    public void Reset()
    {
        _bids.Clear();
        _asks.Clear();
        _hasSnapshot = false;
        _lastSequence = 0;
    }

    #endregion

    #region Private Methods

    private static void ApplyLevelChange(SortedDictionary<decimal, decimal> levels, PriceLevel level)
    {
        if (level.Quantity > 0m)
        {
            levels[level.Price] = level.Quantity;
        }
        else
        {
            levels.Remove(level.Price);
        }
    }

    private BybitOrderBookApply BuildApplied(DateTimeOffset receivedAt)
    {
        Cap(_bids);
        Cap(_asks);

        var bids = new List<PriceLevel>(_bids.Count);
        foreach (var (price, quantity) in _bids)
        {
            bids.Add(new PriceLevel(price, quantity));
        }

        var asks = new List<PriceLevel>(_asks.Count);
        foreach (var (price, quantity) in _asks)
        {
            asks.Add(new PriceLevel(price, quantity));
        }

        return new(
            BybitOrderBookOutcome.Applied,
            new OrderBook
            {
                Symbol = symbol,
                Bids = bids,
                Asks = asks,
                Timestamp = receivedAt,
            });
    }

    /// <summary>Keep the best-priced levels when the state exceeds the subscribed depth.</summary>
    private void Cap(SortedDictionary<decimal, decimal> levels)
    {
        while (levels.Count > maxDepth)
        {
            // The iteration order ends on the worst-priced level (lowest bid,
            // highest ask) — exactly the one to drop.
            levels.Remove(levels.Last().Key);
        }
    }

    #endregion
}
