namespace ZettaTrade.Plugins.Rsi;

using ZettaTrade.Charting;
using ZettaTrade.Core.Entities;

/// <summary>
/// The relative strength index over the bars' closes (T-092 pack), Wilder's
/// conventions: the per-bar change is the close-to-close difference; the
/// first averages of gains and losses seed with the plain mean over the
/// first <c>period</c> changes (the classic seeded first average), then
/// every bar smooths through the Wilder form
/// <c>avg = (prev * (period - 1) + move) / period</c>. The index
/// <c>RSI = 100 * avgGain / (avgGain + avgLoss)</c>: a zero average loss
/// pins the reading to 100 (the classic zero-loss shortcut), a zero average
/// gain with a positive loss reads 0 by the same fraction. The bars before
/// the seed completes (the first <c>period</c>) are null. Pure and cheap:
/// the chart pipeline recomputes the series on every delivery and settings
/// change. The assembly ships as a bundled plugin: it implements the same
/// seam the SMA sample (T-034) implements and is resolved from the plugins
/// directory at runtime; the application carries no compile-time reference
/// to it.
/// </summary>
public sealed class RsiIndicator : IIndicator
{
    #region Constants

    /// <summary>Smallest period a construction accepts.</summary>
    public const int MinPeriod = 1;

    /// <summary>Largest period a construction accepts (guards runaway Computes).</summary>
    public const int MaxPeriod = 500;

    /// <summary>Period the parameterless construction applies (Wilder's classic 14).</summary>
    public const int DefaultPeriod = 14;

    /// <summary>Percentage scale the index reads against.</summary>
    public const decimal ScaleMax = 100m;

    /// <summary>The canonical name the catalog resolves the plugin under.</summary>
    public const string IndicatorName = "RSI";

    #endregion

    private readonly int _period;

    /// <summary>Parameterless construction: the classic Wilder 14 period (the plugin ctor convention's fallback).</summary>
    public RsiIndicator()
        : this(DefaultPeriod)
    {
    }

    /// <summary>
    /// Construct with an explicit period. An out-of-range period throws -
    /// the catalog skips the overlay row then, nothing defaults silently.
    /// </summary>
    public RsiIndicator(int period)
    {
        if (period < MinPeriod || period > MaxPeriod)
        {
            throw new ArgumentOutOfRangeException(
                nameof(period),
                period,
                $"The RSI period must range {MinPeriod}..{MaxPeriod}.");
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

        // The first averages seed with the plain mean of the first period
        // close-to-close changes (bars 1..period counted; bar 0 carries no
        // change). Until that seed bar the value is undefined (warmup).
        if (bars.Count <= _period)
        {
            return values;
        }

        var avgGain = 0m;
        var avgLoss = 0m;
        for (var i = 1; i <= _period; i++)
        {
            var change = bars[i].Close - bars[i - 1].Close;
            if (change > 0)
            {
                avgGain += change;
            }
            else
            {
                avgLoss -= change;
            }
        }

        avgGain /= _period;
        avgLoss /= _period;
        values[_period] = ReadIndex(avgGain, avgLoss);

        var smoothing = _period - 1;
        for (var i = _period + 1; i < bars.Count; i++)
        {
            var change = bars[i].Close - bars[i - 1].Close;
            avgGain = (avgGain * smoothing + (change > 0 ? change : 0)) / _period;
            avgLoss = (avgLoss * smoothing + (change < 0 ? -change : 0)) / _period;
            values[i] = ReadIndex(avgGain, avgLoss);
        }

        return values;
    }

    /// <summary>
    /// The index fraction: gains over the sum of both moves. A zero loss is
    /// the classic shortcut to 100 - the division by zero never substitutes
    /// a default.
    /// </summary>
    private static decimal ReadIndex(decimal avgGain, decimal avgLoss) =>
        avgLoss == 0 ? RsiIndicator.ScaleMax : RsiIndicator.ScaleMax * avgGain / (avgGain + avgLoss);
}
