namespace ZettaTrade.App.Views;

using Avalonia.Controls;
using Avalonia.Interactivity;
using Livery.Extensions;
using ReactiveUI;
using ReactiveUI.Avalonia;
using ReactiveUI.Primitives;
using Serilog;
using ZettaTrade.App.ViewModels;

/// <summary>
/// The first-run wizard window (T-099). Owns the window plumbing: the
/// activation attaches the transient view model's symbol-listing chain and
/// the finish lane, both dying with the activation scope (the view model and
/// its only window share their lifetime). The skip affordance and the system
/// close share one path — a plain window close: the wizard applies nothing
/// on exit, and the once-flag is the launcher's own seam, marked after ANY
/// exit.
/// </summary>
public partial class FirstRunWizardWindow : ReactiveWindow<FirstRunWizardViewModel>
{
    /// <summary>
    /// Create the first-run wizard window. The view model must be assigned
    /// before activation (the opener sets <c>DataContext</c>).
    /// </summary>
    public FirstRunWizardWindow()
    {
        InitializeComponent();

        this.WhenActivated(disposables =>
        {
            // The listing chain (seeded draft + every draft-adapter switch)
            // dies with the window; the user-interface marshals land inside
            // the activation scope only.
            ViewModel?.AttachSymbolListing()
                .DisposeWith(disposables);

            // A successful finish closes the window (View behavior — closing
            // never lives in the ViewModel); a failed one keeps it open with
            // the drafts intact, so the retry or the skip path stays honest.
            ViewModel?.FinishCommand
                .Subscribe(
                    applied =>
                    {
                        if (applied)
                        {
                            Close();
                        }
                    },
                    ex => Log.Error(ex, "First-run wizard: finish stream stopped unexpectedly"))
                .DisposeWith(disposables);
        });
    }

    /// <summary>Skip leaves the wizard without applying anything: a plain close.</summary>
    private void OnSkipClick(object? sender, RoutedEventArgs e) => Close();
}
