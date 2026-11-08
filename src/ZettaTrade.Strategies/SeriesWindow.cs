namespace ZettaTrade.Strategies;

using ZettaTrade.Core.Entities;

/// <summary>
/// The retained candle window of ONE (symbol, interval) series inside the
/// host: a fixed-capacity ring that keeps the last <c>capacity</c> closed
/// bars, oldest first for the snapshot. The host's pump is single-threaded
/// (one evaluation loop), so the ring needs no synchronization - its
/// contract is simply append-accepted-bars and snapshot per evaluation.
/// </summary>
internal sealed class SeriesWindow
{
    private readonly Bar[] _bars;
    private readonly int _capacity;
    private int _count;
    private int _head;

    /// <summary>Build a ring holding at least one bar.</summary>
    /// <param name="capacity">The retained bar count (the host's window length).</param>
    public SeriesWindow(int capacity)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "The window retains at least one bar.");
        }

        _capacity = capacity;
        _bars = new Bar[capacity];
    }

    /// <summary>
    /// Whether the bar advances the series: the first bar ever is
    /// accepted, anything at or before the last accepted open time is not
    /// (a late or duplicate closed candle is ignored, never re-evaluated).
    /// </summary>
    /// <param name="bar">The candidate closed bar.</param>
    public bool IsAdvancing(Bar bar) =>
        _count == 0 || _bars[LastIndex].Time < bar.Time;

    /// <summary>Append one candle the <see cref="IsAdvancing"/> check accepted.</summary>
    /// <param name="bar">The closed bar to append.</param>
    public void Append(Bar bar)
    {
        var writeIndex = (_head + _count) % _capacity;
        _bars[writeIndex] = bar;

        if (_count < _capacity)
        {
            _count++;
        }
        else
        {
            _head = (_head + 1) % _capacity;
        }
    }

    /// <summary>The retained window as an oldest-first array (a private copy per evaluation).</summary>
    public Bar[] Snapshot()
    {
        var snapshot = new Bar[_count];
        for (var i = 0; i < _count; i++)
        {
            snapshot[i] = _bars[(_head + i) % _capacity];
        }

        return snapshot;
    }

    private int LastIndex => (_head + _count - 1) % _capacity;
}
