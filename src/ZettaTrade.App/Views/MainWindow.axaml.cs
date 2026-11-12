using System;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Livery.Extensions;
using Microsoft.Extensions.DependencyInjection;
using ReactiveUI;
using ReactiveUI.Avalonia;
using Serilog;
using ZettaTrade.App.Services;
using ZettaTrade.App.ViewModels;
using ZettaTrade.App.Views.Panels;
using ZettaTrade.UI.Extensions;
// RxVoid rides an alias (not a plain ReactiveUI.Primitives import): plain both
// imports would make every RxVoid Subscribe ambiguous against System's one.
using RxVoid = ReactiveUI.Primitives.RxVoid;

namespace ZettaTrade.App.Views;

/// <summary>
/// Main application window. Owns the menu interaction handlers: creating and
/// showing windows is View behavior, and the deferred focus of the assistant
/// input is a View concern too, so the ViewModel only raises
/// <see cref="MenuViewModel.OpenSettingsRequested"/>,
/// <see cref="MenuViewModel.NewOrderRequested"/>,
/// <see cref="MenuViewModel.ShowDisclaimerRequested"/>, … and
/// <see cref="MenuViewModel.FocusAiChatRequested"/> (fired when the toggle
/// shows the panel).
/// </summary>
public partial class MainWindow : ReactiveWindow<MainViewModel>
{
    /// <summary>The chart workspace panel (T-103): created in the ctor (T-175), one instance for both hosts.</summary>
    private readonly ChartPanelView ChartPanel;

    /// <summary>The order book panel (T-103): created in the ctor (T-175), one instance for both hosts.</summary>
    private readonly OrderBookPanelView BookPanel;

    /// <summary>The AI assistant panel (T-103): created in the ctor (T-175), one instance for both hosts.</summary>
    private readonly AssistantPanelView AssistantPanel;

    /// <summary>
    /// Create the main window around its view model (T-175): the DataContext
    /// lands BEFORE InitializeComponent, so the XAML's <c>$parent[Window]</c>
    /// lanes - the View menu's detach toggles, the HUD's tap gate - and the
    /// menu's own bindings evaluate against the live graph on the first
    /// pass. The null-DataContext phase that logged binding errors and
    /// self-healed on the later assignment (the debug-output noise) is
    /// gone: the app's construction rides this ctor, nothing else.
    /// </summary>
    public MainWindow(MainViewModel mainViewModel)
    {
        DataContext = mainViewModel ?? throw new ArgumentNullException(nameof(mainViewModel));

        InitializeComponent();

        // The workspace panels (T-103, T-175): created in code so each carries
        // its view model as a LocalValue from its OWN construction. The XAML-
        // created alternative let the loader wire the inherited MainViewModel
        // into the still-unattached panel (ahead of every visual and logical
        // tree link) and its subtree's first binding pass cast-errored on it -
        // the startup noise this sweep kills. The LocalValue outranks the
        // inherited value by priority and survives the detach move (the T-103
        // discipline holds by construction: no markup binding ever exists).
        ChartPanel = new ChartPanelView(mainViewModel.Chart);
        ChartPanel.Name = "ChartPanel";
        ChartPanelHost.Child = ChartPanel;
        BookPanel = new OrderBookPanelView(mainViewModel.OrderBook);
        BookPanel.Name = "BookPanel";
        BookPanelHost.Child = BookPanel;
        AssistantPanel = new AssistantPanelView(mainViewModel.Chat);
        AssistantPanel.Name = "AssistantPanel";
        AssistantPanelHost.Child = AssistantPanel;
    }

    /// <summary>
    /// The parameterless construction: the caller assigns the DataContext
    /// after InitializeComponent (the designer's route). The production
    /// path never rides it - the T-175 view-model-carrying ctor is the one
    /// the app uses, and the late assignment's null-DataContext binding
    /// noise (the <c>$parent[Window]</c> lanes evaluate while the
    /// DataContext is still unset) is the defect shape the startup pin
    /// reproduces as its counterfactual. The panels ride bare VMs so the
    /// detach machinery still arms.
    /// </summary>
    public MainWindow()
    {
        InitializeComponent();

        ChartPanel = new ChartPanelView(null);
        ChartPanel.Name = "ChartPanel";
        ChartPanelHost.Child = ChartPanel;
        BookPanel = new OrderBookPanelView(null);
        BookPanel.Name = "BookPanel";
        BookPanelHost.Child = BookPanel;
        AssistantPanel = new AssistantPanelView(null);
        AssistantPanel.Name = "AssistantPanel";
        AssistantPanelHost.Child = AssistantPanel;
    }

