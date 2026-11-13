namespace ZettaTrade.App.Services;

/// <summary>
/// What a fire does to the alert afterwards (T-204): the re-arm options
/// beyond the terminal one-shot. The mode lives on the draft and the
/// definition, persists through the roster codec and steers the engine's
/// fire transition — the fire semantics per alert, never a global switch.
/// </summary>
public enum AlertReArmMode
{
    /// <summary>Today's terminal fire (the default): the reached definition becomes Fired and visits the next restart read-only.</summary>
    OneShot,

    /// <summary>The fire re-arms the alert to Armed and it refires on the next FRESH crossing — the condition must leave its edge and cross again (the per-definition gate holds the last evaluation's hit state in memory, session-scoped).</summary>
    PerCross,

    /// <summary>The fire re-arms the alert to Snoozed with <c>SnoozedUntilUtc</c> at the fire moment plus <see cref="AlertDefinition.CooldownMinutes"/>; the snooze-expiry re-arm carries it back and the first hit after the window fires again.</summary>
    Cooldown,
}

/// <summary>
/// The per-alert sound choice (T-204): the player keeps its single beep
/// tone, so the choice is whether that beep rides the fire at all. The
/// engine's degrade lane is unchanged — a failed beep stays a logged
/// warning, never a broken evaluation.
/// </summary>
public enum AlertSoundChoice
{
    /// <summary>No sound rides the fire.</summary>
    Silent,

    /// <summary>The one beep rides the fire (the default).</summary>
    Beep,
}
