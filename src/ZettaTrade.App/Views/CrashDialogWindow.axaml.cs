using Avalonia.Interactivity;
using ReactiveUI.Avalonia;
using ZettaTrade.App.Services;

namespace ZettaTrade.App.Views;

/// <summary>
/// The global crash guard's dialog (T-101): the last-inform sheet the
/// guard presents before the platform's default kill proceeds. Pure view:
/// the content is the composed <see cref="CrashReport"/> record (read-only
/// by design — nothing may mutate around a crash), the dismiss button
/// closes the window and lets the crash thread finish its exit duty.
/// </summary>
public partial class CrashDialogWindow : ReactiveWindow<CrashReport>
{
    /// <summary>
    /// Create the crash dialog. The report must be assigned before showing
    /// (the presenter sets <c>DataContext</c>).
    /// </summary>
    public CrashDialogWindow()
    {
        InitializeComponent();
    }

    private void OnDismissClick(object? sender, RoutedEventArgs e) => Close();
}