    /// <inheritdoc/>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // The performance HUD's chart tap (T-108): the panel owns the named
        // control, so the drain rides here — one assignment at the open. The
        // tap counts nothing until the HUD's visibility opens the chart's
        // FrameTapEnabled branch (the styled gate rides the panel element's
        // window-relative binding; a detached chart freezes the readout).
        ViewModel?.Hud.AttachChartFrames(() => ChartPanel.ChartControl.TakeFrameTapCount());

        // The detached-panel handover (T-103): the view lane registers the
        // panel slots with the coordinator (the panel controls stay exactly
        // where the XAML put them — the initial inline attach is already
        // their declared home), then the stored record re-opens whatever the
        // user left detached across sessions. The restore rides FromAsync +
        // Subscribe (no bare fire-and-forget): it must not block the open,
        // and its failures land in the file log.
        _detachedRestore = Observable.FromAsync(RestoreDetachedLayoutAsync).Subscribe(
            _ => { },
            ex => Log.Error(ex, "The detached-panel layout restore failed"));
        Closed += (_, _) => _detachedRestore.Dispose();

        // The main window opens once per application lifetime, so the handler
        // registration lives for the rest of it — no subscription disposal here.
        ViewModel?.Menu.OpenSettingsRequested.RegisterHandler(OpenSettingsAsync);
        ViewModel?.Menu.NewOrderRequested.RegisterHandler(OpenNewOrderAsync);
        ViewModel?.Menu.KillSwitchRequested.RegisterHandler(RunKillSwitchAsync);
        ViewModel?.Menu.ShowPositionsRequested.RegisterHandler(OpenPositionsAsync);
        ViewModel?.Menu.ShowOrderHistoryRequested.RegisterHandler(OpenOrderHistoryAsync);
        ViewModel?.Menu.ShowTradeStatsRequested.RegisterHandler(OpenTradeStatsAsync);
        ViewModel?.Menu.ShowBacktestRequested.RegisterHandler(OpenBacktestAsync);
        ViewModel?.Menu.ShowAlertsRequested.RegisterHandler(OpenAlertsAsync);
        ViewModel?.Menu.ShowNotificationsRequested.RegisterHandler(OpenNotificationHistoryAsync);
        ViewModel?.Menu.ShowBalancesRequested.RegisterHandler(OpenBalancesAsync);
        ViewModel?.Menu.FocusAiChatRequested.RegisterHandler(FocusChatAsync);
        ViewModel?.Menu.ShowDisclaimerRequested.RegisterHandler(OpenDisclaimerAsync);
        ViewModel?.Menu.ShowLicenseActivationRequested.RegisterHandler(OpenLicenseActivationAsync);
        ViewModel?.Menu.ShowLogViewerRequested.RegisterHandler(OpenLogViewerAsync);

        // The toast host lives for the whole window's stay like the other
        // singletons: its clicked rows pose their routes through this
        // window's single handler (the host dismisses; the route lands here).
        ViewModel?.ToastHost.OpenPanelRequested.RegisterHandler(OpenNotificationRouteAsync);

        // The book panel lives for the whole window lifetime too: its level
        // presses feed a click stream that channels the prefill into the
        // SAME dialog path the menu drives. SubscribeAsync keeps the whole
        // route task-based (zero async void) and stream failures land in
        // the log via the error callback.
        _bookClicks = BookPanel.BookWidget.LevelClicked.SubscribeAsync(
            level => OpenNewOrderDialogAsync(new OrderPrefill(level.Symbol, level.Side, level.Price)),
            onError: ex => Log.Error(ex, "Book level click stream failed"));
        Closed += (_, _) => _bookClicks.Dispose();

        // The signal rail's (T-112) row presses ride the SAME transient
        // dialog path the book level clicks share: a pressed row carries
        // the prefill FACTS (symbol, side, the signal candle's close), the
        // ticket opens prefilled — and nothing sends. The rail never
        // touches an order surface itself; the human decides on the ticket.
        _railPrefills = ViewModel!.Chart.SignalRail.RowPrefillRequested.SubscribeAsync(
            row => OpenNewOrderDialogAsync(new OrderPrefill(row.Source.Symbol, row.Side, row.Price)),
            onError: ex => Log.Error(ex, "Signal rail prefill stream failed"));
        Closed += (_, _) => _railPrefills.Dispose();

