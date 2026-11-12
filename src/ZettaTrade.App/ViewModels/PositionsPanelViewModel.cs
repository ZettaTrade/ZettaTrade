namespace ZettaTrade.App.ViewModels;

using System.Globalization;
using System.Linq;
using System.Reactive.Linq;
using Livery.Localization;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ReactiveUI.Primitives;
using ZettaTrade.App.Services;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Infrastructure.Persistence;

/// <summary>
/// Positions and orders panel view model (modal window). Loads positions,
/// open orders and the session journal from the current trading adapter on
/// demand, routes cancel requests through the mandatory pipeline:
/// <see cref="IConfirmationService"/> → <see cref="ITradingAdapter.CancelOrderAsync"/>
/// where the adapter always receives the order's own symbol and id (the
/// Symbol-first contract) and the SQLite order history is updated after the
/// adapter confirms the cancellation, and routes amend requests through the
/// <see cref="Services.OrderAmendService"/> pipeline: the view asks for the
/// edits through <see cref="AmendRequested"/> (window plumbing stays in the
/// View), then the service re-reads the adapter state, revalidates the
/// amended shape through <see cref="IRiskManager"/>, walks large-delta
/// amends through the confirmation dialog and re-places the order — the
/// row shows the amending state for as long as the replace is in flight.
/// Position rows carry the action surface (close, partial close, reverse)
/// plus the protective SL/TP edit (T-187): the requests ride the
/// <see cref="PositionActionsService"/> and
/// <see cref="ProtectiveLevelsService"/> pipelines (validate → risk →
/// confirmation → adapter), every attempt is serialized
/// by one in-flight gate — a busy panel disables the rows' action commands,
/// so a double click is a no-op — and the sections reload after every
/// resolved attempt. The orders section carries ONE bulk action (T-076):
/// cancel every working order on the workspace's active symbol through the
/// <see cref="Services.BulkCancelService"/> — the service re-reads the
/// adapter state at click time, one confirmation gates the whole pass, and
/// every order whose per-row pipeline is already in flight is skipped and
/// reported instead of double-commanded.
/// </summary>
public sealed class PositionsPanelViewModel : ReactiveObject
{
    #region Resx Keys

    private const string LoadFailedKey = "Positions.LoadFailed";
    private const string CancelledKey = "Positions.Cancelled";
    private const string CancelFailedKey = "Positions.CancelFailed";
    private const string AmendFailedKey = "Positions.AmendFailed";
    private const string ActionFailedKey = "Positions.ActionFailedFormat";
    private const string FeeUnknownKey = "Fees.Unknown";
    private const string FeeDisplayFormat = "F8";

    #endregion Resx Keys

    #region Private Fields

    private readonly ITradingAdapter _trading;
    private readonly IOrderJournal _journal;
    private readonly IOrderHistoryStore _orderHistory;
    private readonly IConfirmationService _confirmation;
    private readonly OrderConfirmationComposer _composer;
    private readonly OrderAmendService _amendService;
    private readonly PositionActionsService _actions;
    private readonly ProtectiveLevelsService _protect;
    private readonly BulkCancelService _bulkCancel;
    private readonly ILocalizationManager _localization;
    private readonly ILogger<PositionsPanelViewModel> _logger;

    /// <summary>Serializes the cancel pipeline; re-entrancy is an error state.</summary>
    private bool _cancelling;

    /// <summary>
    /// Id of the order whose per-row cancel flow is in flight: the bulk pass
    /// skips and reports this row instead of double-commanding it.
    /// </summary>
    private string? _cancellingOrderId;

    /// <summary>Serializes the amend pipeline; never two pipelined amends.</summary>
    private bool _amending;

    /// <summary>Id of the order whose amend is in flight; drives the row's amending state.</summary>
    private string? _amendingOrderId;

    /// <summary>Serializes the bulk pass; re-entrancy is an error state.</summary>
    private bool _bulkCancelling;

    /// <summary>Serializes the action pipeline; re-entrancy is an error state.</summary>
    private bool _isBusy;

    /// <summary>Whether the traded surface currently rides the TESTNET lane (T-127; set by the owning window).</summary>
    private bool _isTestnetLane;

    /// <summary>The shared action-surface gate: values false while any idle action runs.</summary>
    private readonly IObservable<bool> _actionsIdle;

