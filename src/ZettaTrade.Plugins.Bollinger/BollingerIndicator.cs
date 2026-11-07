namespace ZettaTrade.Plugins.Bollinger;

using ZettaTrade.Charting;
using ZettaTrade.Core.Entities;

/// <summary>
/// The Bollinger band indicator over the bars' closes (T-092 pack), the
/// classic form: the middle band is the simple mean of the closes over the
/// last <c>period</c> bars and the bands sit <c>k</c> standard deviations
/// around it - <c>middle + k * sigma</c> above, <c>middle - k * sigma</c>
/// below, with the deviation of the same window read as the POPULATION
/// spread (the sum of squared carry-overs divides by the window length,
/// Bollinger's own convention). The line <see cref="IIndicator.Apply"/>
/// renders is the UPPER band - through the single-series seam (T-034) one
/// overlay carries one line, and the upper band is the reading every other
/// band derives from: the middle is exactly the SMA the sample plugin
/// plots and the lower band mirrors the upper around the middle. The bars
/// before the window fills (the warmup head) are null. Pure and cheap: the
/// chart pipeline recomputes the series on every delivery and settings
/// change. The assembly ships as a bundled plugin: it implements the same
/// seam the SMA sample (T-034) implements and is resolved from the plugins
/// directory at runtime; the application carries no compile-time reference
/// to it.
/// </summary>
/// <remarks>
/// Period parameter surface: the settings row carries ONE integer
/// (T-034's seam convention), so the catalog-facing construction is
/// <c>ctor(int)</c> over the window length while the deviation count keeps
/// its classic 2 - the row knob configures the smoothing, the bands stay
/// classic. The full pair (length, deviation count) stays constructable
/// for embeddings and the tests. A non-positive deviation count throws -
/// the catalog skips the overlay row then, nothing defaults silently.
/// </remarks>
public sealed class BollingerIndicator : IIndicator
{
    #region Constants

    /// <summary>Smallest window a construction accepts.</summary>
    public const int MinPeriod = 1;

    /// <summary>Largest window a construction accepts (guards runaway Computes).</summary>
    public const int MaxPeriod = 500;

    /// <summary>Window the parameterless construction applies.</summary>
    public const int DefaultPeriod = 20;

    /// <summary>Deviation count the classic two-band definition pins.</summary>
    public const decimal CanonicalDeviationCount = 2m;

    /// <summary>The canonical name the catalog resolves the plugin under.</summary>
    public const string IndicatorName = "BOLL";

    #endregion

    private readonly int _period;
    private readonly decimal _deviationCount;

    /// <summary>Parameterless construction: the classic 20/2 pair (the plugin ctor convention's fallback).</summary>
    public BollingerIndicator()
        : this(DefaultPeriod)
    {
    }

    /// <summary>
    /// Construct with an explicit window: the length the row's period knob
    /// configures, the deviation count stays classic. An out-of-range
    /// window throws - the catalog skips the overlay row then, nothing
    /// defaults silently.
    /// </summary>
    public BollingerIndicator(int period)
        : this(period, CanonicalDeviationCount)
    {
    }

    /// <summary>
    /// Construct the full pair: the window length and the deviation count
    /// the bands sit at. A window outside <see cref="MinPeriod"/>..<see
    /// cref="MaxPeriod"/> or a non-positive deviation count throws.
    /// </summary>
    public BollingerIndicator(
        int period,
        decimal deviationCount)
    {
        if (period < MinPeriod || period > MaxPeriod)
        {
            throw new ArgumentOutOfRangeException(
                nameof(period),
                period,
                $"The Bollinger window must range {MinPeriod}..{MaxPeriod}.");
        }

        if (deviationCount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(deviationCount),
                deviationCount,
                "The Bollinger deviation count must be positive.");
        }

        _period = period;
        _deviationCount = deviationCount;
    }

    /// <inheritdoc />
    public string Name => IndicatorName;

    /// <inheritdoc />
    public IReadOnlyList<decimal?> Apply(IReadOnlyList<Bar> bars)
    {
        ArgumentNullException.ThrowIfNull(bars);

        var values = new decimal?[bars.Count];

        for (var i = _period - 1; i < bars.Count; i++)
        {
            var middle = 0m;
            for (var offset = 0; offset < _period; offset++)
            {
                middle += bars[i - offset].Close;
            }

            middle /= _period;

            // The population deviation of the same window: the squared
            // carry-overs sum over the window length (Bollinger's own
            // convention) - n, not the unbiased n-1.
            var squares = 0m;
            for (var offset = 0; offset < _period; offset++)
            {
                var carry = bars[i - offset].Close - middle;
                squares += carry * carry;
            }

            var deviation = (decimal)Math.Sqrt((double)(squares / _period));
            values[i] = middle + _deviationCount * deviation;
        }

        return values;
    }
}