        // The same route serves the chart's older-history requests: a pan
        // that reaches the loaded series' left edge asks the ViewModel for
        // the previous bars window (SubscribeAsync keeps the route task
        // based; stream failures land in the log via the error callback).
        _olderHistoryRequests = ChartPanel.ChartControl.OlderHistoryRequested
            .ObserveOnUiThread()
            .SubscribeAsync(
                request => ViewModel!.Chart.RequestOlderBarsAsync(),
                onError: ex => Log.Error(ex, "Chart older history request stream failed"));
        Closed += (_, _) => _olderHistoryRequests.Dispose();

        // The heatmap tab's (T-121) pressed tiles ride the workspace symbol
        // route: the widget publishes just the tile's own text, the panel
        // ViewModel resolves it against the active exchange's parser and
        // writes the workspace context (or refuses it honestly). SubscribeAsync
        // keeps the whole route task-based (zero async void) and stream
        // failures land in the log via the error callback.
        _heatmapClicks = HeatmapWidget.TileClicked.SubscribeAsync(
            click => ViewModel!.Heatmap.RouteTileAsync(click),
            onError: ex => Log.Error(ex, "Heatmap tile click stream failed"));
        Closed += (_, _) => _heatmapClicks.Dispose();

        // The compare chips' detach presses (T-124) ride the removal route:
        // the chart control reports which chip's × zone was pressed, the
        // chart ViewModel owns the layer list (a removed label drops its
        // layer and the next pass republishes the strip cache). SubscribeAsync
        // keeps the route task-based (zero async void); stream failures land
        // in the log via the error callback.
        _compareRemovals = ChartPanel.ChartControl.CompareRemovals.SubscribeAsync(
            label =>
            {
                ViewModel!.Chart.RemoveCompare(label);
                return Task.CompletedTask;
            },
            onError: ex => Log.Error(ex, "Chart compare removal stream failed"));
        Closed += (_, _) => _compareRemovals.Dispose();

        // The drawings layer's interaction mirror (T-091): the control owns
        // the pointer routes (placement, drag, selection, delete) and the
        // ViewModel owns the layer's persistence — the three state routes
        // and the two lifecycle routes pair up per event (SubscribeAsync
        // keeps every route task-based; stream failures land in the log
        // via the error callback).
        _drawingCommitted = ChartPanel.ChartControl.DrawingCommitted.SubscribeAsync(
            request => ViewModel!.Chart.CommitDrawingAsync(request),
            onError: ex => Log.Error(ex, "Chart drawing commit stream failed"));
        Closed += (_, _) => _drawingCommitted.Dispose();
        _drawingEdited = ChartPanel.ChartControl.DrawingEdited.SubscribeAsync(
            request => ViewModel!.Chart.EditDrawingAsync(request),
            onError: ex => Log.Error(ex, "Chart drawing edit stream failed"));
        Closed += (_, _) => _drawingEdited.Dispose();
        _drawingSelected = ChartPanel.ChartControl.DrawingSelected.SubscribeAsync(
            id =>
            {
                ViewModel!.Chart.SelectDrawing(id);
                return Task.CompletedTask;
            },
            onError: ex => Log.Error(ex, "Chart drawing select stream failed"));
        Closed += (_, _) => _drawingSelected.Dispose();
        _drawingDeleted = ChartPanel.ChartControl.DrawingDeleted.SubscribeAsync(
            id => ViewModel!.Chart.DeleteDrawingAsync(id),
            onError: ex => Log.Error(ex, "Chart drawing delete stream failed"));
        Closed += (_, _) => _drawingDeleted.Dispose();

        // The chart view model is an application-lifetime singleton living
        // for the whole window's stay, so — like the menu handlers above —
        // its save-dialog registration is registered here without disposal:
        // the candles export asks THIS window for the destination (picking a
        // file is view behavior).
        ViewModel?.Chart.SaveCandlesCsvRequested.RegisterHandler(async context =>
        {
            var path = await SaveFilePicker.PickAsync(this, context.Input, "csv", "CSV");
            context.SetOutput(path);
        });

