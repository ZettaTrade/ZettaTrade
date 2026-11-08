namespace ZettaTrade.App.Services;

using System;
using System.Threading;
using Microsoft.Extensions.Logging;

/// <summary>
/// The global crash guard (T-101): the process hook pair App.axaml.cs
/// attaches right after the service provider builds. Ha hook fires:
/// <list type="bullet">
/// <item><see cref="AppDomain.UnhandledException"/> — the runtime's
/// terminating crash leg (background threads and the raw fatal paths);</item>
/// <item>the Avalonia dispatcher's <c>Dispatcher.UIThread.UnhandledException</c>
/// — the UI-thread fatal leg (the T-056 silent-death family's remaining gap).</item>
/// </list>
/// <para>
/// The contract is a LAST-INFORM, never a suppressor: each hook logs the
/// exception into the file-only sink FIRST (the log path is what the
/// dialog is about, so the entry must exist), composes the report and
/// presents the dialog once (the first report wins — the two legs often
/// fire in tandem; a double dialog helps nobody), and RETURNS. Nothing
/// exits the process here, nothing marks an exception handled, nothing
/// writes settings or trading state: the platform's default kill proceeds
/// after the dialog, exactly as an unhandled exception must.
/// </para>
/// </summary>
/// <remarks>
/// The guard holds NO settings or secret-store dependency — the compose
/// and present path cannot fail a trading write even in principle, and
/// the "no state writes" acceptance runs on that structural shape.
/// </remarks>
public sealed class CrashGuard
{
    #region Private Fields

    private readonly ILogger<CrashGuard> _logger;
    private readonly CrashReportComposer _composer;
    private readonly ICrashDialogPresenter _presenter;

    /// <summary>One dialog per process, whatever fires: 0 = none shown, 1 = shown.</summary>
    private int _dialogShown;

    #endregion Private Fields

    #region Constructor

    /// <summary>
    /// Create the guard with its composer and presenter.
    /// </summary>
    /// <param name="logger">The guard's own logger (file-only — Serilog writes files, never stdout).</param>
    /// <param name="composer">The crash report composer (the log path lives there).</param>
    /// <param name="presenter">The dialog presenter seam (the Avalonia window in production).</param>
    public CrashGuard(
        ILogger<CrashGuard> logger,
        CrashReportComposer composer,
        ICrashDialogPresenter presenter)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _composer = composer ?? throw new ArgumentNullException(nameof(composer));
        _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
    }

    #endregion Constructor

    #region Public API

    /// <summary>
    /// Attach both process hooks. When one leg already fires, the other's
    /// report deduplicates into the log-only path (the one-dialog rule).
    /// </summary>
    public void Attach()
    {
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        Avalonia.Threading.Dispatcher.UIThread.UnhandledException += OnDispatcherUnhandledException;
    }

    /// <summary>
    /// The runtime hook: report, then RETURN — with
    /// <see cref="UnhandledExceptionEventArgs.IsTerminating"/> the process
    /// exits right after (the unhandled exception's exit requirement
    /// stands; the dialog is the last-inform that precedes it).
    /// </summary>
    /// <param name="sender">The runtime's sender (ignored).</param>
    /// <param name="e">The terminating event args.</param>
    private void OnUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        Report(e.ExceptionObject as Exception);
    }

    /// <summary>
    /// The dispatcher hook: report, leave <c>e.Handled</c> explicitly
    /// FALSE — the dispatcher's default kill proceeds after the handler
    /// returns (the guard never suppresses the crash it reports).
    /// </summary>
    /// <param name="sender">The dispatcher (ignored).</param>
    /// <param name="e">The dispatcher exception event args.</param>
    private void OnDispatcherUnhandledException(
        object? sender,
        Avalonia.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        Report(e.Exception);
        e.Handled = false;
    }

    /// <summary>
    /// The report path (both hooks): log FIRST (the crash dialog's whole
    /// point is where the log lives), compose, present once. A composer or
    /// presenter fault never blocks the report: the process dies with the
    /// logged entry even when the dialog cannot show.
    /// </summary>
    /// <param name="exception">The caught exception, or null when the runtime offered none.</param>
    internal void Report(Exception? exception)
    {
        try
        {
            // File-only logging: the crash entry lands in the same sink the
            // dialog points at (nothing writes to stdout — the purity rule).
            if (exception is not null)
            {
                _logger.LogCritical(
                    exception,
                    "Unhandled exception caught by the crash guard; the process exits after the crash dialog");
            }
            else
            {
                _logger.LogCritical(
                    "Unhandled fatal event caught by the crash guard; the process exits after the crash dialog");
            }

            var report = exception is not null
                ? _composer.Compose(exception)
                : _composer.ComposeWithoutException();

            // One dialog per process: the two hooks fire in tandem on a UI
            // crash; the second report stays log-only.
            if (Interlocked.CompareExchange(ref _dialogShown, 1, 0) != 0)
            {
                _logger.LogInformation(
                    "The crash dialog already presented; reporting the second hook's event into the log only");
                return;
            }

            // Best effort: a failed presentation logs inside the presenter
            // and the process dies quietly — the platform's exit proceeds.
            _presenter.ShowCrashDialog(report);
        }
        catch (Exception failure)
        {
            // The last-inform must never become the crash that hangs the
            // crash: a fault here short-circuits to the log and the
            // process's own exit continues.
            _logger.LogError(
                failure,
                "The crash guard failed to present the crash dialog; the process exits");
        }
    }

    #endregion Public API

    #region Test Seam

    /// <summary>
    /// The test seam the facts drive through the same composition + one-dialog
    /// path the hooks reach; the hooks themselves stay private (a test never
    /// mutates process-global handler state).
    /// </summary>
    /// <param name="exception">The caught exception (or null for the runtime's empty leg).</param>
    internal void ReportForTest(Exception? exception) => Report(exception);

    #endregion Test Seam
}
