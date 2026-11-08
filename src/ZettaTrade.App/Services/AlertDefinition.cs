namespace ZettaTrade.App.Services;

/// <summary>
/// One persisted price alert (T-096), an immutable record: the manager table
/// renders it and the engine evaluates copies of it. The facts for the two
/// <see cref="PriceAlertKind"/> members live on the same shape — a Price
/// alert reads <see cref="TargetPrice"/> with <see cref="Trigger"/>, a
/// PercentMove alert reads <see cref="ThresholdPercent"/> with
/// <see cref="WindowMinutes"/>; the other fields stay at their neutral
/// defaults and are never read for that kind (the engine reads only what
/// its kind names).
/// </summary>
/// <param name="Id">The stable identity the persistence and the manager commands key on.</param>
/// <param name="SymbolName">The normalized symbol ("BTC/USDT") the alert evaluates; resolved against the active exchange's own listing when drafted.</param>
/// <param name="Kind">Which evaluation the alert performs.</param>
/// <param name="Trigger">The Price kind's exact edge (<see cref="AlertTrigger"/>); the PercentMove kind leaves it at <see cref="AlertTrigger.At"/>'s neutral slot and reads the move's own direction at fire time.</param>
/// <param name="TargetPrice">The Price kind's level; unused (zero) for the PercentMove kind.</param>
/// <param name="ThresholdPercent">The PercentMove kind's non-negative percent (the move's magnitude that satisfies it); unused (zero) for the Price kind.</param>
/// <param name="WindowMinutes">The PercentMove kind's trailing window in whole minutes; unused (zero) for the Price kind.</param>
/// <param name="Note">The user's own free-text note; empty when left off.</param>
/// <param name="Status">The lifecycle state the engine evaluates (and the manager renders).</param>
/// <param name="CreatedUtc">The UTC moment the alert was created.</param>
/// <param name="SnoozedUntilUtc">The snooze expiration window for the <see cref="AlertStatus.Snoozed"/> state; null otherwise.</param>
/// <param name="LastFiredUtc">The UTC moment the alert last fired; null until it does.</param>
public sealed record AlertDefinition(
    Guid Id,
    string SymbolName,
    PriceAlertKind Kind,
    AlertTrigger Trigger,
    decimal TargetPrice,
    decimal ThresholdPercent,
    int WindowMinutes,
    string Note,
    AlertStatus Status,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? SnoozedUntilUtc,
    DateTimeOffset? LastFiredUtc)
{
    /// <summary>Whether the engine still evaluates the alert on arriving ticks — every non-terminal state does; a Fired one visits the next restart read-only.</summary>
    public bool IsEvaluatable => Status != AlertStatus.Fired;
}
