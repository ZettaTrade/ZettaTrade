namespace ZettaTrade.Plugins.Macd;

using ZettaTrade.Plugins.Abstractions;

/// <summary>
/// The moving average convergence/divergence over the bars' closes
/// (T-092 pack), the classic Appel definition. The trio of exponential
/// averages - fast, slow and the signal-of-the-line - seeds through the
/// plain mean of the first bars of each length (the classic seeded first
/// average), then every bar applies one alpha step with
/// <c>alpha = 2 / (length + 1)</c>. The line <see cref="IIndicator.Apply"/>
/// renders is the fast EMA minus the slow EMA; the other two classic
/// outputs build on it without touching the single-series seam: the signal
/// is the EMA of the line over the signal length and the histogram is the
/// line minus its signal. The bars before the slow EMA's seed completes
/// (the first <c>slow - 1</c> bars) are null - the pair needs both
/// averages before the spread exists.
/// </summary>
/// <remarks>
/// Period parameter surface: the settings row carries ONE integer
/// (T-034's seam convention), so the catalog-facing construction is
/// <c>ctor(int)</c> over the fast length while the slow and signal lengths
/// keep their canonical 26/9 - the row knob configures the smallest of the
/// three averages, every other length stays classic. The full trio stays
/// constructable for embeddings and the tests. A fast length at or beyond
/// the slow length collapses the spread and throws - the catalog skips
/// the overlay row then, nothing defaults silently.
/// </remarks>
public sealed class MacdIndicator : IIndicator
{
    #region Constants

    /// <summary>Smallest construction length any of the three averages accepts.</summary>
    public const int MinPeriod = 1;

    /// <summary>Largest construction length any of the three averages accepts.</summary>
    public const int MaxPeriod = 500;

    /// <summary>Fast length the parameterless construction applies (the classic trio's shortest).</summary>
    public const int CanonicalFastPeriod = 12;

    /// <summary>Slow length the classic trio pins.</summary>
    public const int CanonicalSlowPeriod = 26;

    /// <summary>Signal length the classic trio pins.</summary>
    public const int CanonicalSignalPeriod = 9;

    /// <summary>
    /// Largest fast length the catalog-facing ctor accepts: the fast EMA
    /// must stay faster than the canonical slow (26) for the spread to
    /// exist at all.
    /// </summary>
    public const int MaxFastPeriod = CanonicalSlowPeriod - 1;

    /// <summary>The numerator of the classic smoothing constant alpha = 2 / (length + 1).</summary>
    public const decimal AlphaNumerator = 2m;

    /// <summary>The canonical name the catalog resolves the plugin under.</summary>
    public const string IndicatorName = "MACD";

    #endregion

    private readonly int _fastPeriod;
    private readonly int _slowPeriod;

    /// <summary>Parameterless construction: the classic trio 12/26/9 (the plugin ctor convention's fallback).</summary>
    public MacdIndicator()
        : this(CanonicalFastPeriod, CanonicalSlowPeriod, CanonicalSignalPeriod)
    {
    }

    /// <summary>
    /// The catalog-facing construction: the fast length is the row's period
    /// knob, the slow and signal lengths keep the classic 26/9. A fast
    /// length outside <c>1..25</c> throws.
    /// </summary>
    public MacdIndicator(int fastPeriod)
        : this(
            fastPeriod,
            CanonicalSlowPeriod,
            CanonicalSignalPeriod)
    {
    }

    /// <summary>
    /// Construct the full classic trio. Every length must range
    /// <see cref="MinPeriod"/>..<see cref="MaxPeriod"/> and the fast length
    /// must stay below the slow one; a violation throws.
    /// </summary>
    public MacdIndicator(
        int fastPeriod,
        int slowPeriod,
        int signalPeriod)
    {
        if (fastPeriod < MinPeriod || fastPeriod > MaxPeriod)
        {
            throw new ArgumentOutOfRangeException(
                nameof(fastPeriod),
                fastPeriod,
                $"The MACD lengths must range {MinPeriod}..{MaxPeriod}.");
        }

        if (slowPeriod < MinPeriod || slowPeriod > MaxPeriod)
        {
            throw new ArgumentOutOfRangeException(
                nameof(slowPeriod),
                slowPeriod,
                $"The MACD lengths must range {MinPeriod}..{MaxPeriod}.");
        }

        if (signalPeriod < MinPeriod || signalPeriod > MaxPeriod)
        {
            throw new ArgumentOutOfRangeException(
                nameof(signalPeriod),
                signalPeriod,
                $"The MACD lengths must range {MinPeriod}..{MaxPeriod}.");
        }

        if (fastPeriod >= slowPeriod)
        {
            throw new ArgumentException(
                $"The MACD fast length ({fastPeriod}) must stay below the slow one ({slowPeriod}).",
                nameof(fastPeriod));
        }

        _fastPeriod = fastPeriod;
        _slowPeriod = slowPeriod;
    }

    /// <inheritdoc />
    public string Name => IndicatorName;

    /// <inheritdoc />
    public IReadOnlyList<decimal?> Apply(IReadOnlyList<IndicatorBar> bars)
    {
        ArgumentNullException.ThrowIfNull(bars);

        var values = new decimal?[bars.Count];

        // The spread exists only once BOTH averages are seeded: below the
        // slow length the series is pure warmup (the fast seed alone never
        // renders a spread, nothing defaults).
        if (bars.Count < _slowPeriod)
        {
            return values;
        }

        var closes = new decimal[bars.Count];
        for (var i = 0; i < bars.Count; i++)
        {
            closes[i] = bars[i].Close;
        }

        var fast = Exponential(closes, _fastPeriod);
        var slow = Exponential(closes, _slowPeriod);

        // The spread exists only once BOTH averages are seeded; the slow
        // seed is the later one, so it owns the warmup head.
        for (var i = _slowPeriod - 1; i < bars.Count; i++)
        {
            values[i] = fast[i] - slow[i];
        }

        return values;
    }

    #region Private Static Methods

    /// <summary>
    /// The classic seeded EMA over the same close array: the plain mean of
    /// the first <c>period</c> closes is the seed, then one alpha step per
    /// bar. Every position of the returned array is a decimal (the warmup
    /// tail of this helper does not exist - callers own the nulls).
    /// </summary>
    private static decimal[] Exponential(decimal[] closes, int period)
    {
        var ema = new decimal[closes.Length];

        var seed = 0m;
        for (var i = 0; i < period; i++)
        {
            seed += closes[i];
        }

        ema[period - 1] = seed / period;
        var alpha = AlphaNumerator / (period + 1);
        for (var i = period; i < closes.Length; i++)
        {
            ema[i] = ema[i - 1] + alpha * (closes[i] - ema[i - 1]);
        }

        return ema;
    }

    #endregion
}
