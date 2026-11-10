namespace ZettaTrade.Plugins.Ema;

using ZettaTrade.Plugins.Abstractions;

/// <summary>
/// The exponential moving average over the bars' closes (T-092 pack).
/// Formula: <c>alpha = 2 / (period + 1)</c>; the series seeds with the
/// simple mean of the closes over the first <c>period</c> bars (the
/// classic seeded first average), then every bar applies one alpha step.
/// The bars before the seed completes are null. Pure and cheap: the chart
/// pipeline recomputes the series on every delivery and settings change.
/// The assembly ships as a bundled plugin: it implements the same seam the
/// SMA sample (T-034) implements and is resolved from the plugins directory
/// at runtime; the application carries no compile-time reference to it.
/// </summary>
public sealed class EmaIndicator : IIndicator
{
    #region Constants

    /// <summary>Smallest period a construction accepts.</summary>
    public const int MinPeriod = 1;

    /// <summary>Largest period a construction accepts (guards runaway Computes).</summary>
    public const int MaxPeriod = 500;

    /// <summary>Period the parameterless construction applies.</summary>
    public const int DefaultPeriod = 20;

    /// <summary>The numerator of the classic smoothing constant alpha = 2 / (period + 1).</summary>
    public const decimal AlphaNumerator = 2m;

    /// <summary>The canonical name the catalog resolves the plugin under.</summary>
    public const string IndicatorName = "EMA";

    #endregion

    private readonly int _period;

    /// <summary>Parameterless construction: the classic default period (the plugin ctor convention's fallback).</summary>
    public EmaIndicator()
        : this(DefaultPeriod)
    {
    }

    /// <summary>
    /// Construct with an explicit period. An out-of-range period throws -
    /// the catalog skips the overlay row then, nothing defaults silently.
    /// </summary>
    public EmaIndicator(int period)
    {
        if (period < MinPeriod || period > MaxPeriod)
        {
            throw new ArgumentOutOfRangeException(
                nameof(period),
                period,
                $"The EMA period must range {MinPeriod}..{MaxPeriod}.");
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

        // The series' first computed value is the seed: the plain mean of
        // the first period closes, then one alpha step per bar.
        decimal ema = 0m;
        for (var i = 0; i < _period; i++)
        {
            ema += bars[i].Close;
        }

        ema /= _period;
        values[_period - 1] = ema;

        var alpha = AlphaNumerator / (_period + 1);
        for (var i = _period; i < bars.Count; i++)
        {
            ema += alpha * (bars[i].Close - ema);
            values[i] = ema;
        }

        return values;
    }
}
