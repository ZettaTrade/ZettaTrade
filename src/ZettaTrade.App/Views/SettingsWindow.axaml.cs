using System;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Microsoft.Extensions.DependencyInjection;
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
/// The settings port's (T-100) file dialogs ride the same discipline: the
/// code-behind picks the file (View behavior over the storage provider),
/// then hands the path to the ViewModel's port methods.
/// </summary>
public partial class SettingsWindow : ReactiveWindow<SettingsViewModel>
{
    /// <summary>The export file's suggested name: the app name (the picker appends its extension).</summary>
    private const string ExportSuggestedFileName = "zettatrade-settings";

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

    /// <summary>
    /// The first-run wizard's re-run entry (T-099): the board names the
    /// settings window as the only way back into the wizard. View behavior —
    /// the window closes itself and the wizard shows modally over the owner
    /// (this window was the owner's dialog, so the owner stays the modal
    /// parent). The route is task-based with errors surfaced through the
    /// log: never an async void, never a fire-and-forget.
    /// </summary>
    private void OnFirstRunWizardClick(object? sender, RoutedEventArgs e) =>
        Observable
            .FromAsync(RunFirstRunWizardRouteAsync)
            .Subscribe(
                _ => { },
                ex => Log.Error(ex, "Failed to open the first-run wizard from the settings"));

    private async Task RunFirstRunWizardRouteAsync()
    {
        var owner = Owner as Window;
        Close();

        // The wizard never writes the once-flag here: it exists already on
        // this entry (the wizard announced once on the first launch).
        var dialog = new FirstRunWizardWindow
        {
            DataContext = App.Services.GetRequiredService<FirstRunWizardViewModel>(),
        };
        await dialog.ShowDialog(owner!);
    }

    private async void OnExportClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel?.IsPortAvailable != true)
        {
            return;
        }

        try
        {
            var destination = await SettingsPortFilePicker.PickSaveAsync(this, ExportSuggestedFileName);
            if (destination is not null)
            {
                await ViewModel.ExportToFileAsync(destination);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to export the settings");
        }
    }

    private async void OnImportClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel?.IsPortAvailable != true)
        {
            return;
        }

        try
        {
            var source = await SettingsPortFilePicker.PickOpenAsync(this);
            if (source is null)
            {
                return;
            }

            var jsonText = await File.ReadAllTextAsync(source);
            await ViewModel.DraftImportAsync(jsonText);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to read the file the import picked");
        }
    }

    private async void OnResetClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel?.IsPortAvailable != true)
        {
            return;
        }

        try
        {
            await ViewModel.ResetToDefaultsAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to run the factory reset");
        }
    }
}
