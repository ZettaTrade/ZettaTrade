namespace ZettaTrade.App.ViewModels;

using ZettaTrade.App.Services;

/// <summary>
/// One bound row of the log viewer (T-101): the parsed redacted line plus
/// the display strings composed ONCE at row build. Localization rides the
/// composed-once discipline (the window rebuilds per open, so a language
/// switch re-lands labels — the same posture the notification center
/// rows take).
/// </summary>
/// <param name="Line">The parsed redacted line the row renders (the filter's raw fields too).</param>
/// <param name="Moment">The header moment rendered per log precision (empty on continuations).</param>
/// <param name="LevelName">The localized level name (empty for heading-less lines).</param>
public sealed record LogViewerRow(
    LogLine Line,
    string Moment,
    string LevelName);
