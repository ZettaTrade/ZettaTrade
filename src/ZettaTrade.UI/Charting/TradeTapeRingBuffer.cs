namespace ZettaTrade.UI.Charting;

using System;
using System.Collections.Generic;
using ZettaTrade.Core.Entities;

/// <summary>
/// Fixed-capacity ring of recent trades (the tape's memory bound): a push
/// past the capacity evicts the oldest trade instead of growing, so a burst
/// feed can never inflate the panel's memory.
/// </summary>
/// <remarks>
/// Single-mutator contract: <see cref="Push"/> and <see cref="CopyNewestFirst"/>
/// are plain (non-locking) operations, for the widget that drains its pending
/// queue and snapshots on the user-interface thread only (the Render path).
/// The ring itself ships to render and tests needing a snapshot the same way
/// the book page does: by copy, bounded by the asked row count.
/// </remarks>
public sealed class TradeTapeRingBuffer
{
    #region Constants

    /// <summary>The capacity the panel ships with: enough history to scroll
    /// back through an ordinary session, small enough that a full copy stays
    /// trivially cheap (a bounded, array-backed structure, not a list).</summary>
    public const int DefaultCapacity = 4096;

    #endregion

    #region Fields & Properties

    private readonly MarketTrade[] _items;

    /// <summary>Index of the oldest carried trade.</summary>
    private int _head;

    private int _count;

    /// <summary>Gets the maximum number of trades the ring carries.</summary>
    public int Capacity => _items.Length;

    /// <summary>Gets the number of trades currently carried (grows to the capacity, then stays).</summary>
    public int Count => _count;

    #endregion

    #region Constructors

    /// <summary>
    /// Create a new TradeTapeRingBuffer instance.
    /// </summary>
    /// <param name="capacity">The fixed trade capacity; a non-positive value is an error, never a default.</param>
    public TradeTapeRingBuffer(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "The capacity must be positive.");
        }

        _items = new MarketTrade[capacity];
    }

    #endregion

    #region Public Methods

    /// <summary>
    /// Append one trade as the newest; pushing past the capacity evicts the
    /// oldest trade (Count then stays at the capacity forever).
    /// </summary>
    public void Push(MarketTrade trade)
    {
        if (trade is null)
        {
            throw new ArgumentNullException(nameof(trade));
        }

        if (_count < _items.Length)
        {
            _items[(_head + _count) % _items.Length] = trade;
            _count++;
            return;
        }

        _items[_head] = trade;
        _head = (_head + 1) % _items.Length;
    }

    /// <summary>
    /// Copy the carried trades newest first into <paramref name="target"/>
    /// (cleared first), no more than <paramref name="maxCount"/> of them —
    /// a frame asks only for the rows its height can show, so the per-frame
    /// cost stays bounded by the visible rows regardless of the ring's fill.
    /// </summary>
    public void CopyNewestFirst(List<MarketTrade> target, int maxCount)
    {
        if (target is null)
        {
            throw new ArgumentNullException(nameof(target));
        }

        if (maxCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCount), maxCount, "The count must not be negative.");
        }

        target.Clear();

        var wanted = Math.Min(_count, maxCount);
        for (var offset = 0; offset < wanted; offset++)
        {
            // Walk from the newest (head + count - 1, relative to the oldest
            // head) backward; the wanted prefix must be the NEWEST trades or
            // a partial copy serves the stale end of the ring.
            var index = (_head + _count - 1 - offset) % _items.Length;
            target.Add(_items[index]);
        }
    }

    /// <summary>Drop everything: the panel's clear button empties the tape.</summary>
    public void Clear()
    {
        _count = 0;
        _head = 0;
    }

    #endregion
}