    #endregion Private Fields

    #region Properties

    private IReadOnlyList<PositionRowViewModel> _positions = [];
    private IReadOnlyList<OrderRowViewModel> _openOrders = [];
    private IReadOnlyList<OrderRowViewModel> _recentOrders = [];
    private string? _errorMessage;
    private string? _resultMessage;

    /// <summary>Loaded open positions from the trading adapter.</summary>
    public IReadOnlyList<PositionRowViewModel> Positions
    {
        get => _positions;
        private set
        {
            if (Positions == value)
            {
                return;
            }

            _positions = value;
            this.RaisePropertyChanged(nameof(Positions));
            this.RaisePropertyChanged(nameof(HasPositions));
        }
    }

    /// <summary>True while <see cref="Positions"/> is non-empty; drives empty-state visibility.</summary>
    public bool HasPositions => _positions.Count > 0;

    /// <summary>Loaded open orders from the trading adapter.</summary>
    public IReadOnlyList<OrderRowViewModel> OpenOrders
    {
        get => _openOrders;
        private set
        {
            if (OpenOrders == value)
            {
                return;
            }

            _openOrders = value;
            this.RaisePropertyChanged(nameof(OpenOrders));
            this.RaisePropertyChanged(nameof(HasOpenOrders));
        }
    }

    /// <summary>True while <see cref="OpenOrders"/> is non-empty; drives empty-state visibility.</summary>
    public bool HasOpenOrders => _openOrders.Count > 0;

    /// <summary>Orders recorded in the session journal, newest first.</summary>
    public IReadOnlyList<OrderRowViewModel> RecentOrders
    {
        get => _recentOrders;
        private set
        {
            if (RecentOrders == value)
            {
                return;
            }

            _recentOrders = value;
            this.RaisePropertyChanged(nameof(RecentOrders));
            this.RaisePropertyChanged(nameof(HasRecentOrders));
        }
    }

    /// <summary>True while <see cref="RecentOrders"/> is non-empty; drives empty-state visibility.</summary>
    public bool HasRecentOrders => _recentOrders.Count > 0;

    /// <summary>Last flow error (load failure, cancel failure).</summary>
    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (ErrorMessage == value)
            {
                return;
            }

