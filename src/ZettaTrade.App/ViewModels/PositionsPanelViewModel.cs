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
/// </summary>
public sealed class PositionsPanelViewModel : ReactiveObject
{
    #region Resx Keys

    private const string LoadFailedKey = "Positions.LoadFailed";
    private const string CancelledKey = "Positions.Cancelled";
    private const string CancelFailedKey = "Positions.CancelFailed";
    private const string AmendFailedKey = "Positions.AmendFailed";

    #endregion Resx Keys

    #region Private Fields

    private readonly ITradingAdapter _trading;
    private readonly IOrderJournal _journal;
    private readonly IOrderHistoryStore _orderHistory;
    private readonly IConfirmationService _confirmation;
    private readonly OrderConfirmationComposer _composer;
    private readonly OrderAmendService _amendService;
    private readonly ILocalizationManager _localization;
    private readonly ILogger<PositionsPanelViewModel> _logger;

    /// <summary>Serializes the cancel pipeline; re-entrancy is an error state.</summary>
    private bool _cancelling;

    /// <summary>Serializes the amend pipeline; never two pipelined amends.</summary>
    private bool _amending;

    /// <summary>Id of the order whose amend is in flight; drives the row's amending state.</summary>
    private string? _amendingOrderId;

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

    /// <summary>Reloads every section from the adapter and the journal.</summary>
    public ReactiveCommand<RxVoid, RxVoid> LoadCommand { get; }

    /// <summary>
    /// The amend dialog handoff: the view model asks for the edits over one
    /// order, the View opens the amend dialog window and answers with the
    /// parsed <see cref="Services.OrderAmendRequest"/> — or null when the
    /// user cancelled the dialog. Window plumbing stays in the View.
    /// </summary>
    public Interaction<Order, OrderAmendRequest?> AmendRequested { get; }

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
        ILocalizationManager localization,
        ILogger<PositionsPanelViewModel> logger)
    {
        _trading = trading ?? throw new ArgumentNullException(nameof(trading));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _orderHistory = orderHistory ?? throw new ArgumentNullException(nameof(orderHistory));
        _confirmation = confirmation ?? throw new ArgumentNullException(nameof(confirmation));
        _composer = composer ?? throw new ArgumentNullException(nameof(composer));
        _amendService = amendService ?? throw new ArgumentNullException(nameof(amendService));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        LoadCommand = ReactiveCommand.CreateFromTask(HandleLoadAsync);
        AmendRequested = new Interaction<Order, OrderAmendRequest?>();
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

    /// <summary>Build the immutable display row for one position.</summary>
    private PositionRowViewModel CreatePositionRow(Position position) => new()
    {
        Symbol = position.Symbol.Normalized,
        Side = _localization.GetString($"Order.{position.Side}"),
        Quantity = position.Quantity.Value.ToString(CultureInfo.InvariantCulture),
        EntryPrice = position.EntryPrice.Value.ToString("F8", CultureInfo.InvariantCulture),

        // The journal stores UTC; the display follows the local wall clock —
        // the one convention the order history window renders too, so the
        // same order shows the same time in both windows (BUG-055).
        Opened = position.OpenedAt.ToLocalTime().ToString(TimeFormats.OrderTimestamp, CultureInfo.InvariantCulture),
    };

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
            CancelCommand = ReactiveCommand.CreateFromTask(
                () => HandleCancelAsync(order),
                Observable.Return(isCancellable)),
            AmendCommand = ReactiveCommand.CreateFromTask(
                () => HandleAmendAsync(order),
                Observable.Return(isAmendable)),
        };
    }

    #endregion Private Methods
}

