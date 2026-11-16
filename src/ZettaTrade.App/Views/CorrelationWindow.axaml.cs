namespace ZettaTrade.App.Views;

using Avalonia.Interactivity;
using ReactiveUI.Avalonia;
using ZettaTrade.App.ViewModels;

/// <summary>
/// Correlation matrix window (T-252). Owns the window plumbing: the panel
/// ViewModel is the workspace singleton — its watchlist subscription,
/// knobs and the standing matrix live with the application, so the window
/// only hosts the page (the opener assigns <c>DataContext</c>); the Close
/// button closes the window directly (the screener window's own
/// discipline).
/// </summary>
public partial class CorrelationWindow : ReactiveWindow<CorrelationViewModel>
{
    /// <summary>
    /// Create the correlation window. The view model must be assigned
    /// before activation (the opener sets <c>DataContext</c>).
    /// </summary>
    public CorrelationWindow()
    {
        InitializeComponent();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
