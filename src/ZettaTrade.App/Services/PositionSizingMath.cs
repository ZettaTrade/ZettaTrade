namespace ZettaTrade.App.Services;

/// <summary>
/// Pure sizing math of the position sizing helper — the golden-tested core
/// the ticket's quick-size row rides. Every function is total and guarded:
/// callers must not pass underdetermined values (the row's can-execute
/// gates do exactly that), and an invalid argument is an error, never a
/// substituted default. Rounding policy is ONE and documented: proposals
/// FLOOR onto the instrument's lot step, so a proposed size can never
/// overshoot the balance or the risk budget the user sized against.
/// </summary>
public static class PositionSizingMath
{
    #region Constants

    /// <summary>
    /// Denominator helper for percent inputs: a "risk percent" is a share of
    /// the available balance, 100 meaning the whole of it.
    /// </summary>
    private const decimal FullPercent = 100m;

    #endregion Constants

    #region Public Methods

    /// <summary>
    /// Floor a raw quantity onto the instrument's lot step: the largest
    /// step-grid quantity that does not exceed the raw value. Flooring is
    /// the deliberate rounding direction — under-proposing is harmless
    /// (the user can always type more), overshooting the balance or the
    /// risk budget is not. A divide that drifts by decimal precision is
    /// corrected by stepping up while the floor still fits the raw value,
    /// so the result always satisfies the invariant
    /// <c>0 ≤ result ≤ raw</c> and the remainder <c>raw - result</c>
    /// stays strictly below the step.
    /// </summary>
    /// <param name="rawQuantity">The unrounded raw proposal (zero or positive).</param>
    /// <param name="quantityStep">The instrument's positive lot step.</param>
    /// <returns>The floored proposal on the step grid.</returns>
    public static decimal FloorToStep(decimal rawQuantity, decimal quantityStep)
    {
        if (quantityStep <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(quantityStep), quantityStep, "The quantity step must be positive.");
        }

        if (rawQuantity < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(rawQuantity), rawQuantity, "A raw quantity must not be negative.");
        }

        var floored = decimal.Floor(rawQuantity / quantityStep) * quantityStep;

        // Correct decimal-division drift: a quotient rounded down left one
        // fitting step behind. Bounded (each pass advances by at least one
        // step), usually zero passes.
        while (floored + quantityStep <= rawQuantity)
        {
            floored += quantityStep;
        }

        return floored;
    }

    /// <summary>
    /// The quantity a balance-share preset of the BASE asset proposes for a
    /// sell: a fraction of the base amount the snapshot captured (selling
    /// spends base custody directly, no price involved). The caller floors
    /// the result onto the instrument's step.
    /// </summary>
    /// <param name="availableBase">The captured base-asset amount (zero or positive).</param>
    /// <param name="fraction">The preset share, in (0; 1].</param>
    /// <returns>The raw (unfloored) proposal.</returns>
    public static decimal BalanceSellQuantity(decimal availableBase, decimal fraction)
    {
        if (availableBase < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(availableBase), availableBase, "A captured base amount must not be negative.");
        }

        if (fraction is <= 0m or > 1m)
        {
            throw new ArgumentOutOfRangeException(nameof(fraction), fraction, "A preset share lives in (0; 1].");
        }

        return availableBase * fraction;
    }

    /// <summary>
    /// The quantity a balance-share preset of the QUOTE asset proposes for a
    /// buy: a fraction of the captured quote amount converted at the entry
    /// price estimate (spending quote custody the market quotes). The caller
    /// floors the result onto the instrument's step.
    /// </summary>
    /// <param name="availableQuote">The captured quote-asset amount (zero or positive).</param>
    /// <param name="fraction">The preset share, in (0; 1].</param>
    /// <param name="entryPrice">The positive entry price the conversion divides by.</param>
    /// <returns>The raw (unfloored) proposal.</returns>
    public static decimal BalanceBuyQuantity(
        decimal availableQuote,
        decimal fraction,
        decimal entryPrice)
    {
        if (availableQuote < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(availableQuote), availableQuote, "A captured quote amount must not be negative.");
        }

        if (fraction is <= 0m or > 1m)
        {
            throw new ArgumentOutOfRangeException(nameof(fraction), fraction, "A preset share lives in (0; 1].");
        }

        if (entryPrice <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(entryPrice), entryPrice, "An entry price must be positive.");
        }

        return availableQuote * fraction / entryPrice;
    }

    /// <summary>
    /// The quantity a risk-percent proposal commands: risking a percent of
    /// the captured quote balance across the stop distance. The distance is
    /// per-unit money at risk, so dividing it into the risk budget yields
    /// the quantity. The caller floors the result onto the instrument's
    /// step.
    /// </summary>
    /// <param name="availableQuote">The captured quote-asset amount (zero or positive).</param>
    /// <param name="riskPercent">The risk share as a percent of the balance (positive; 1 == 1 %).</param>
    /// <param name="stopDistance">The positive per-unit stop distance (|entry − stop|).</param>
    /// <returns>The raw (unfloored) proposal.</returns>
    public static decimal RiskQuantity(
        decimal availableQuote,
        decimal riskPercent,
        decimal stopDistance)
    {
        if (availableQuote < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(availableQuote), availableQuote, "A captured quote amount must not be negative.");
        }

        if (riskPercent <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(riskPercent), riskPercent, "A risk percent must be positive.");
        }

        if (stopDistance <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(stopDistance), stopDistance, "A stop distance must be positive.");
        }

        return availableQuote * (riskPercent / FullPercent) / stopDistance;
    }

    #endregion Public Methods
}
