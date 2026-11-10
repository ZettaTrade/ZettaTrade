namespace ZettaTrade.Plugins.Wma;

using ZettaTrade.Plugins.Abstractions;

/// <summary>
/// The linearly weighted moving average over the bars' closes (T-092 pack).
/// Formula: over the closing window of the last <c>period</c> bars the
/// weights run from <c>period</c> (the newest close, heaviest) down to
/// <c>1</c> (the oldest); the window sum divides by the weight sum
/// <c>period * (period + 1) / 2</c>. The bars before the window fills
/// (the warmup head) are null. Pure and cheap: the chart pipeline
/// recomputes the series on every delivery and settings change. The
/// assembly ships as a bundled plugin: it implements the same seam the
/// SMA sample (T-034) implements and is resolved from the plugins
/// directory at runtime; the application carries no compile-time
/// reference to it.
/// </summary>
public sealed class WmaIndicator : IIndicator
{
    #region Constants

    /// <summary>Smallest period a construction accepts.</summary>
    public const int MinPeriod = 1;

    /// <summary>Largest period a construction accepts (guards runaway Computes).</summary>
    public const int MaxPeriod = 500;

    /// <summary>Period the parameterless construction applies.</summary>
    public const int DefaultPeriod = 20;

    /// <summary>The canonical name the catalog resolves the plugin under.</summary>
    public const string IndicatorName = "WMA";

    #endregion

    private readonly int _period;

    /// <summary>Parameterless construction: the classic default period (the plugin ctor convention's fallback).</summary>
    public WmaIndicator()
        : this(DefaultPeriod)
    {
    }

    /// <summary>
    /// Construct with an explicit period. An out-of-range period throws -
    /// the catalog skips the overlay row then, nothing defaults silently.
    /// </summary>
    public WmaIndicator(int period)
    {
        if (period < MinPeriod || period > MaxPeriod)
        {
            throw new ArgumentOutOfRangeException(
                nameof(period),
                period,
                $"The WMA period must range {MinPeriod}..{MaxPeriod}.");
        }

        _period = period;
    }

    /// <inheritdoc />
    public string Name => IndicatorName;

    /// <inheritdoc />
    public IReadOnlyList<decimal?> Apply(IReadOnlyList<IndicatorBar> bars)
    {
        ArgumentNullException.ThrowIfNull(bars);

        var values = new decimal?[bars.Count];
        if (bars.Count < _period)
        {
            return values;
        }

        // The weight sum is the stable denominator of the linear ladder:
        // period + (period-1) + ... + 1. The weights themselves read off
        // the window offset (newest close weighs the period, the oldest
        // weighs one).
        var weightSum = (decimal)_period * (_period + 1) / 2;

        for (var i = _period - 1; i < bars.Count; i++)
        {
            var weighted = 0m;
            for (var offset = 0; offset < _period; offset++)
            {
                weighted += (_period - offset) * bars[i - offset].Close;
            }

            values[i] = weighted / weightSum;
        }

        return values;
    }
}
