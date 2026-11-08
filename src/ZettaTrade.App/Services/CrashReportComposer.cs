namespace ZettaTrade.App.Services;

using System;
using System.Text;
using Livery.Localization;

/// <summary>
/// Composes the crash dialog's content (T-101). Every user-facing shape
/// rides the localization catalog; the LOG PATH is the sentence's payload
/// the acceptance pins: the dialog carries it raw (selectable text in the
/// window) AND inside the localized sentence, so the user can act on it
/// without the app running. The composer touches nothing but the
/// localization manager — no settings, no secrets, no trading state: it
/// cannot fail a trading write even in principle.
/// </summary>
public sealed class CrashReportComposer
{
    #region Resx Keys

    /// <summary>The dialog title.</summary>
    private const string TitleKey = "Log.Crash.Title";

    /// <summary>The exception summary frame: {0} = type name, {1} = message.</summary>
    private const string SummaryKey = "Log.Crash.Summary";

    /// <summary>The summary for a fatal event without an exception object (the runtime's degenerate corner).</summary>
    private const string SummaryEmptyKey = "Log.Crash.SummaryEmpty";

    /// <summary>The log-path sentence: {0} = the directory (raw, for selection).</summary>
    private const string LogPathKey = "Log.Crash.LogPath";

    #endregion Resx Keys

    #region Private Fields

    private readonly ILocalizationManager _localization;
    private readonly string _logPath;

    #endregion Private Fields

    #region Constructor

    /// <summary>
    /// Create the composer over the localization catalog and the log
    /// directory the dialog reports.
    /// </summary>
    /// <param name="localization">The localized-string catalog.</param>
    /// <param name="logPath">The log directory path (the composition root's sink folder).</param>
    public CrashReportComposer(
        ILocalizationManager localization,
        string logPath)
    {
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _logPath = logPath ?? throw new ArgumentNullException(nameof(logPath));
    }

    #endregion Constructor

    #region Public API

    /// <summary>Build the crash report for one exception.</summary>
    /// <param name="exception">The exception the handler caught (never null by the hooks' contract).</param>
    /// <returns>The composed, read-only dialog content.</returns>
    public CrashReport Compose(Exception exception)
    {
        var type = exception.GetType().Name;
        var message = CleanMessage(exception.Message);

        return new CrashReport(
            Title: _localization.GetString(TitleKey),
            Summary: _localization.GetString(SummaryKey, type, message),
            LogPathLine: _localization.GetString(LogPathKey, _logPath),
            LogPath: _logPath);
    }

    /// <summary>
    /// The unhandled exception with NO exception object (the .NET runtime
    /// hook's degenerate corner): a dedicated summary sentence, digest the
    /// title and the path only.
    /// </summary>
    /// <returns>The path-only crash report.</returns>
    public CrashReport ComposeWithoutException() => new(
        Title: _localization.GetString(TitleKey),
        Summary: _localization.GetString(SummaryEmptyKey),
        LogPathLine: _localization.GetString(LogPathKey, _logPath),
        LogPath: _logPath);

    #endregion Public API

    #region Helpers

    /// <summary>Compose keeps the message single-line (newline-free) and bounded; a truncation suffix marks the clipped case explicitly. No defaulting replaces a missing message — it just stays empty.</summary>
    private const int MaxSummaryMessageLength = 300;

    /// <summary>Sanitize one message: text-plain, single-line, bounded; a truncation suffix marks the case explicitly. Same-shape continuation text compose only when present — no defaulting replaces a missing message, it just stays empty.</summary>
    private static string CleanMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return string.Empty;
        }

        var bounded = message.Length > MaxSummaryMessageLength
            ? message[..MaxSummaryMessageLength] + TruncationSuffix
            : message;

        var single = new StringBuilder(bounded.Length);
        foreach (var ch in bounded)
        {
            single.Append(ch switch
            {
                '\r' or '\n' => ' ',
                _ => ch,
            });
        }

        return single.ToString();
    }

    /// <summary>The marker a bounded message carries (ASCII-only, additive to the frame).</summary>
    private const string TruncationSuffix = "…";

    #endregion Helpers
}
