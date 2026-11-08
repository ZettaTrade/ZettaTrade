namespace ZettaTrade.App;

/// <summary>
/// Settings keys of the notification center section (T-097): the
/// Do-Not-Disturb flag and the four per-source mute toggles. Single source
/// of truth for notification settings access; every flip writes through
/// the reactive settings service, and readers compose the persisted values
/// with the change stream (the T-053 reactive settings canon), so a flip
/// written anywhere (a drawer toggle, a settings-file external edit) is
/// live on every subscriber without a restart.
/// </summary>
public static class NotificationSettingsKeys
{
    /// <summary>Settings section holding notification preferences.</summary>
    public const string Section = "Notifications";

    /// <summary>Settings key of the Do-Not-Disturb flag (a bool).</summary>
    public const string DoNotDisturb = "DoNotDisturb";

    /// <summary>Settings key of the fills-source mute (a bool).</summary>
    public const string MuteFill = "MuteFill";

    /// <summary>Settings key of the cancellation-source mute (a bool).</summary>
    public const string MuteCancel = "MuteCancel";

    /// <summary>Settings key of the rejection-source mute (a bool).</summary>
    public const string MuteReject = "MuteReject";

    /// <summary>Settings key of the adapter-state-source mute (a bool).</summary>
    public const string MuteAdapterState = "MuteAdapterState";

    /// <summary>
    /// The Do-Not-Disturb flag applied until a stored value is loaded: off —
    /// the default drawer state a fresh install (or a reset file) sees, and
    /// the pre-center behavior (every outcome had its log line, nothing had
    /// a toggle).
    /// </summary>
    public const bool DefaultDoNotDisturb = false;

    /// <summary>The mute state applied for a source until a stored value is loaded: unmuted.</summary>
    public const bool DefaultMuted = false;
}
