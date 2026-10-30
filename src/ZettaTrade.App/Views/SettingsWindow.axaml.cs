using System;
using Avalonia.Interactivity;
using ReactiveUI.Avalonia;
using Serilog;
using ZettaTrade.App.ViewModels;

namespace ZettaTrade.App.Views;

/// <summary>
/// Modal settings window. The ViewModel applies changes only on the explicit
/// save (<see cref="SettingsViewModel.SaveAsync"/>), so "Save" closes the window
/// and "Close" discards unsaved selection changes (the ViewModel re-syncs its
/// selections on the next open). Closing/click handling stays in code-behind
/// on purpose — window lifecycle is View behavior, not ViewModel logic.
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

    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (ViewModel is not null)
            {
                await ViewModel.SaveAsync();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save settings");
        }

        // Saved (or failed-and-logged): the user's intent was to leave the dialog.
        Close();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
