using ReactiveUI.Avalonia;
using ZettaTrade.App.ViewModels;

namespace ZettaTrade.App.Views;

/// <summary>
/// Main application window.
/// </summary>
public partial class MainWindow : ReactiveWindow<MainViewModel>
{
    /// <summary>
    /// Create a new MainWindow instance.
    /// </summary>
    public MainWindow()
    {
        InitializeComponent();
    }
}
