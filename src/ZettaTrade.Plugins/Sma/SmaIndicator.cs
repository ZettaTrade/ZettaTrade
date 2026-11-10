namespace ZettaTrade.Plugins.Sma;

using ZettaTrade.Charting;
using ZettaTrade.Core.Entities;

/// <summary>
/// The simple moving average over the bars' closes: the mean of the
/// <see cref="_period"/> closes ending at each bar; the warmup head (the
/// bars before the period is filled) is null. Pure and cheap: the chart
/// pipeline recomputes the series on every delivery and settings change.
/// This assembly is the plugin-first proof for indicators (T-034): it is
/// resolved from the plugins directory at runtime, the application carries
/// no compile-time reference to it.
/// </summary>
public sealed class SmaIndicator : IIndicator
{
    #region Constants

    /// <summary>Smallest period a construction accepts.</summary>
    public const int MinPeriod = 1;

    /// <summary>Largest period a construction accepts (guards runaway Computes).</summary>
    public const int MaxPeriod = 500;

    /// <summary>Period the parameterless construction applies.</summary>
    public const int DefaultPeriod = 20;

    /// <summary>The canonical name the catalog resolves the plugin under.</summary>
    public const string IndicatorName = "SMA";

    #endregion

    private readonly int _period;

    /// <summary>Parameterless construction: the classic default period (the plugin ctor convention's fallback).</summary>
    public SmaIndicator()
        : this(DefaultPeriod)
    {
    }

    /// <summary>
    /// Construct with an explicit period. An out-of-range period throws -
    /// the catalog skips the overlay row then, nothing defaults silently.
    /// </summary>
    public SmaIndicator(int period)
    {
        if (period < MinPeriod || period > MaxPeriod)
        {
            throw new ArgumentOutOfRangeException(
                nameof(period),
                period,
                $"The SMA period must range {MinPeriod}..{MaxPeriod}.");
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
        decimal windowSum = 0m;

        for (var i = 0; i < bars.Count; i++)
        {
            windowSum += bars[i].Close;

            if (i >= _period)
            {
                windowSum -= bars[i - _period].Close;
            }

            if (i >= _period - 1)
            {
                values[i] = windowSum / _period;
            }
        }

        return values;
    }
}
