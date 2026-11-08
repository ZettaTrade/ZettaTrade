namespace ZettaTrade.App.Services;

/// <summary>
/// The alert edge seam (T-096): the ONE place the trigger semantics are
/// decided, so "edge semantics exact (above/at/below)" stays a named,
/// documented decision rather than an accidental epsilon. Every evaluator is
/// a pure function of the observed values; an unseen trigger is an argument
/// fault — invalid values are errors, never defaults (the trading-safety
/// canon applied to the alerts module).
/// </summary>
public static class AlertEdge
{
    /// <summary>
    /// Is one observed price inside one exact Price edge? Above is STRICTLY
    /// greater (a tick exactly at the target is not above); Below is STRICTLY
    /// less; At is EXACT decimal equality — no tolerance in either direction.
    /// </summary>
    public static bool IsLevelHit(AlertTrigger trigger, decimal observedPrice, decimal target) => trigger switch
    {
        AlertTrigger.Above => observedPrice > target,
        AlertTrigger.Below => observedPrice < target,
        AlertTrigger.At => observedPrice == target,
        _ => throw new ArgumentOutOfRangeException(nameof(trigger), trigger, "The trigger names no known edge."),
    };

    /// <summary>
    /// The signed relative move of the observed price against a reference
    /// price, in percent: <c>(current / reference - 1) * 100</c> — positive
    /// when the price grew. The reference must be strictly positive (the
    /// ratio is undefined otherwise, and a nonpositive reference is a fault,
    /// never a coerced zero-move).
    /// </summary>
    public static decimal SignedMovePercent(decimal reference, decimal current)
    {
        if (reference <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(reference), reference, "The reference price must be strictly positive.");
        }

        return (current / reference - 1m) * 100m;
    }

    /// <summary>
    /// Does one reference-to-observed pair meet the move threshold? The move's
    /// magnitude compares with the non-negative threshold by EXACT OR GREATER
    /// — the equality boundary belongs to the alert (a move of exactly the
    /// threshold fires; the operator never waits for a sub-tick amount the
    /// stream cannot print).
    /// </summary>
    public static bool MoveMeetsThreshold(decimal reference, decimal current, decimal thresholdPercent)
    {
        if (thresholdPercent <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(thresholdPercent), thresholdPercent, "The percent threshold must be strictly positive.");
        }

        return Math.Abs(SignedMovePercent(reference, current)) >= thresholdPercent;
    }
}
