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

    /// <summary>Settings key of the tray close behavior (a stored enum name).</summary>
    public const string TrayCloseBehavior = "TrayCloseBehavior";

    /// <summary>
    /// The tray close behavior applied until a stored value is loaded:
    /// the close gesture exits exactly as it always has — the tray option
    /// never changes anything before the user asks for it.
    /// </summary>
    public const TrayCloseMode DefaultTrayCloseBehavior = TrayCloseMode.Exit;
}
