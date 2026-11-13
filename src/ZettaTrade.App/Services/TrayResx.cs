namespace ZettaTrade.App.Services;

/// <summary>
/// The tray surface's resx keys and static assets (T-214): one place so the
/// service's strings stay greppable next to their consumers. The DND and
/// Exit captions deliberately reuse the shipped keys (one truth per
/// surface — the notification center's own toggle caption and the File
/// menu's own exit caption); only the two tray-specific keys are new.
/// </summary>
internal static class TrayResx
{
    /// <summary>The show/hide item's caption key.</summary>
    public const string ShowHide = "Tray.ShowHide";

    /// <summary>The close-to-tray checkable's caption key.</summary>
    public const string CloseToTray = "Tray.CloseToTray";

    /// <summary>The DND checkable's caption: the notification center's own key.</summary>
    public const string DoNotDisturb = "Notif.DndToggle";

    /// <summary>The exit item's caption: the File menu's own key.</summary>
    public const string Exit = "Menu.File.Exit";
}
