namespace ZettaTrade.Backtest;

/// <summary>
/// One grid axis of a parameter sweep (T-212): the knob it varies and the
/// values it takes, in the order the parameterizations enumerate. The values
/// are kept exactly as declared — a duplicate spelling produces a duplicate
/// parameterization whose ranking tie breaks by enumeration order, nothing
/// is deduplicated or reordered behind the caller's back.
/// </summary>
/// <param name="Parameter">The swept knob.</param>
/// <param name="Values">The knob's values, declaration order (the enumeration order of the grid).</param>
public sealed record BacktestSweepAxis(BacktestSweepParameter Parameter, IReadOnlyList<decimal> Values)
{
    #region Constants

    /// <summary>The largest value count one axis accepts: the sanity ceiling
    /// keeps the grid (both axes multiplied) bounded and the sweep's runtime
    /// predictable — a settings typo ("1,2,3,…" forever) must not pin an
    /// unbounded load.</summary>
    public const int MaxValuesPerAxis = 8;

    #endregion Constants

    #region Validation

    /// <summary>
    /// Refuse an invalid axis before any run starts: nothing here is ever
    /// defaulted, corrected or clamped silently — the standing rule.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The parameter is not a defined knob, or the value count leaves 1..<see cref="MaxValuesPerAxis"/>.</exception>
    /// <exception cref="ArgumentNullException">The values list is missing.</exception>
    public void Validate()
    {
        if (!Enum.IsDefined(Parameter))
        {
            throw new ArgumentOutOfRangeException(
                nameof(Parameter),
                Parameter,
                "The sweep parameter is not a defined knob.");
        }

        ArgumentNullException.ThrowIfNull(Values);

        if (Values.Count == 0 || Values.Count > MaxValuesPerAxis)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Values),
                Values.Count,
                $"A sweep axis carries {Values.Count} values; the sweep accepts 1..{MaxValuesPerAxis} per axis.");
        }

        foreach (var value in Values)
        {
            ValidateValue(Parameter, value);
        }
    }

    /// <summary>Refuse one value outside its knob's own request bounds (the exact bounds <see cref="BacktestRunRequest.Validate"/> enforces).</summary>
    internal static void ValidateValue(BacktestSweepParameter parameter, decimal value)
    {
        switch (parameter)
        {
            case BacktestSweepParameter.LongNotionalFraction when value <= 0m || value > 1m:
                throw new ArgumentOutOfRangeException(
                    nameof(Values),
                    value,
                    "The long notional fraction must lie in (0, 1].");
            case BacktestSweepParameter.SlippageValue when value < 0m:
                throw new ArgumentOutOfRangeException(
                    nameof(Values),
                    value,
                    "The slippage magnitude must be non-negative.");
        }
    }

    #endregion Validation
}
