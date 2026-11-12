using Avalonia.Interactivity;
using ReactiveUI.Avalonia;
using ZettaTrade.App.ViewModels;

namespace ZettaTrade.App.Views;

/// <summary>
/// Modal kill-switch report window (T-188). Pure View: binds the composed
/// summary from <see cref="KillSwitchReportViewModel"/>, closes on the
/// button. Button clicks stay in code-behind on purpose — closing a window
/// is View behavior, and routing it through the ViewModel would leak Window
/// types into it.
/// </summary>
public partial class KillSwitchReportWindow : ReactiveWindow<KillSwitchReportViewModel>
{
    /// <summary>
    /// Create a new KillSwitchReportWindow instance.
    /// </summary>
    public KillSwitchReportWindow()
    {
        InitializeComponent();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
