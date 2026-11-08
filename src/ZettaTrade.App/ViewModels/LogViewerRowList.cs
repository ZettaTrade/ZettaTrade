namespace ZettaTrade.App.ViewModels;

using System;
using System.Collections.Generic;
using System.Collections.Specialized;

/// <summary>
/// The log viewer's bound surface (T-101): a fixed-capacity list whose
/// collection events ride ONE multi-element event per drained slice (the
/// Avalonia 12 virtualizing panel consumes multi-element Add/Remove
/// events, so a 50k-line flood storms the list with at most a handful of
/// events per drain tick — the smooth-tail contract's shape).
/// <para>
/// The ceiling the viewer applies to eviction lives at the tail service's
/// history ring; this list's caller keeps the view equal to the filtered
/// projection of the ring, applying the slices the ring publishes.
/// </para>
/// </summary>
public sealed class LogViewerRowList : IReadOnlyList<LogViewerRow>
{
    #region Private Fields

    private readonly List<LogViewerRow> _items = new();

    #endregion Private Fields

    #region Public API

    /// <summary>The collection-change event the virtualized list binds.</summary>
    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    /// <summary>The row count.</summary>
    public int Count => _items.Count;

    /// <summary>The row at one index.</summary>
    public LogViewerRow this[int index] => _items[index];

    /// <summary>The enumerator over the rows.</summary>
    public IEnumerator<LogViewerRow> GetEnumerator() => _items.GetEnumerator();

    /// <summary>The enumerator over the rows.</summary>
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>
    /// Replace the whole content in one <c>Reset</c> event (the seed and
    /// the filter rebuilds ride this — the virtualized panel re-realizes
    /// only its visible window, so a wholesale rebuild stays cheap).
    /// </summary>
    /// <param name="rows">The rows that become the content, oldest first.</param>
    public void Reset(IReadOnlyList<LogViewerRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        _items.Clear();
        _items.AddRange(rows);

        CollectionChanged?.Invoke(
            this,
            new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    /// <summary>
    /// Append a drained batch in one <c>Add</c> event (the batch's rows,
    /// oldest first; the append cost is one event, never one per line).
    /// </summary>
    /// <param name="batch">The rows to append, oldest first.</param>
    public void AppendBatch(IReadOnlyList<LogViewerRow> batch)
    {
        ArgumentNullException.ThrowIfNull(batch);

        if (batch.Count == 0)
        {
            return;
        }

        var start = _items.Count;
        foreach (var row in batch)
        {
            _items.Add(row);
        }

        CollectionChanged?.Invoke(
            this,
            new NotifyCollectionChangedEventArgs(
                NotifyCollectionChangedAction.Add,
                changedItems: new List<LogViewerRow>(batch),
                startingIndex: start));
    }

    /// <summary>
    /// Drop the oldest rows in one <c>Remove</c> event (the history ring's
    /// evictions mirrored: the view never outgrows the ring).
    /// </summary>
    /// <param name="count">How many front rows leave.</param>
    public void RemoveFromFront(int count)
    {
        if (count <= 0 || count > _items.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, "The removal count must match the view's head.");
        }

        List<LogViewerRow> removed = new(count);
        for (var i = 0; i < count; i++)
        {
            removed.Add(_items[i]);
        }

        _items.RemoveRange(0, count);

        CollectionChanged?.Invoke(
            this,
            new NotifyCollectionChangedEventArgs(
                NotifyCollectionChangedAction.Remove,
                changedItems: removed,
                startingIndex: 0));
    }

    #endregion Public API
}
