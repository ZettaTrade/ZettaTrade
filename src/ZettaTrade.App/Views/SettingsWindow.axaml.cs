using Avalonia.Interactivity;
using ReactiveUI.Avalonia;
using ZettaTrade.App.ViewModels;

namespace ZettaTrade.App.Views;

/// <summary>
/// Modal settings window. Pure View: hosts <see cref="SettingsViewModel"/>,
/// whose selection properties apply theme/language changes live.
/// Closing stays in code-behind on purpose — closing a window is View
/// behavior, and routing it through the ViewModel would leak Window types into it.
/// </summary>
public partial class SettingsWindow : ReactiveWindow<SettingsViewModel>
{
    /// <summary>
    /// Create a new SettingsWindow instance.
    /// </summary>
    public SettingsWindow()
    {
        InitializeComponent();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
