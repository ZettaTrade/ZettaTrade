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

    /// <summary>Settings key of the auto-update enable state (a bool, T-258).</summary>
    public const string AutoUpdate = "AutoUpdate";

    /// <summary>
    /// The auto-update applied until a stored value is loaded: OFF —
    /// nothing downloads, nothing stages, nothing asks before the user
    /// switches the loop on. The default is the opt-in posture (the
    /// trading-safety canon: a session's first job is trading, and the
    /// consent dialog is the only door an update walks through).
    /// </summary>
    public const bool DefaultAutoUpdateEnabled = false;

    /// <summary>Settings key of the update channel (a stored name, T-258).</summary>
    public const string UpdateChannel = "UpdateChannel";

    /// <summary>
    /// The update channel applied until a stored value is loaded (and the
    /// repair target for a garbage stored value): the stable releases only.
    /// The channel class's own name spells it, fully qualified — the
    /// settings key above shadows the short name here.
    /// </summary>
    public const string DefaultUpdateChannel = ZettaTrade.App.Services.UpdateChannel.StableName;

    /// <summary>Settings key of the tray close behavior (a stored enum name).</summary>
    public const string TrayCloseBehavior = "TrayCloseBehavior";

    /// <summary>
    /// The tray close behavior applied until a stored value is loaded:
    /// the close gesture exits exactly as it always has — the tray option
    /// never changes anything before the user asks for it.
    /// </summary>
    public const TrayCloseMode DefaultTrayCloseBehavior = TrayCloseMode.Exit;
}