        // The chart's snapshot export (T-094): the destination rides the
        // same dialog discipline, and the render+write is VIEW behavior —
        // the chart control owns its frame, so the handler drives it after
        // the pick (a render or write fault propagates through the
        // interaction into the ViewModel's error-line catch).
        ViewModel?.Chart.ExportPngRequested.RegisterHandler(async context =>
        {
            var path = await SaveFilePicker.PickAsync(this, context.Input.SuggestedFileName, "png", "PNG");
            if (path is null)
            {
                context.SetOutput(null);
                return;
            }

            await ChartPanel.ChartControl.ExportSnapshotPngAsync(path, context.Input.Watermark);
            context.SetOutput(path);
        });

        // The visible-range candles export (T-094): the destination plus the
        // chart's own visible slice travel together (the slice is the
        // control's state) — the ViewModel streams the handoff through the
        // shared CSV writer.
        ViewModel?.Chart.ExportVisibleCsvRequested.RegisterHandler(async context =>
        {
            var path = await SaveFilePicker.PickAsync(this, context.Input, "csv", "CSV");
            if (path is null)
            {
                context.SetOutput(null);
                return;
            }

            context.SetOutput(new VisibleCsvHandoff(path, ChartPanel.ChartControl.VisibleBarsSlice));
        });

        // The T-095 palette's own route: its both interactions live for the
        // window's lifetime (a workspace singleton behind them), and the
        // overlay's key route is a routed-event handler added now and
        // removed with the window (AddHandler/RemoveHandler pair below).
        ViewModel?.Palette.FocusQueryRequested.RegisterHandler(FocusPaletteQueryAsync);
        ViewModel?.Palette.FocusReturnRequested.RegisterHandler(FocusReturnAsync);

        // The compare pick's route (T-124): the chart's CompareCommand asks
        // this window to open the palette in compare mode — the ride
        // completes the interaction with the picked symbol (a null output
        // is the user closing without a pick, never an error).
        ViewModel?.Chart.PickCompareRequested.RegisterHandler(async context =>
        {
            var symbol = await ViewModel!.Palette.BeginComparePickAsync();
            context.SetOutput(symbol);
        });
        // handledEventsToo is the point of the route: the query text box's
        // own caret handling marks Up/Down/PageUp/PageDown handled before
        // the event bubbles here, and the route must outrank it — inside the
        // palette those keys mean row navigation, never caret movement.
        PaletteOverlay.AddHandler(
            InputElement.KeyDownEvent,
            OnPaletteKeyDown,
            RoutingStrategies.Bubble,
            handledEventsToo: true);
        Closed += (_, _) => PaletteOverlay.RemoveHandler(InputElement.KeyDownEvent, OnPaletteKeyDown);

