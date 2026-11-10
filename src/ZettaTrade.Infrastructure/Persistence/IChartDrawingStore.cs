namespace ZettaTrade.Infrastructure.Persistence;

using ZettaTrade.Charting;
using ZettaTrade.Core.Enums;

/// <summary>
/// The persisted chart drawings' store contract (T-091): the annotations
/// of one (adapter, symbol, interval) key load and save as WHOLE layers —
/// the chart ViewModel owns the in-memory list against the standing key,
/// every commit/edit/delete rewrites it, and a rebase reloads it. The
/// layer is small (a trader's handful of drawings), so whole-layer saves
/// stay cheap and free the contract from per-drawing diffing.
/// </summary>
public interface IChartDrawingStore
{
    /// <summary>
    /// The key's persisted drawings, ordered by the first anchor's time
    /// (a fresh read paints the same order the trader drew). A faulted
    /// load refuses loudly through the task — the caller decides whether
    /// to stand empty (the drawings are a convenience, never a trading
    /// input).
    /// </summary>
    Task<IReadOnlyList<ChartDrawing>> LoadAsync(
        string adapterId,
        string symbol,
        TimeFrame timeFrame,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the key's whole layer with the given drawings (their
    /// optional identity rides the <see cref="ChartDrawing.Id"/>): an
    /// unset id gains a fresh store identity on the answer's order — the
    /// caller takes that identity back through the returned list to keep
    /// in-memory and persisted names aligned.
    /// </summary>
    Task<IReadOnlyList<ChartDrawing>> SaveAsync(
        string adapterId,
        string symbol,
        TimeFrame timeFrame,
        IReadOnlyList<ChartDrawing> drawings,
        CancellationToken cancellationToken = default);
}
