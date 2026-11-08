namespace ZettaTrade.App;

/// <summary>
/// Settings keys of the alerts section (T-096): the persisted definitions
/// roster and the named constants the manager UX seeds. Single source of
/// truth for alert settings access; the roster's one key holds the whole
/// versioned payload the codec authors, so a create/snooze/delete edit and a
/// fire both rewrite exactly one entry atomically through the reactive
/// settings service.
/// </summary>
public static class AlertSettingsKeys
{
    /// <summary>Settings section holding the price alerts.</summary>
    public const string Section = "Alerts";

    /// <summary>Settings key of the persisted definitions roster (the codec's versioned JSON payload).</summary>
    public const string Definitions = "Definitions";

    /// <summary>How long a snooze lasts (minutes): the manager's one snooze affordance rides this preset.</summary>
    public const int SnoozeMinutes = 15;

    /// <summary>The percent-move window the editor seeds for a fresh percent alert (minutes).</summary>
    public const int DefaultWindowMinutes = 15;

    /// <summary>The percent threshold the editor seeds for a fresh percent alert.</summary>
    public const decimal DefaultPercentThreshold = 5m;
}
