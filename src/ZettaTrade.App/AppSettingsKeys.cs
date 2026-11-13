namespace ZettaTrade.App;

/// <summary>
/// Settings keys of the application-level preferences section (T-213):
/// behaviors that belong to the app itself (the update check today)
/// keep their knobs apart from the daily-use UI section, so an opt-out
/// never touches unrelated keys.
/// </summary>
public static class AppSettingsKeys
{
    /// <summary>Settings section holding application-level preferences.</summary>
    public const string Section = "App";

    /// <summary>Settings key of the update check enable state (a bool).</summary>
    public const string UpdateCheck = "UpdateCheck";

    /// <summary>
    /// The update check applied until a stored value is loaded: on — a
    /// fresh install learns about releases (an opt-out switch, the
    /// posture the app always had before the check existed was simply
    /// "no news arrives").
    /// </summary>
    public const bool DefaultUpdateCheckEnabled = true;
}