        // The global hotkey route (T-102): the window translates every
        // pressed combination through the live registry map (the VM's
        // TryMatch pass-through) into RouteHotkey. Bubble +
        // handledEventsToo is the point of the route: a bound gesture must
        // outrank the focused text box's own key eats (the select-all and
        // friends), the same outrank shape the palette query box keeps.
        // The dialogs carry their own windows, so the route never fires
        // over a settings surface; while the palette is open the route
        // stands down for everything but its own toggle key (below).
        this.AddHandler(
            InputElement.KeyDownEvent,
            OnGlobalKeyDown,
            RoutingStrategies.Bubble,
            handledEventsToo: true);
        Closed += (_, _) => this.RemoveHandler(InputElement.KeyDownEvent, OnGlobalKeyDown);
    }

    /// <summary>Subscription of the book level click stream; disposed with the window.</summary>
    private IDisposable? _bookClicks;

    /// <summary>Subscription of the signal rail's row-press prefill stream; disposed with the window.</summary>
    private IDisposable? _railPrefills;

    /// <summary>Subscription of the heatmap tab's tile-press stream (T-121); disposed with the window.</summary>
    private IDisposable? _heatmapClicks;

    /// <summary>Subscription of the chart older-history request stream; disposed with the window.</summary>
    private IDisposable? _olderHistoryRequests;

    /// <summary>Subscription of the compare chips' removal stream (T-124); disposed with the window.</summary>
    private IDisposable? _compareRemovals;

    private IDisposable? _drawingCommitted;

    private IDisposable? _drawingEdited;

    private IDisposable? _drawingSelected;

    private IDisposable? _drawingDeleted;

    /// <summary>Subscription of the detached-layout restore leg (T-103); disposed with the window.</summary>
    private IDisposable? _detachedRestore;

    #region Detached panels (T-103)

    /// <summary>The panel slots the view lane hands to the coordinator: the panel control, its inline swap host and the panel's own focus affordance.</summary>
    private DetachedPanelSlot[] BuildPanelSlots() =>
    [
        new DetachedPanelSlot(PanelDetachId.Chart, ChartPanel, ChartPanelHost, null),
        new DetachedPanelSlot(PanelDetachId.Book, BookPanel, BookPanelHost, null),
        new DetachedPanelSlot(
            PanelDetachId.Assistant,
            AssistantPanel,
            AssistantPanelHost,
            () => AssistantPanel.ChatInput.Focus()),
    ];

    /// <summary>
    /// The detached-layout restore: register the slots (the UI thread — the
    /// view lane belongs to it), then let the coordinator re-open whatever
    /// the stored record keeps detached. Faults land in the file log — the
    /// open itself never fails over a detached-window restore.
    /// </summary>
    private async Task RestoreDetachedLayoutAsync()
    {
        try
        {
            var coordinator = App.Services.GetRequiredService<IDetachedPanelCoordinator>();
            await Dispatcher.UIThread.InvokeAsync(() => coordinator.RegisterPanels(BuildPanelSlots()));
            await coordinator.RestoreDetachedAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "The detached-panel layout restore failed");
        }
    }

    #endregion Detached panels (T-103)

    /// <summary>
    /// The tape's clear button: the buffered tape lives entirely in the
    /// widget (rows, pause, symbol switch), so emptying it is a view
    /// concern — no ViewModel state is involved.
    /// </summary>
    private void OnTapeClearClick(object? sender, RoutedEventArgs e) => TapeWidget.Clear();

    #region Palette overlay (T-095)

    /// <summary>
    /// The frames (queued below the render priority) the palette focus may
    /// stay refused for before giving up: the overlay only just flipped
    /// visible, and arrangement can take more than one frame — the chat
    /// focus's budget.
    /// </summary>
    private const int MaxPaletteFocusFrames = 8;

    /// <summary>The pre-open element the palette returns the focus to.</summary>
    private Control? _paletteFocusReturn;

    /// <summary>The element currently holding the keyboard focus, or null.</summary>
    private Control? FindFocusedControl() =>
        TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Control;

    /// <summary>
    /// Land the keyboard focus on the palette query AFTER layout, then
    /// complete the request (the T-012 chat-focus pattern: every attempt
    /// queues below the render priority so the not-yet-arranged box can
    /// accept it). The element under the focus BEFORE any move is stashed
    /// as the return point — a palette opened from the chart drops the
    /// user back on the chart.
    /// </summary>
    private async Task FocusPaletteQueryAsync(IInteractionContext<RxVoid, RxVoid> context)
    {
        _paletteFocusReturn = FindFocusedControl();
        var focused = false;
        for (var attempt = MaxPaletteFocusFrames; attempt > 0 && !focused; attempt--)
        {
            focused = await Dispatcher.UIThread.InvokeAsync(
                () => PaletteQuery.Focus(),
                DispatcherPriority.Background);
        }

        context.SetOutput(RxVoid.Default);
    }

    /// <summary>Hand the pre-open focus back and complete the request.</summary>
    private Task FocusReturnAsync(IInteractionContext<RxVoid, RxVoid> context)
    {
        _paletteFocusReturn?.Focus();
        _paletteFocusReturn = null;
        context.SetOutput(RxVoid.Default);
        return Task.CompletedTask;
    }

    /// <summary>
    /// A press on the backdrop closes the palette — a click in the open
    /// that landed on nothing the panel serves is the mouse's way back out.
    /// The palette never writes state by itself; the close rides the same
    /// command Esc rides.
    /// </summary>
    private void OnPaletteScrimPressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
        var palette = ViewModel?.Palette;
        if (palette is { } owner)
        {
            DispatchOverlayCommand(owner.CloseCommand);
        }
    }

    /// <summary>
    /// The palette's key route — the only place the palette's keys live.
    /// It runs only while the palette is open (the overlay receives key
    /// events from its own subtree alone: the workspace can never trigger
    /// row navigation from outside), and every dispatch re-checks the row
    /// bounds so a keystroke never executes a gated command. The ViewModel
    /// stays keyboard-agnostic: these are its commands and its selection,
    /// the window only translates keys into them.
    /// </summary>
    private void OnPaletteKeyDown(object? sender, KeyEventArgs e)
    {
        var palette = ViewModel?.Palette;
        if (palette is null || !palette.IsOpen)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Up when palette.SelectedIndex > 0:
                e.Handled = true;
                DispatchOverlayCommand(palette.MoveSelectionUpCommand);
                break;
            case Key.Down when palette.HasRowAfter(palette.SelectedIndex):
                e.Handled = true;
                DispatchOverlayCommand(palette.MoveSelectionDownCommand);
                break;
            case Key.PageUp when palette.SelectedIndex > 0:
                e.Handled = true;
                DispatchOverlayCommand(palette.MoveSelectionPageUpCommand);
                break;
            case Key.PageDown when palette.HasRowAfter(palette.SelectedIndex):
                e.Handled = true;
                DispatchOverlayCommand(palette.MoveSelectionPageDownCommand);
                break;
            case Key.Enter when palette.HasRowAt(palette.SelectedIndex):
                e.Handled = true;
                DispatchOverlayCommand(palette.ApplySelectionCommand);
                break;
            case Key.Enter:
                // No row stands to apply: swallow the key so nothing below
                // the route reacts to it, and apply nothing.
                e.Handled = true;
                break;
            case Key.Escape:
                e.Handled = true;
                DispatchOverlayCommand(palette.CloseCommand);
                break;
        }
    }

    /// <summary>
    /// Execute an overlay command with its error handled — the view's only
    /// command dispatch (the palette's and the hotkey sheet's keyboard
    /// routes share it), so a keyboard event never goes fire-and-forget.
    /// </summary>
    private static void DispatchOverlayCommand(ReactiveCommand<RxVoid, RxVoid> command) =>
        command.Execute(RxVoid.Default).Subscribe(
            _ => { },
            error => Log.Error(error, "An overlay command failed"));

    #endregion Palette overlay (T-095)

    #region Hotkey route (T-102)

    /// <summary>
    /// The global hotkey route: the window translates every pressed
    /// combination through the registry's live map and dispatches the
    /// bound action. Bound gestures outrank the focused controls' own key
    /// eats (handledEventsToo), the open palette owns its busy keys (only
    /// its own toggle still routes — pressing the palette's gesture again
    /// is the toggle's close side, the old KeyBinding behavior kept), and
    /// Esc closes the cheat sheet first when it shows. The registry is
    /// the single seam: the menu's advertised gestures print from the
    /// same map this route matches.
    /// </summary>
    private void OnGlobalKeyDown(object? sender, KeyEventArgs e)
    {
        var main = ViewModel;
        var hotkeys = main?.Hotkeys;
        if (main is null || hotkeys is null)
        {
            return;
        }

        // The sheet's way back out: Esc while it shows (and the palette
        // is not the one owning the key) closes it, riding the same
        // toggle command the menu checkbox routes.
        if (hotkeys.IsSheetVisible && !main.Palette.IsOpen && e.Key == Key.Escape)
        {
            e.Handled = true;
            DispatchOverlayCommand(hotkeys.ToggleSheetCommand);
            return;
        }

        if (!hotkeys.TryMatch(e.Key, e.KeyModifiers, out var actionId))
        {
            return;
        }

        if (main.Palette.IsOpen && actionId != HotkeyRegistry.TogglePalette)
        {
            // The palette is the keyboard's owner until it closes: its
            // navigation keys never reach here (the overlay marks them
            // handled and the palette's rows are its own business), and
            // the other global gestures stand down — the palette route
            // itself stays live so its gesture toggles the palette shut.
            return;
        }

        e.Handled = true;
        main.RouteHotkey(actionId);
    }

    /// <summary>
    /// A press on the cheat sheet's backdrop closes it — the same way
    /// back out the palette's scrim keeps; the close rides the same
    /// toggle command the menu checkbox routes, so the Help menu's stick
    /// state stays the one source of truth.
    /// </summary>
    private void OnHotkeysSheetScrimPressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
        var hotkeys = ViewModel?.Hotkeys;
        if (hotkeys is { } owner)
        {
            DispatchOverlayCommand(owner.ToggleSheetCommand);
        }
    }

    #endregion Hotkey route (T-102)

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
    /// landed (or the budget ran out). A DETACHED assistant (T-103) routes
    /// through the coordinator instead: it activates the panel's own window
    /// and lands the same deferred focus on the same input control there.
    /// </summary>
    private async Task FocusChatAsync(IInteractionContext<RxVoid, RxVoid> context)
    {
        var coordinator = App.Services.GetService<IDetachedPanelCoordinator>();
        if (coordinator is not null && coordinator.IsDetached(PanelDetachId.Assistant))
        {
            await coordinator.FocusPanelAsync(PanelDetachId.Assistant, CancellationToken.None);
            context.SetOutput(RxVoid.Default);
            return;
        }

        var focused = false;
        for (var attempt = MaxChatFocusFrames; attempt > 0 && !focused; attempt--)
        {
            focused = await Dispatcher.UIThread.InvokeAsync(
                () => AssistantPanel.ChatInput.Focus(),
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
    /// Land the menu's (and the hotkey's) kill-switch route (T-188): the
    /// sweep service runs behind its ONE confirmation, and the localized
    /// report window renders its per-venue outcome. A refused confirmation
    /// reports nothing — the refusal is the user's answer; a sweep fault
    /// logs loud and the route still settles (the same posture the other
    /// window routes keep).
    /// </summary>
    private async Task RunKillSwitchAsync(IInteractionContext<RxVoid, RxVoid> context)
    {
        try
        {
            var service = App.Services.GetRequiredService<KillSwitchService>();
            var result = await service.ExecuteAsync();
            if (result.IsDeclined)
            {
                return;
            }

            var report = new KillSwitchReportWindow
            {
                DataContext = new KillSwitchReportViewModel(result.Summary ?? string.Empty),
            };
            await report.ShowDialog(this);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to run the kill switch");
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
            await ShowOrderHistoryWindowAsync();
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

    /// <summary>
    /// The order history window's core: the view model is transient, so
    /// every open re-reads the persisted order journal — a restarted app
    /// never shows stale rows. Shared by the menu route and the journal
    /// toasts' own route.
    /// </summary>
    private async Task ShowOrderHistoryWindowAsync()
    {
        var window = new OrderHistoryWindow
        {
            DataContext = App.Services.GetRequiredService<ViewModels.OrderHistoryViewModel>(),
        };
        await window.ShowDialog<object>(this);
    }

    private async Task OpenTradeStatsAsync(IInteractionContext<RxVoid, RxVoid> context)
    {
        try
        {
            await ShowTradeStatsWindowAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to open the trade statistics window");
        }
        finally
        {
            context.SetOutput(RxVoid.Default);
        }
    }

    private async Task OpenAlertsAsync(IInteractionContext<RxVoid, RxVoid> context)
    {
        try
        {
            await ShowAlertsWindowAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to open the alerts window");
        }
        finally
        {
            context.SetOutput(RxVoid.Default);
        }
    }

    /// <summary>
    /// The alerts window's core: the view model is transient, so every open
    /// renders the CURRENT roster over the shared singleton engine — the
    /// attach pattern owns the subscriptions, the opener only opens.
    /// </summary>
    private async Task ShowAlertsWindowAsync()
    {
        var window = new AlertsWindow
        {
            DataContext = App.Services.GetRequiredService<ViewModels.AlertsViewModel>(),
        };
        await window.ShowDialog<object>(this);
    }

    /// <summary>
    /// The trade statistics window's core: the view model is transient, so
    /// every open starts a fresh scan of the persisted order journal — the
    /// tiles never show stale runs. Shared by the menu route and the fill
    /// toasts' own route.
    /// </summary>
    private async Task ShowTradeStatsWindowAsync()
    {
        var window = new TradeStatsWindow
        {
            DataContext = App.Services.GetRequiredService<ViewModels.TradeStatsViewModel>(),
        };
        await window.ShowDialog<object>(this);
    }

    /// <summary>
    /// The backtest window's core: the view model is transient, so every
    /// open arms a fresh run form over the shared singleton harness — the
    /// attach pattern owns the subscriptions, the opener only opens.
    /// </summary>
    private async Task ShowBacktestWindowAsync()
    {
        var window = new BacktestWindow
        {
            DataContext = App.Services.GetRequiredService<ViewModels.BacktestRunViewModel>(),
        };
        await window.ShowDialog<object>(this);
    }

    private async Task OpenBacktestAsync(IInteractionContext<RxVoid, RxVoid> context)
    {
        try
        {
            await ShowBacktestWindowAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to open the backtest window");
        }
        finally
        {
            context.SetOutput(RxVoid.Default);
        }
    }

    private async Task OpenNotificationHistoryAsync(IInteractionContext<RxVoid, RxVoid> context)
    {
        try
        {
            await ShowNotificationHistoryWindowAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to open the notification history window");
        }
        finally
        {
            context.SetOutput(RxVoid.Default);
        }
    }

    /// <summary>
    /// The notification history window's core: the view model is transient,
    /// so every open attaches the live lane and seeds the session snapshot
    /// fresh (the window's activation drives the pair). Shared by the menu
    /// route and the summary toast's own route.
    /// </summary>
    private async Task ShowNotificationHistoryWindowAsync()
    {
        var window = new NotificationHistoryWindow
        {
            DataContext = App.Services.GetRequiredService<ViewModels.NotificationHistoryViewModel>(),
        };
        await window.ShowDialog<object>(this);
    }

    /// <summary>
    /// The log viewer window's core (T-101): the view model is transient, so
    /// every open attaches the live tail lane and seeds fresh from the tail's
    /// history snapshot (the window's activation drives the pair). The tail
    /// service itself is the process-wide singleton — one bounded redacted
    /// ring across every open.
    /// </summary>
    private async Task ShowLogViewerWindowAsync()
    {
        var window = new LogViewerWindow
        {
            DataContext = App.Services.GetRequiredService<ViewModels.LogViewerViewModel>(),
        };
        await window.ShowDialog<object>(this);
    }

    /// <summary>
    /// Land the menu's show-log-viewer route: the window opens modally, a
    /// failure logs loud and the menu route still settles (the same posture
    /// the other window routes keep).
    /// </summary>
    private async Task OpenLogViewerAsync(IInteractionContext<RxVoid, RxVoid> context)
    {
        try
        {
            await ShowLogViewerWindowAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to open the log viewer window");
        }
        finally
        {
            context.SetOutput(RxVoid.Default);
        }
    }

    /// <summary>
    /// Land one clicked toast's route: the switch carries the tested
    /// <see cref="NotificationRouteMap"/> through the window plumbing —
    /// every mapped route opens its panel, and
    /// <see cref="NotificationRoute.None"/> is the plain dismissal the host
    /// already performed. An unnamed route value faults through the log,
    /// never a silently swallowed click.
    /// </summary>
    private async Task OpenNotificationRouteAsync(IInteractionContext<NotificationRoute, RxVoid> context)
    {
        try
        {
            switch (context.Input)
            {
                case NotificationRoute.TradeStatistics:
                    await ShowTradeStatsWindowAsync();
                    break;
                case NotificationRoute.OrderHistory:
                    await ShowOrderHistoryWindowAsync();
                    break;
                case NotificationRoute.HistoryDrawer:
                    await ShowNotificationHistoryWindowAsync();
                    break;
                case NotificationRoute.None:
                    // The dismissal is the whole action; no panel exists.
                    break;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(context),
                        context.Input,
                        "The notification route has no window.");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to land the notification route {Route}", context.Input);
        }
        finally
        {
            context.SetOutput(RxVoid.Default);
        }
    }

    private async Task OpenLicenseActivationAsync(IInteractionContext<RxVoid, RxVoid> context)
    {
        try
        {
            // The license activation dialog (T-132): a fresh view model per
            // open (transient — no pasted key or verdict carries over), the
            // dialog closes itself on a stored activation, a refusal keeps
            // it open with the verdict shown.
            var viewModel = App.Services.GetRequiredService<ViewModels.LicenseActivationViewModel>();
            var dialog = new LicenseActivationWindow
            {
                DataContext = viewModel,
            };
            await dialog.ShowDialog(this);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to open the license activation dialog");
        }
        finally
        {
            context.SetOutput(RxVoid.Default);
        }
    }

    private async Task OpenDisclaimerAsync(IInteractionContext<RxVoid, RxVoid> context)
    {
        try
        {
            // The same dialog the first run shows: a fresh acknowledge write
            // (the shipped version is idempotent there) or another look at
            // the already-acknowledged wording.
            var viewModel = App.Services.GetRequiredService<ViewModels.DisclaimerDialogViewModel>();
            var dialog = new DisclaimerDialogWindow
            {
                DataContext = viewModel,
            };
            await dialog.ShowDialog(this);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to open the disclaimer dialog");
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
                // The hotkey manager (T-102) is a shared app-lifetime
                // singleton: the settings window's tab binds it through
                // the window's own Hotkeys property (an Avalonia styled
                // property, not the ViewModel — the settings ViewModel
                // stays untouched by the hotkey seam).
                Hotkeys = ViewModel!.Hotkeys,
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
