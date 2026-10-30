using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using ReactiveUI;
using ReactiveUI.Avalonia;
using ReactiveUI.Primitives;
using Serilog;
using ZettaTrade.App.ViewModels;

namespace ZettaTrade.App.Views;

/// <summary>
/// Main application window. Owns the open-settings interaction handler:
/// creating and showing windows is View behavior, so the ViewModel only
/// raises <see cref="MenuViewModel.OpenSettingsRequested"/>.
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

    /// <inheritdoc/>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // The main window opens once per application lifetime, so the handler
        // registration lives for the rest of it — no subscription disposal here.
        ViewModel?.Menu.OpenSettingsRequested.RegisterHandler(OpenSettingsAsync);
    }

    private async Task OpenSettingsAsync(IInteractionContext<RxVoid, RxVoid> context)
    {
        try
        {
            var dialog = new SettingsWindow
            {
                DataContext = ViewModel!.Settings,
            };
            await dialog.ShowDialog(this);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to open the settings window");
        }
        finally
        {
            context.SetOutput(RxVoid.Default);
        }
    }
}
