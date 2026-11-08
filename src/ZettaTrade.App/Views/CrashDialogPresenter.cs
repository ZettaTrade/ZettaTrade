namespace ZettaTrade.App.Views;

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using ZettaTrade.App.Services;

/// <summary>
/// The crash dialog presenter (T-101): the production
/// <see cref="ICrashDialogPresenter"/>. The presenter runs on the crashing
/// thread — when that thread is not the UI thread, the dialog composes
/// inside a synchronous UI-thread dispatch (the crash waits for the
/// dialog; the platform kill proceeds after it). When the window system
/// has no main window at all (a startup crash), the dialog still shows
/// un-owned; a failed presentation logs and lets the process die quietly.
/// </summary>
public sealed class CrashDialogPresenter : ICrashDialogPresenter
{
    private readonly ILogger<CrashDialogPresenter> _logger;

    /// <summary>
    /// Create the presenter.
    /// </summary>
    /// <param name="logger">The presenter's own logger.</param>
    public CrashDialogPresenter(ILogger<CrashDialogPresenter> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public void ShowCrashDialog(CrashReport report)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            ShowOnUi(report);
            return;
        }

        try
        {
            // The crash thread marshals synchronously: the dialog shows,
            // the user reads it, the crash thread wakes and the platform's
            // exit proceeds. A dispatcher that cannot answer throws here
            // and only the log entry remains (the honest last-inform).
            Dispatcher.UIThread.Invoke(() => ShowOnUi(report));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The crash dialog could not marshal onto the UI thread");
        }
    }

    private void ShowOnUi(CrashReport report)
    {
        try
        {
            var lifetime = Application.Current?.ApplicationLifetime
                as IClassicDesktopStyleApplicationLifetime;

            if (lifetime?.MainWindow is { } owner)
            {
                // An owned plain show: the dialog stays on the owner, keeps
                // the process's fatal path free (no modal await can block
                // the mandated exit), and the platform kills both at once.
                new CrashDialogWindow
                {
                    DataContext = report,
                }.Show(owner);
            }
            else
            {
                // No owner exists yet (a startup crash): a plain show still
                // informs; the launcher's teardown closes it with the process.
                new CrashDialogWindow
                {
                    DataContext = report,
                }.Show();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The crash dialog window failed to show");
        }
    }
}
