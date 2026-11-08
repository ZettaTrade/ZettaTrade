namespace ZettaTrade.App.Services;

/// <summary>
/// The kinds of facts the alert engine publishes (T-096). The manager
/// re-renders its rows on every event; the fire event additionally carries
/// the fired fact for anything that wants the precise moment.
/// </summary>
public enum AlertEventKind
{
    /// <summary>The roster changed (create, edit re-arm, snooze, delete) — re-read the definitions.</summary>
    RosterChanged,

    /// <summary>An alert fired: the payload's definition carries its own post-fire facts (Fired, the firing stamp).</summary>
    Fired,
}

/// <summary>
/// One fact the alert engine publishes (T-096): either the roster's shape
/// changed (re-read <see cref="IAlertEngine.Definitions"/>) or an alert
/// reached its condition (the carried definition is its post-fire self and
/// the observation the tick that fired it).
/// </summary>
/// <param name="Kind">Which fact landed.</param>
/// <param name="Definition">The post-fire definition for a Fired event; null for a roster change.</param>
/// <param name="Observation">The qualifying observation for a Fired event; null for a roster change.</param>
public sealed record AlertEvent(
    AlertEventKind Kind,
    AlertDefinition? Definition,
    AlertObservation? Observation);
