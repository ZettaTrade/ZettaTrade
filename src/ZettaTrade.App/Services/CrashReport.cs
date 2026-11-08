namespace ZettaTrade.App.Services;

/// <summary>
/// The crash dialog's composed content (T-101): what happened and where
/// the log lives. A plain record on purpose — the dialog is read-only and
/// binds the record directly; no view model state exists around a crash
/// report (nothing must mutate or write during the teardown).
/// </summary>
/// <param name="Title">The dialog title (localized).</param>
/// <param name="Summary">The one-line exception summary: type + message (localized frame).</param>
/// <param name="LogPathLine">The localized sentence naming the log path.</param>
/// <param name="LogPath">The raw log directory path (grepable, selectable in the dialog).</param>
public sealed record CrashReport(
    string Title,
    string Summary,
    string LogPathLine,
    string LogPath);
