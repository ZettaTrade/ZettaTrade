namespace ZettaTrade.App.Views;

using Livery.Extensions;
using ReactiveUI;
using ReactiveUI.Avalonia;
using ReactiveUI.Primitives;
using Serilog;
using ZettaTrade.App.ViewModels;

/// <summary>
/// The first-run disclaimer dialog (educational software, not investment
/// advice + the explicit alpha posture). Window plumbing only: the
/// acknowledge button persists through the ViewModel's settings command and
/// a completed execution closes the window — a dismissal without an
/// acknowledge (title bar X, Esc) leaves the flag unset, so the dialog
/// re-shows on the next launch until explicitly acknowledged.
/// </summary>
public partial class DisclaimerDialogWindow : ReactiveWindow<DisclaimerDialogViewModel>
{
    /// <summary>
    /// Create a new DisclaimerDialogWindow instance. The view model must be
    /// assigned before activation (the opener sets <c>DataContext</c>).
    /// </summary>
    public DisclaimerDialogWindow()
    {
        InitializeComponent();
        this.WhenActivated(disposables =>
        {
            // An executed acknowledge closes the window (View behavior —
            // closing never lives in the ViewModel). The ViewModel's own
            // catch keeps the command finishing normally even on a failed
            // write, so the close happens after the write attempt is over
            // and a stream failure stays in the file log.
            ViewModel?.AcknowledgeCommand
                .Subscribe(
                    _ => Close(),
                    ex => Log.Error(ex, "Disclaimer dialog: acknowledge stream stopped unexpectedly"))
                .DisposeWith(disposables);
        });
    }
}
