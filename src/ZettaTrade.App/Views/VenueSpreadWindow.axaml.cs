namespace ZettaTrade.App.Views;

using Avalonia.Interactivity;
using ReactiveUI.Avalonia;
using ZettaTrade.App.ViewModels;

/// <summary>
/// Cross-venue spread and perp-spot basis window (T-253). Owns the
/// window plumbing: the panel ViewModel is the workspace singleton —
/// the symbol text, the knobs and the standing table live with the
/// application, so the window only hosts the page (the opener assigns
/// <c>DataContext</c>); the Close button closes the window directly
/// (the correlation window's own discipline).
/// </summary>
public partial class VenueSpreadWindow : ReactiveWindow<VenueSpreadViewModel>
{
    /// <summary>
    /// Create the venue-spread window. The view model must be assigned
    /// before activation (the opener sets <c>DataContext</c>).
    /// </summary>
    public VenueSpreadWindow()
    {
        InitializeComponent();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
