namespace ZettaTrade.App.Services;

/// <summary>
/// One persisted price alert (T-096, T-203), an immutable record: the
/// manager table renders it and the engine evaluates copies of it. The
/// facts for the kinds live on the same shape — a Price alert reads
/// <see cref="TargetPrice"/> with <see cref="Trigger"/>, a PercentMove
/// alert reads <see cref="ThresholdPercent"/> with
/// <see cref="WindowMinutes"/>, an IndicatorCross alert reads
/// <see cref="Indicator"/>; the other fields stay at their neutral defaults
/// and are never read for that kind (the engine reads only what its kind
/// names).
/// </summary>
/// <param name="Id">The stable identity the persistence and the manager commands key on.</param>
/// <param name="SymbolName">The normalized symbol ("BTC/USDT") the alert evaluates; resolved against the active exchange's own listing when drafted.</param>
/// <param name="Kind">Which evaluation the alert performs.</param>
/// <param name="Trigger">The Price kind's exact edge (<see cref="AlertTrigger"/>) and the IndicatorCross and FundingCross kinds' cross direction (Above/Below; At is refused at draft validation); the WhalePrint kind's fixed Above reading (T-245 — a trade alert is "bigger than", At and Below are refused); the PercentMove kind leaves it at <see cref="AlertTrigger.At"/>'s neutral slot and reads the move's own direction at fire time.</param>
/// <param name="TargetPrice">The Price kind's level and the FundingCross kind's signed threshold in PERCENT units (T-240; 0.05 = 0.05%); the WhalePrint kind's notional threshold in absolute quote-currency units (T-245; 100000 = 100 000 of the quote asset); unused (zero) for the PercentMove kind.</param>
/// <param name="ThresholdPercent">The PercentMove kind's non-negative percent (the move's magnitude that satisfies it); unused (zero) for the Price kind.</param>
/// <param name="WindowMinutes">The PercentMove kind's trailing window in whole minutes; unused (zero) for the Price kind.</param>
/// <param name="Note">The user's own free-text note; empty when left off.</param>
/// <param name="Status">The lifecycle state the engine evaluates (and the manager renders).</param>
/// <param name="CreatedUtc">The UTC moment the alert was created.</param>
/// <param name="SnoozedUntilUtc">The snooze expiration window for the <see cref="AlertStatus.Snoozed"/> state; null otherwise.</param>
/// <param name="LastFiredUtc">The UTC moment the alert last fired; null until it does.</param>
/// <param name="Indicator">The IndicatorCross kind's own facts (T-203, <see cref="AlertIndicatorSpec"/>); null for the other kinds.</param>
/// <param name="ReArm">The fire's aftermath (T-204, <see cref="AlertReArmMode"/>): the terminal one-shot, the per-crossing re-arm or the cooldown snooze.</param>
/// <param name="CooldownMinutes">The Cooldown re-arm's window in whole minutes; unused (zero) for the other modes.</param>
/// <param name="Sound">The fire's sound choice (T-204, <see cref="AlertSoundChoice"/>).</param>
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
    DateTimeOffset? LastFiredUtc,
    AlertIndicatorSpec? Indicator = null,
    AlertReArmMode ReArm = AlertReArmMode.OneShot,
    int CooldownMinutes = 0,
    AlertSoundChoice Sound = AlertSoundChoice.Beep)
{
    /// <summary>Whether the engine still evaluates the alert — every Armed or Snoozed one does; a Fired one visits the next restart read-only, and a Paused one (T-203) stalls until its plugin's set restores the indicator.</summary>
    public bool IsEvaluatable => Status is AlertStatus.Armed or AlertStatus.Snoozed;
}
