namespace ZettaTrade.Backtest;

/// <summary>
/// The deterministic input of one parameter sweep (T-212): a fully valid run
/// request as the template (the strategy, the cached series identity, the
/// bar target, the seed and the base policy) plus two grid axes naming two
/// DIFFERENT knobs of the request. The sweep enumerates the cartesian
/// product row-major — the first axis outer, the second axis inner, the
/// values in declaration order — and one parameterization is the template
/// with both axes' knobs set to the grid cell's pair.
/// </summary>
public sealed record BacktestSweepRequest
{
    #region Required Identity

    /// <summary>The fully valid run request every parameterization starts from (the unswept knobs come from here).</summary>
    public required BacktestRunRequest Template { get; init; }

    /// <summary>The outer grid axis (the enumeration's slower dimension).</summary>
    public required BacktestSweepAxis FirstAxis { get; init; }

    /// <summary>The inner grid axis (the enumeration's faster dimension).</summary>
    public required BacktestSweepAxis SecondAxis { get; init; }

    #endregion Required Identity

    #region Validation

    /// <summary>
    /// Refuse an invalid sweep before any run starts: the template and both
    /// axes validate on their own, and the two axes must name different
    /// knobs — a duplicate-knob grid is a diagonal, not a grid, and is
    /// refused outright (the standing refuse-not-default rule).
    /// </summary>
    /// <exception cref="ArgumentNullException">The template or an axis is missing.</exception>
    /// <exception cref="ArgumentException">The two axes name the same knob.</exception>
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Template);
        Template.Validate();
        FirstAxis.Validate();
        SecondAxis.Validate();

        if (FirstAxis.Parameter == SecondAxis.Parameter)
        {
            throw new ArgumentException(
                "The two sweep axes must name different parameters; a duplicate-knob grid is refused, never collapsed.");
        }
    }

    #endregion Validation

    #region Parameterization

    /// <summary>
    /// Compose one parameterization's run request: the template with the
    /// first axis' knob set to <paramref name="firstValue"/> and the second
    /// axis' knob set to <paramref name="secondValue"/>. The unswept knobs
    /// (strategy, series identity, bars, seed, initial balance) ride the
    /// template unchanged, so every parameterization replays the same
    /// strategy over the same window with the same base policy.
    /// </summary>
    public BacktestRunRequest Apply(decimal firstValue, decimal secondValue) =>
        WithKnob(WithKnob(Template, FirstAxis.Parameter, firstValue), SecondAxis.Parameter, secondValue);

    private static BacktestRunRequest WithKnob(BacktestRunRequest template, BacktestSweepParameter parameter, decimal value) =>
        parameter switch
        {
            BacktestSweepParameter.LongNotionalFraction => template with { LongNotionalFraction = value },
            BacktestSweepParameter.SlippageValue => template with { SlippageValue = value },
            _ => throw new InvalidOperationException(
                $"The sweep parameter '{parameter}' is not a defined knob; the parameterization is refused, never defaulted."),
        };

    #endregion Parameterization
}
