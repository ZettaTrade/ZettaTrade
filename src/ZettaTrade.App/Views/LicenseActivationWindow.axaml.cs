namespace ZettaTrade.App.Views;

using Avalonia.Interactivity;
using Livery.Extensions;
using ReactiveUI;
using ReactiveUI.Avalonia;
using ReactiveUI.Primitives;
using Serilog;
using ZettaTrade.App.ViewModels;

/// <summary>
/// The license activation dialog (T-132). Window plumbing only: an
/// activation that stored the key (a valid verdict) closes the window, a
/// refusal leaves it open with the verdict shown, and the close button (or
/// the title bar X) dismisses without any write.
/// </summary>
public partial class LicenseActivationWindow : ReactiveWindow<LicenseActivationViewModel>
{
    /// <summary>
    /// Create a new LicenseActivationWindow instance. The view model must be
    /// assigned before activation (the opener sets <c>DataContext</c>).
    /// </summary>
    public LicenseActivationWindow()
    {
        InitializeComponent();
        this.WhenActivated(disposables =>
        {
            // The verdict rendering the window owns: the language-switch
            // re-render chain and the plugin-id EULA reset join this
            // activation scope and tear down with it (the Attach canon the
            // transient view model and its only window share).
            ViewModel?.AttachVerdictRendering()
                .DisposeWith(disposables);

            // A stored activation closes the window (View behavior — closing
            // never lives in the ViewModel). The ViewModel's own catch keeps
            // the command finishing normally on an unexpected failure, so a
            // stream failure stays in the file log.
            ViewModel?.ActivateCommand
                .Subscribe(
                    _ => { if (ViewModel?.Stored is true) Close(); },
                    ex => Log.Error(ex, "License activation stream stopped unexpectedly"))
                .DisposeWith(disposables);
        });
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close();
}
