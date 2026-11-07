namespace ZettaTrade.Plugins.Atr;

using ZettaTrade.Charting;
using ZettaTrade.Core.Entities;

/// <summary>
/// The average true range over the bars (T-092 pack), Wilder's smoothed
/// form. The true range of a bar is the largest of <c>high - low</c>,
/// <c>|high - prior close|</c> and <c>|low - prior close|</c>; the very
/// first bar carries no prior close, so its true range is the plain
/// <c>high - low</c> range (Wilder's first-day convention). The series
/// seeds with the plain mean of the true ranges over the first
/// <c>period</c> bars (the classic seeded first average) and then every
/// bar smooths through the Wilder form
/// <c>atr = (prev * (period - 1) + trueRange) / period</c>. The bars
/// before the seed completes (the first <c>period - 1</c>) are null. Pure
/// and cheap: the chart pipeline recomputes the series on every delivery
/// and settings change. The assembly ships as a bundled plugin: it
/// implements the same seam the SMA sample (T-034) implements and is
/// resolved from the plugins directory at runtime; the application carries
/// no compile-time reference to it.
/// </summary>
public sealed class AtrIndicator : IIndicator
{
    #region Constants

    /// <summary>Smallest period a construction accepts.</summary>
    public const int MinPeriod = 1;

    /// <summary>Largest period a construction accepts (guards runaway Computes).</summary>
    public const int MaxPeriod = 500;

    /// <summary>Period the parameterless construction applies (Wilder's classic 14).</summary>
    public const int DefaultPeriod = 14;

    /// <summary>The canonical name the catalog resolves the plugin under.</summary>
    public const string IndicatorName = "ATR";

    #endregion

    private readonly int _period;

    /// <summary>Parameterless construction: the classic Wilder 14 period (the plugin ctor convention's fallback).</summary>
    public AtrIndicator()
        : this(DefaultPeriod)
    {
    }

    /// <summary>
    /// Construct with an explicit period. An out-of-range period throws -
    /// the catalog skips the overlay row then, nothing defaults silently.
    /// </summary>
    public AtrIndicator(int period)
    {
        if (period < MinPeriod || period > MaxPeriod)
        {
            throw new ArgumentOutOfRangeException(
                nameof(period),
                period,
                $"The ATR period must range {MinPeriod}..{MaxPeriod}.");
        }

        _period = period;
    }

    /// <inheritdoc />
    public string Name => IndicatorName;

    /// <inheritdoc />
    public IReadOnlyList<decimal?> Apply(IReadOnlyList<Bar> bars)
    {
        ArgumentNullException.ThrowIfNull(bars);

        var values = new decimal?[bars.Count];
        if (bars.Count < _period)
        {
            return values;
        }

        // The seed is the plain mean of the first period true ranges (the
        // opening bar's range stands in for the missing prior close), then
        // one Wilder smoothing step per bar.
        var atr = 0m;
        for (var i = 0; i < _period; i++)
        {
            atr += TrueRange(bars, i);
        }

        atr /= _period;
        values[_period - 1] = atr;

        var smoothing = _period - 1;
        for (var i = _period; i < bars.Count; i++)
        {
            atr = (atr * smoothing + TrueRange(bars, i)) / _period;
            values[i] = atr;
        }

        return values;
    }

    /// <summary>
    /// The bar's true range: the widest of the bar's own spread and the two
    /// gaps against the prior close; the opening bar's spread stands alone
    /// (nothing to gap against yet).
    /// </summary>
    private static decimal TrueRange(IReadOnlyList<Bar> bars, int index)
    {
        var range = bars[index].High - bars[index].Low;
        if (index == 0)
        {
            return range;
        }

        var priorClose = bars[index - 1].Close;
        var gapUp = Math.Abs(bars[index].High - priorClose);
        var gapDown = Math.Abs(bars[index].Low - priorClose);
        return Math.Max(range, Math.Max(gapUp, gapDown));
    }
}
