using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using ReactiveUI;
using ReactiveUI.Avalonia;
using ReactiveUI.Primitives;
using Serilog;
using ZettaTrade.App.Services;
using ZettaTrade.App.ViewModels;
using ZettaTrade.UI.Extensions;

namespace ZettaTrade.App.Views;

/// <summary>
/// Main application window. Owns the menu interaction handlers: creating and
/// showing windows is View behavior, and focusing the assistant input is a
/// View concern too, so the ViewModel only raises
/// <see cref="MenuViewModel.OpenSettingsRequested"/>,
/// <see cref="MenuViewModel.NewOrderRequested"/>, … and
/// <see cref="MenuViewModel.OpenAiChatRequested"/>.
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
        ViewModel?.Menu.OpenAiChatRequested.RegisterHandler(FocusChatAsync);

        // The book panel lives for the whole window lifetime too: its level
        // presses feed a click stream that channels the prefill into the
        // SAME dialog path the menu drives. SubscribeAsync keeps the whole
        // route task-based (zero async void) and stream failures land in
        // the log via the error callback.
        _bookClicks = BookWidget.LevelClicked.SubscribeAsync(
            level => OpenNewOrderDialogAsync(new OrderPrefill(level.Symbol, level.Side, level.Price)),
            onError: ex => Log.Error(ex, "Book level click stream failed"));
        Closed += (_, _) => _bookClicks.Dispose();
    }

    /// <summary>Subscription of the book level click stream; disposed with the window.</summary>
    private IDisposable? _bookClicks;

    private Task FocusChatAsync(IInteractionContext<RxVoid, RxVoid> context)
    {
        ChatInput.Focus();
        context.SetOutput(RxVoid.Default);
        return Task.CompletedTask;
    }

    private async Task OpenNewOrderAsync(IInteractionContext<RxVoid, RxVoid> context)
    {
        try
        {
            await OpenNewOrderDialogAsync(prefill: null);
        }
        finally
        {
            context.SetOutput(RxVoid.Default);
        }
    }

    /// <summary>
    /// Open a NEW order dialog through the single transient path the menu
    /// and the book level clicks share. The transient VM starts EMPTY by
    /// design (a stale symbol or quantity never leaks into a new order);
    /// a prefill is not a leftover — it is the user's deliberate action,
    /// applied before the window shows.
    /// </summary>
    private async Task OpenNewOrderDialogAsync(OrderPrefill? prefill)
    {
        try
        {
            var viewModel = App.Services.GetRequiredService<ViewModels.NewOrderDialogViewModel>();
            if (prefill is { } carry)
            {
                viewModel.Prefill(carry);
            }

            var dialog = new NewOrderDialogWindow
            {
                DataContext = viewModel,
            };
            await dialog.ShowDialog<object>(this);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to open the new order dialog");
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
