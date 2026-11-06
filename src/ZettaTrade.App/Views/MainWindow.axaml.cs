using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using Livery.Extensions;
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
/// showing windows is View behavior, and the deferred focus of the assistant
/// input is a View concern too, so the ViewModel only raises
/// <see cref="MenuViewModel.OpenSettingsRequested"/>,
/// <see cref="MenuViewModel.NewOrderRequested"/>, … and
/// <see cref="MenuViewModel.FocusAiChatRequested"/> (fired when the toggle
/// shows the panel).
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
        ViewModel?.Menu.ShowBalancesRequested.RegisterHandler(OpenBalancesAsync);
        ViewModel?.Menu.FocusAiChatRequested.RegisterHandler(FocusChatAsync);

        // The book panel lives for the whole window lifetime too: its level
        // presses feed a click stream that channels the prefill into the
        // SAME dialog path the menu drives. SubscribeAsync keeps the whole
        // route task-based (zero async void) and stream failures land in
        // the log via the error callback.
        _bookClicks = BookWidget.LevelClicked.SubscribeAsync(
            level => OpenNewOrderDialogAsync(new OrderPrefill(level.Symbol, level.Side, level.Price)),
            onError: ex => Log.Error(ex, "Book level click stream failed"));
        Closed += (_, _) => _bookClicks.Dispose();

        // The same route serves the chart's older-history requests: a pan
        // that reaches the loaded series' left edge asks the ViewModel for
        // the previous bars window (SubscribeAsync keeps the route task
        // based; stream failures land in the log via the error callback).
        _olderHistoryRequests = CandlesChart.OlderHistoryRequested
            .ObserveOnUiThread()
            .SubscribeAsync(
                request => ViewModel!.Chart.RequestOlderBarsAsync(),
                onError: ex => Log.Error(ex, "Chart older history request stream failed"));
        Closed += (_, _) => _olderHistoryRequests.Dispose();
    }

    /// <summary>Subscription of the book level click stream; disposed with the window.</summary>
    private IDisposable? _bookClicks;

    /// <summary>Subscription of the chart older-history request stream; disposed with the window.</summary>
    private IDisposable? _olderHistoryRequests;

    /// <summary>
    /// The frames (queued below the render priority) the chat focus is
    /// allowed to stay refused for before giving up: the toggle only just
    /// made the panel visible, and the startup can take more than one frame
    /// to arrange it.
    /// </summary>
    private const int MaxChatFocusFrames = 8;

    /// <summary>
    /// Land the keyboard focus on the chat input AFTER layout, then complete
    /// the request. An immediate Focus would hit the not-yet-arranged text
    /// box and be refused, so every attempt is queued below the render
    /// priority and awaited; the interaction resolves only when the focus
    /// landed (or the budget ran out).
    /// </summary>
    private async Task FocusChatAsync(IInteractionContext<RxVoid, RxVoid> context)
    {
        var focused = false;
        for (var attempt = MaxChatFocusFrames; attempt > 0 && !focused; attempt--)
        {
            focused = await Dispatcher.UIThread.InvokeAsync(
                () => ChatInput.Focus(),
                DispatcherPriority.Background);
        }

        context.SetOutput(RxVoid.Default);
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

    private async Task OpenBalancesAsync(IInteractionContext<RxVoid, RxVoid> context)
    {
        try
        {
            // The panel view model is transient: every open re-queries the
            // adapters, so a stale snapshot never rides into a new open.
            var panel = new BalancesPanelWindow
            {
                DataContext = App.Services.GetRequiredService<ViewModels.BalancesPanelViewModel>(),
            };
            await panel.ShowDialog<object>(this);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to open the balances panel");
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
            // to the applied state (the pairs tab re-reads the exchange listing)
            // so unsaved changes never leak into a new dialog.
            await ViewModel!.Settings.ResetSelectionsAsync();

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
