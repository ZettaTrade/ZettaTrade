namespace ZettaTrade.Charting;

using ZettaTrade.Core.Entities;

/// <summary>
/// One indicator implementation the charting seam renders as a line overlay
/// (T-034): a plugin assembly offers concrete implementations the
/// <see cref="IIndicatorCatalog"/> resolves by <see cref="Name"/> for a
/// configured period. <see cref="Apply"/> must be pure and cheap - the chart
/// pipeline recomputes it on every bars delivery, every older-history
/// prepend and every indicator settings change.
/// </summary>
public interface IIndicator
{
    /// <summary>Canonical indicator name, fixed by the implementation (e.g. "SMA").</summary>
    string Name { get; }

    /// <summary>
    /// Compute the indicator's series over the given bars: one value per
    /// bar, aligned by index. The warmup stretch an implementation cannot
    /// compute yet (the head of a moving average over its period, for
    /// example) is null. The input is the ascending open-time candle series
    /// the chart holds; the output never mutates it.
    /// </summary>
    IReadOnlyList<decimal?> Apply(IReadOnlyList<Bar> bars);
}
