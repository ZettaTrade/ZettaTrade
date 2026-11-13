namespace ZettaTrade.App;

/// <summary>
/// What the main window's close gesture does (T-214). The shipped default
/// is today's semantics: a closed main window IS the application exit.
/// The tray mode keeps the session alive behind the tray icon instead —
/// the exchanges, the risk gates and the dispatch pipe never stop.
/// </summary>
public enum TrayCloseMode
{
    /// <summary>The close gesture exits the application (the shipped default).</summary>
    Exit,

    /// <summary>The close gesture hides the window behind the tray icon; the session keeps running.</summary>
    MinimizeToTray,
}
