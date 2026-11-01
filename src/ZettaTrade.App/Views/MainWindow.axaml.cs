using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
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
        ViewModel?.Menu.NewOrderRequested.RegisterHandler(OpenNewOrderAsync);
        ViewModel?.Menu.ShowPositionsRequested.RegisterHandler(OpenPositionsAsync);
        ViewModel?.Menu.ShowOrderHistoryRequested.RegisterHandler(OpenOrderHistoryAsync);
    }

    private async Task OpenNewOrderAsync(IInteractionContext<RxVoid, RxVoid> context)
    {
        try
        {
            // The dialog view model is transient: every open starts with empty
            // inputs, so a stale symbol or quantity never leaks into a new order.
            var dialog = new NewOrderDialogWindow
            {
                DataContext = App.Services.GetRequiredService<ViewModels.NewOrderDialogViewModel>(),
            };
            await dialog.ShowDialog<object>(this);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to open the new order dialog");
        }
        finally
        {
            context.SetOutput(RxVoid.Default);
        }
    }

    private async Task OpenPositionsAsync(IInteractionContext<RxVoid, RxVoid> context)
    {
        try
        {
            // The panel view model is transient: every open starts by
            // reloading the adapter state and the session order journal.
            var panel = new PositionsPanelWindow
            {
                DataContext = App.Services.GetRequiredService<ViewModels.PositionsPanelViewModel>(),
            };
            await panel.ShowDialog<object>(this);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to open the positions panel");
        }
        finally
        {
            context.SetOutput(RxVoid.Default);
        }
    }

    private async Task OpenOrderHistoryAsync(IInteractionContext<RxVoid, RxVoid> context)
    {
        try
        {
            // The view model is transient: every open re-reads the persisted
            // order journal, so a restarted app never shows stale rows.
            var window = new OrderHistoryWindow
            {
                DataContext = App.Services.GetRequiredService<ViewModels.OrderHistoryViewModel>(),
            };
            await window.ShowDialog<object>(this);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to open the order history window");
        }
        finally
        {
            context.SetOutput(RxVoid.Default);
        }
    }

    private async Task OpenSettingsAsync(IInteractionContext<RxVoid, RxVoid> context)
    {
        try
        {
            // The ViewModel is an app-lifetime singleton: resync its selections
            // to the applied state so unsaved changes never leak into a new dialog.
            ViewModel!.Settings.ResetSelections();

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
