namespace ZettaTrade.App.Views;

using Avalonia.Interactivity;
using ReactiveUI.Avalonia;
using ZettaTrade.App.ViewModels;

/// <summary>
/// Screener window (T-195). Owns the window plumbing: the panel ViewModel
/// is the workspace singleton — its feed subscription, presets and sorts
/// live with the application, so the window only hosts the table (the
/// opener assigns <c>DataContext</c>); the Close button closes the window
/// directly.
/// </summary>
public partial class ScreenerWindow : ReactiveWindow<ScreenerViewModel>
{
    /// <summary>
    /// Create the screener window. The view model must be assigned before
    /// activation (the opener sets <c>DataContext</c>).
    /// </summary>
    public ScreenerWindow()
    {
        InitializeComponent();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