            _errorMessage = value;
            this.RaisePropertyChanged(nameof(ErrorMessage));
            this.RaisePropertyChanged(nameof(HasErrorMessage));
        }
    }

    /// <summary>True while <see cref="ErrorMessage"/> holds text; drives the error line visibility.</summary>
    public bool HasErrorMessage => _errorMessage is not null;

    /// <summary>Text of the last successful cancellation.</summary>
    public string? ResultMessage
    {
        get => _resultMessage;
        private set
        {
            if (ResultMessage == value)
            {
                return;
            }

            _resultMessage = value;
            this.RaisePropertyChanged(nameof(ResultMessage));
            this.RaisePropertyChanged(nameof(HasResultMessage));
        }
    }

    /// <summary>True while <see cref="ResultMessage"/> holds text; drives the result line visibility.</summary>
    public bool HasResultMessage => _resultMessage is not null;

    /// <summary>
    /// True while a position action (close, partial close or reverse) is in
    /// flight; the rows' action commands ride its negation as canExecute, so
    /// an in-flight attempt disables the whole action surface — a double
    /// click resolves into the disabled (queued) state and stays a no-op.
    /// </summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set => this.RaiseAndSetIfChanged(ref _isBusy, value);
    }

    /// <summary>
    /// Whether the workspace's traded surface currently rides the TESTNET
    /// lane (T-127): the panel's badge mirrors the status-bar chip so the
    /// operator sees the venue surface the position and order actions ride.
    /// Set by the owning window from the trading-mode lane stream — the view
    /// model holds no lane service of its own.
    /// </summary>
    public bool IsTestnetLane
    {
        get => _isTestnetLane;
        set => this.RaiseAndSetIfChanged(ref _isTestnetLane, value);
    }

    /// <summary>Reloads every section from the adapter and the journal.</summary>
    public ReactiveCommand<RxVoid, RxVoid> LoadCommand { get; }

    /// <summary>
    /// The amend dialog handoff: the view model asks for the edits over one
    /// order, the View opens the amend dialog window and answers with the
    /// parsed <see cref="Services.OrderAmendRequest"/> — or null when the
    /// user cancelled the dialog. Window plumbing stays in the View.
    /// </summary>
    public Interaction<Order, OrderAmendRequest?> AmendRequested { get; }

    /// <summary>
    /// The protective-levels dialog handoff (T-187): the view model asks for
    /// the level edits over one position, the View opens the protect dialog
    /// window and answers with the parsed <see cref="Services.ProtectiveLevelsEdits"/>
    /// — or null when the user cancelled the dialog. Window plumbing stays
    /// in the View.
    /// </summary>
    public Interaction<Position, ProtectiveLevelsEdits?> ProtectRequested { get; }

    /// <summary>
    /// The bulk action of the orders section (T-076): confirm once, then
    /// cancel every working order the adapter still serves on the workspace's
    /// active symbol. Enabled while the panel is idle and the section is
    /// non-empty; the rows already in flight are skipped and reported by the
    /// service, never double-commanded.
    /// </summary>
    public ReactiveCommand<RxVoid, RxVoid> CancelAllCommand { get; }

    #endregion Properties

    #region Constructor

    /// <summary>
    /// Create a positions panel view model. The view model is transient: a
    /// fresh instance reloads the current state on every panel open.
    /// </summary>
    public PositionsPanelViewModel(
        ITradingAdapter trading,
        IOrderJournal journal,
        IOrderHistoryStore orderHistory,
        IConfirmationService confirmation,
        OrderConfirmationComposer composer,
        OrderAmendService amendService,
        PositionActionsService actions,
        ProtectiveLevelsService protect,
        BulkCancelService bulkCancel,
        ILocalizationManager localization,
        ILogger<PositionsPanelViewModel> logger)
    {
        _trading = trading ?? throw new ArgumentNullException(nameof(trading));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _orderHistory = orderHistory ?? throw new ArgumentNullException(nameof(orderHistory));
        _confirmation = confirmation ?? throw new ArgumentNullException(nameof(confirmation));
        _composer = composer ?? throw new ArgumentNullException(nameof(composer));
        _amendService = amendService ?? throw new ArgumentNullException(nameof(amendService));
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));
        _protect = protect ?? throw new ArgumentNullException(nameof(protect));
        _bulkCancel = bulkCancel ?? throw new ArgumentNullException(nameof(bulkCancel));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // The gate emits its current state at subscribe, so every command
        // created afterwards binds the standing idle/busy value; property
        // changes follow on the thread that raised them.
        _actionsIdle = this
            .WhenAnyValue(x => x.IsBusy, busy => !busy);

        LoadCommand = ReactiveCommand.CreateFromTask(HandleLoadAsync);
        AmendRequested = new Interaction<Order, OrderAmendRequest?>();
        ProtectRequested = new Interaction<Position, ProtectiveLevelsEdits?>();
        CancelAllCommand = ReactiveCommand.CreateFromTask(
            HandleBulkCancelAsync,
            Observable.CombineLatest(
                _actionsIdle,
                this.WhenAnyValue(x => x.HasOpenOrders),
                (idle, hasOpenOrders) => idle && hasOpenOrders));
    }

    #endregion Constructor

    #region Private Methods

    private async Task HandleLoadAsync()
    {
        // A fresh load replaces whatever a previous flow reported.
        ErrorMessage = null;
        ResultMessage = null;

        await ReloadSectionsAsync();
    }

    private async Task HandleCancelAsync(Order order)
    {
        // The command can only fire from one row at a time in practice, but
        // re-entrancy (double click) must still be impossible: never two
        // pipelined cancellations.
        if (_cancelling)
        {
            return;
        }

        // A fresh attempt replaces whatever the previous attempt reported.
        ErrorMessage = null;
        ResultMessage = null;

        _cancelling = true;
        _cancellingOrderId = order.Id;
        try
        {
            var confirmed = await _confirmation.RequestConfirmationAsync(_composer.ForCancel(order));
            if (!confirmed)
            {
                _logger.LogInformation(
                    "Positions panel: user declined the cancellation of {OrderId}",
                    order.Id);
                return;
            }

            // The adapter contract takes the symbol first and it must be the
            // order's own symbol — enforced here by construction.
            await _trading.CancelOrderAsync(order.Symbol, order.Id);

            // Record the cancellation in the persistent order history. Best
            // effort: the adapter has already cancelled the order, so a
            // persistence fault must never undo or fail the cancellation.
            try
            {
                await _orderHistory.MarkCancelledAsync(order.ExchangeId, order.Id, DateTimeOffset.UtcNow);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Positions panel: failed to mark {OrderId} cancelled in the order history",
                    order.Id);
            }

            ResultMessage = _localization.GetString(CancelledKey, order.Id);
            _logger.LogInformation(
                "Positions panel: cancelled {OrderId} on {Symbol}",
                order.Id,
                order.Symbol.Normalized);

            // Refresh the sections so the cancelled order leaves the lists.
            await ReloadSectionsAsync();
        }
        catch (Exception ex)
        {
            // Broader than the adapter's validation refusals: the cancel
            // reaches the trading adapter over the same surface a network
            // request rides, so a failed cancellation can surface as
            // anything. Every failure lands as the localized error line
            // instead of escaping to an unobserved
            // ReactiveCommand.ThrownExceptions (stale lists, silent panel).
            _logger.LogError(ex, "Positions panel: cancellation failed");
            ErrorMessage = _localization.GetString(CancelFailedKey, ex.Message);
        }
        finally
        {
            _cancelling = false;
            _cancellingOrderId = null;
        }
    }

    /// <summary>
    /// The amend flow for one order: the row state shows amending while the
    /// flow is in flight, the View hands back the parsed edits (null = the
    /// user cancelled the dialog), and the <see cref="Services.OrderAmendService"/>
    /// pipeline carries the state re-read, the risk re-check, the
    /// confirmation rule and the replace. The result line renders the
    /// outcome — including the honest cancel+new-or-fill-won outcomes.
    /// </summary>
    private async Task HandleAmendAsync(Order order)
    {
        // The command can only fire from one row at a time in practice, but
        // re-entrancy (double click, the dialog reopened while the flow
        // runs) must still be impossible: never two pipelined amends.
        if (_amending)
        {
            return;
        }

        // A fresh attempt replaces whatever the previous attempt reported.
        ErrorMessage = null;
        ResultMessage = null;

        _amending = true;
        _amendingOrderId = order.Id;
        MarkAmendingRow(order.Id, isAmending: true);
        try
        {
            var edits = await AmendRequested.Handle(order);
            if (edits is null)
            {
                _logger.LogInformation(
                    "Positions panel: the amend dialog of {OrderId} closed without an amend",
                    order.Id);
                return;
            }

            var result = await _amendService.AmendAsync(edits);
            if (result.Message is { } message)
            {
                if (result.Succeeded)
                {
                    ResultMessage = message;
                }
                else
                {
                    ErrorMessage = message;
                }
            }

            // The state clears BEFORE the reload: the fresh rows must render
            // the post-amend state, not the still-amending marker.
            _amending = false;
            _amendingOrderId = null;
            await ReloadSectionsAsync();
        }
        catch (Exception ex)
        {
            // Broader than the service's own conversion: only an unexpected
            // fault of the dialog handoff itself can reach here, and the
            // panel must report it instead of going silently stale.
            _logger.LogError(ex, "Positions panel: the amend flow of {OrderId} failed", order.Id);
            ErrorMessage = _localization.GetString(AmendFailedKey, ex.Message);
        }
        finally
        {
            // The declared end of every path, including the
            // dialog-cancelled early return: the row's amending state ends
            // exactly when the flow does.
            _amending = false;
            _amendingOrderId = null;
            MarkAmendingRow(order.Id, isAmending: false);
        }
    }

    /// <summary>
    /// Flip the amending marker on the row that carries
    /// <paramref name="orderId"/> in both order sections: the list itself is
    /// rebuilt only at reload, so an in-flight reload cannot wipe the marker
    /// (the row factory reads the id field at rebuild too).
    /// </summary>
    private void MarkAmendingRow(string orderId, bool isAmending)
    {
        OpenOrders = OpenOrders
            .Select(row => row.OrderId == orderId ? row with { IsAmending = isAmending } : row)
            .ToArray();
        RecentOrders = RecentOrders
            .Select(row => row.OrderId == orderId ? row with { IsAmending = isAmending } : row)
            .ToArray();
    }

    /// <summary>
    /// The bulk flow for the orders section (T-076): one confirmation over
    /// the pass, then the service cancels every working order the adapter
    /// still serves on the workspace's active symbol. The in-flight ids of
    /// the panel's own per-row pipelines (the flow currently cancelling, the
    /// flow currently amending) ride into the pass as the skip set — the
    /// service reports those rows and never double-commands them. The
    /// service resolves every path into a result; this coordination adds no
    /// error handling of its own beyond the unloaded flow's cancellation.
    /// </summary>
    private async Task HandleBulkCancelAsync()
    {
        // Belt-and-braces against a raced click: the canExecute gate disables
        // the command, but anything that still arrives while a pass runs is
        // dropped, not queued into a second pipeline.
        if (_bulkCancelling)
        {
            return;
        }

        // A fresh attempt replaces whatever the previous attempt reported.
        ErrorMessage = null;
        ResultMessage = null;

        _bulkCancelling = true;
        IsBusy = true;
        try
        {
            var overrideIds = new[] { _cancellingOrderId, _amendingOrderId };
            var inFlight = overrideIds
                .Where(id => id is not null)
                .Select(id => id!)
                .ToArray();

            var result = await _bulkCancel.CancelWorkingOrdersAsync(inFlight);

            // The sections refresh BEFORE the outcome line so the lists the
            // rows render are the ones the report describes.
            await ReloadSectionsAsync();

            switch (result.Outcome)
            {
                case BulkCancelOutcome.Done:
                    ResultMessage = result.ResultMessage;
                    break;

                case BulkCancelOutcome.Failed:
                    ErrorMessage = result.ErrorMessage;
                    break;
            }
        }
        finally
        {
            // The shared action-surface gate reopens exactly when the pass
            // ends: the position rows' commands were disabled for it too.
            IsBusy = false;
            _bulkCancelling = false;
        }
    }

    /// <summary>
    /// Close (flatten) the position the row rendered through the service
    /// pipeline — market exit, risk-re-checked and confirmed.
    /// </summary>
    private async Task HandleCloseAsync(Position position) =>
        await RunActionAsync(() => _actions.CloseAsync(position.Symbol));

    /// <summary>
    /// Partially close the position the row rendered by the amount the row's
    /// reduce cell holds; the service parses and validates it against the
    /// position as it stands (an empty, unparsable or oversized amount is an
    /// explicit localized rejection).
    /// </summary>
    private async Task HandleReduceAsync(Position position, PositionRowViewModel row) =>
        await RunActionAsync(
            () => _actions.ReduceAsync(position.Symbol, row.ReduceQuantityInput));

    /// <summary>
    /// Reverse the position the row rendered: the service places the
    /// reduce-only exit and the opposite-side re-entry behind ONE mandatory
    /// confirmation, with both legs risk-checked — a refusal or a failed
    /// guard leaves the flow aborted and reported.
    /// </summary>
    private async Task HandleReverseAsync(Position position) =>
        await RunActionAsync(() => _actions.ReverseAsync(position.Symbol));

    /// <summary>
    /// The protective-levels flow for one position (T-187): the View hands
    /// back the parsed level edits (null = the user cancelled the dialog),
    /// and the <see cref="ProtectiveLevelsService"/> pipeline carries the
    /// fresh state re-read, the direction checks, the risk pair, the ONE
    /// confirmation and the cancel+place rewrite — under the same in-flight
    /// gate the other position actions ride, with the sections reloading on
    /// resolution and the outcome line landing last.
    /// </summary>
    private async Task HandleProtectAsync(Position position)
    {
        // Belt-and-braces against a raced click: the canExecute gate disables
        // the commands, but anything that still arrives while an attempt
        // runs is dropped, not queued into a second pipeline.
        if (IsBusy)
        {
            return;
        }

        // A fresh attempt replaces whatever the previous attempt reported.
        ErrorMessage = null;
        ResultMessage = null;

        IsBusy = true;
        try
        {
            var edits = await ProtectRequested.Handle(position);
            if (edits is null)
            {
                _logger.LogInformation(
                    "Positions panel: the protective-levels dialog of {Symbol} closed without an edit",
                    position.Symbol.Normalized);
                return;
            }

            var result = await _protect.ProtectAsync(position, edits.StopLoss, edits.TakeProfit);

            // The sections refresh BEFORE the outcome line so the lists the
            // rows render are the ones the report describes.
            await ReloadSectionsAsync();

            switch (result.Outcome)
            {
                case PositionActionOutcome.Placed:
                    ResultMessage = result.ResultMessage;
                    break;

                case PositionActionOutcome.Failed:
                    ErrorMessage = result.ErrorMessage;
                    break;
            }
        }
        catch (Exception ex)
        {
            // Broader than the service's own conversion: only an unexpected
            // fault of the dialog handoff itself can reach here, and the
            // panel must report it instead of going silently stale.
            _logger.LogError(ex, "Positions panel: the protective-levels flow of {Symbol} failed", position.Symbol.Normalized);
            ErrorMessage = _localization.GetString(ActionFailedKey, ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Run one position action under the in-flight gate: the gate disables
    /// the rows' action commands for its duration (a double click is a
    /// no-op), the sections reload on resolution, and the attempt's own
    /// outcome line lands last — a placed attempt reports its result, a
    /// failed one its localized reason, and a refused confirmation stays
    /// silent like the new-order dialog's does. The service resolves every
    /// path into a result; this coordination adds no error handling of its
    /// own beyond the unloaded flow's cancellation.
    /// </summary>
    private async Task RunActionAsync(Func<Task<PositionActionResult>> attempt)
    {
        // Belt-and-braces against a raced click: the canExecute gate disables
        // the commands, but anything that still arrives while an attempt
        // runs is dropped, not queued into a second pipeline.
        if (IsBusy)
        {
            return;
        }

        // A fresh attempt replaces whatever the previous attempt reported.
        ErrorMessage = null;
        ResultMessage = null;

        IsBusy = true;
        try
        {
            var result = await attempt();

            // The sections refresh BEFORE the outcome line so the lists the
            // rows render are the ones the report describes.
            await ReloadSectionsAsync();

            switch (result.Outcome)
            {
                case PositionActionOutcome.Placed:
                    ResultMessage = result.ResultMessage;
                    break;

                case PositionActionOutcome.Failed:
                    ErrorMessage = result.ErrorMessage;
                    break;
            }
        }
        finally
        {
            IsBusy = false;
        }
    }


    private async Task ReloadSectionsAsync()
    {
        try
        {
            var openOrders = await _trading.GetOpenOrdersAsync();
            var positions = await _trading.GetPositionsAsync();
            var recent = _journal.GetRecent();

            Positions = positions.Select(CreatePositionRow).ToArray();
            OpenOrders = openOrders.Select(CreateOrderRow).ToArray();
            RecentOrders = recent.Select(CreateOrderRow).ToArray();

            _logger.LogInformation(
                "Positions panel: loaded {PositionCount} positions, {OpenOrderCount} open orders, {RecentCount} recent orders",
                Positions.Count,
                OpenOrders.Count,
                RecentOrders.Count);
        }
        catch (Exception ex)
        {
            // Broader than the adapter's validation refusals — same class of
            // surface as the cancel path above: a load failure can be a
            // network request as easily as a validation fault, and the
            // panel must report it instead of showing stale/empty sections
            // silently (unobserved ThrownExceptions are no reporting).
            _logger.LogError(ex, "Positions panel: load failed");
            ErrorMessage = _localization.GetString(LoadFailedKey, ex.Message);
        }
    }

    /// <summary>
    /// Build the immutable display row for one position, wiring its action
    /// surface: close, partial close (from the row's own reduce input),
    /// reverse and the protective SL/TP edit all close over the position the
    /// row rendered, ride the service pipelines, and share the in-flight
    /// gate — an attempt in flight disables every row's action commands
    /// until it resolves. The SL/TP cells read the levels the position
    /// itself reports (the working protective legs' values the venue
    /// surfaces); an unprotected position renders the honest empty cell.
    /// </summary>
    private PositionRowViewModel CreatePositionRow(Position position)
    {
        // The journal stores UTC; the display follows the local wall clock —
        // the one convention the order history window renders too, so the
        // same order shows the same time in both windows (BUG-055).
        // The reduce command captures the row local before its assignment
        // completes — safe by construction: the closure reads the typed
        // value at execution time, long after the build assigns the row.
        var row = (PositionRowViewModel)null!;
        row = new PositionRowViewModel
        {
            Symbol = position.Symbol.Normalized,
            Side = _localization.GetString($"Order.{position.Side}"),
            IsBuySide = position.Side is OrderSide.Buy,
            Quantity = position.Quantity.Value.ToString(CultureInfo.InvariantCulture),
            EntryPrice = position.EntryPrice.Value.ToString("F8", CultureInfo.InvariantCulture),
            StopLoss = position.StopLoss is { } stop
                ? stop.Value.ToString("F8", CultureInfo.InvariantCulture)
                : string.Empty,
            TakeProfit = position.TakeProfit is { } takeProfit
                ? takeProfit.Value.ToString("F8", CultureInfo.InvariantCulture)
                : string.Empty,
            Opened = position.OpenedAt.ToLocalTime().ToString(TimeFormats.OrderTimestamp, CultureInfo.InvariantCulture),
            CloseCommand = ReactiveCommand.CreateFromTask(
                () => HandleCloseAsync(position),
                _actionsIdle),
            ReduceCommand = ReactiveCommand.CreateFromTask(
                () => HandleReduceAsync(position, row),
                _actionsIdle),
            ReverseCommand = ReactiveCommand.CreateFromTask(
                () => HandleReverseAsync(position),
                _actionsIdle),
            EditProtectCommand = ReactiveCommand.CreateFromTask(
                () => HandleProtectAsync(position),
                _actionsIdle),
        };

        return row;
    }

    /// <summary>
    /// Build the immutable display row for one order. The per-row cancel
    /// command closes over the full order (adapter symbol + id) and is
    /// enabled exactly when the status is still cancellable upstream; the
    /// per-row amend command is enabled for the statuses AND types the
    /// amend flow accepts, and the amending marker renders while that
    /// row's flow is in flight.
    /// </summary>
    private OrderRowViewModel CreateOrderRow(Order order)
    {
        var isCancellable = order.Status
            is OrderStatus.Pending
            or OrderStatus.Submitted
            or OrderStatus.PartiallyFilled;

        // The amendable set narrows the cancellable one by type: market and
        // stop-market orders execute at once and are never amendable, and
        // the OCO legs the service refuses stay disabled at the button too.
        // A partially filled working order stays amendable — its amend asks
        // for the confirmation the flow's rule defines (fills rewrite the
        // remaining exposure).
        var isAmendable = isCancellable
            && order.Type is OrderType.Limit or OrderType.StopLimit or OrderType.TrailingStop
            && order.OcoGroupId is null;

        return new OrderRowViewModel
        {
            OrderId = order.Id,
            Symbol = order.Symbol.Normalized,
            Side = _localization.GetString($"Order.{order.Side}"),
            IsBuySide = order.Side is OrderSide.Buy,
            Quantity = order.Quantity.Value.ToString(CultureInfo.InvariantCulture),
            Price = order.Price.Value.ToString("F8", CultureInfo.InvariantCulture),
            Status = _localization.GetString($"Positions.Status{order.Status}"),

            // The stored stamp is UTC; display converts to the local wall
            // clock, the convention shared with the order history window
            // (bug BUG-055: the panel used to print the raw UTC field).
            Opened = order.CreatedAt.ToLocalTime().ToString(TimeFormats.OrderTimestamp, CultureInfo.InvariantCulture),
            IsCancellable = isCancellable,
            IsAmendable = isAmendable,
            IsAmending = _amendingOrderId == order.Id,
            Fee = FormatFee(order.Commission),
            CancelCommand = ReactiveCommand.CreateFromTask(
                () => HandleCancelAsync(order),
                Observable.Return(isCancellable)),
            AmendCommand = ReactiveCommand.CreateFromTask(
                () => HandleAmendAsync(order),
                Observable.Return(isAmendable)),
        };
    }

    /// <summary>
    /// The commission the order's execution carried, invariant-formatted
    /// like the other money cells; an order without recorded execution fee
    /// data shows the localized dash — an honest unknown, never a
    /// fabricated zero (a working order has not executed yet: same dash).
    /// </summary>
    private string FormatFee(decimal? commission) =>
        commission is { } fee
            ? fee.ToString(FeeDisplayFormat, CultureInfo.InvariantCulture)
            : _localization.GetString(FeeUnknownKey);

    #endregion Private Methods
}

