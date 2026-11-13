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

    /// <summary>
    /// Does the indicator's value cross the fixed level between two
    /// consecutive closes (T-203)? Above is a cross from STRICTLY below to
    /// AT-OR-ABOVE (a value already at the level then rising is not a cross —
    /// it never left it), Below the mirror; the equality boundary belongs to
    /// the arriving close, the same discipline
    /// <see cref="MoveMeetsThreshold"/> keeps. An uncomputable value on either
    /// side (the indicator's warmup stretch reads null) is an honest absence —
    /// the cross waits for two computable consecutive closes, nothing fires.
    /// The At edge is meaningless for a cross and is an argument fault.
    /// </summary>
    public static bool IsLevelCrossHit(AlertTrigger edge, decimal? previous, decimal? current, decimal level)
    {
        if (edge is not (AlertTrigger.Above or AlertTrigger.Below))
        {
            throw new ArgumentOutOfRangeException(nameof(edge), edge, "A cross reads only the Above or Below edge.");
        }

        if (previous is not { } from || current is not { } to)
        {
            return false;
        }

        return edge switch
        {
            AlertTrigger.Above => from < level && to >= level,
            AlertTrigger.Below => from > level && to <= level,
            _ => false,
        };
    }

    /// <summary>
    /// Does the close price cross the indicator's value between two
    /// consecutive closes (T-203, the price source)? The price is judged
    /// against the indicator's own value at the SAME close — the moving level
    /// the MA draws — so the crossing is the sign flip of
    /// <c>close - indicator</c>: Above is a flip from strictly negative to
    /// at-or-positive, Below the mirror. An uncomputable indicator value or a
    /// missing close on either side is an honest absence — nothing fires. The
    /// At edge is meaningless for a cross and is an argument fault.
    /// </summary>
    public static bool IsIndicatorCrossHit(
        AlertTrigger edge,
        decimal? previousPrice,
        decimal? previousIndicator,
        decimal? currentPrice,
        decimal? currentIndicator)
    {
        if (edge is not (AlertTrigger.Above or AlertTrigger.Below))
        {
            throw new ArgumentOutOfRangeException(nameof(edge), edge, "A cross reads only the Above or Below edge.");
        }

        if (previousPrice is not { } fromPrice || currentPrice is not { } toPrice ||
            previousIndicator is not { } fromIndicator || currentIndicator is not { } toIndicator)
        {
            return false;
        }

        return edge switch
        {
            AlertTrigger.Above => fromPrice - fromIndicator < 0m && toPrice - toIndicator >= 0m,
            AlertTrigger.Below => fromPrice - fromIndicator > 0m && toPrice - toIndicator <= 0m,
            _ => false,
        };
    }
}
