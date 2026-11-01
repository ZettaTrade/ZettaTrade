namespace ZettaTrade.App.ViewModels;

using System.Globalization;
using System.Linq;
using System.Reactive.Linq;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ReactiveUI.Primitives;
using ZettaTrade.App.Services;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// Positions and orders panel view model (modal window). Loads positions,
/// open orders and the session journal from the current trading adapter on
/// demand, and routes cancel requests through the mandatory pipeline:
/// <see cref="IConfirmationService"/> → <see cref="ITradingAdapter.CancelOrderAsync"/>
/// where the adapter always receives the order's own symbol and id (the
/// Symbol-first contract). Window plumbing stays in the View.
/// </summary>
public sealed class PositionsPanelViewModel : ReactiveObject
{
    #region Resx Keys

    private const string LoadFailedKey = "Positions.LoadFailed";
    private const string CancelledKey = "Positions.Cancelled";
    private const string CancelFailedKey = "Positions.CancelFailed";

    /// <summary>Shared display format for order/position timestamps.</summary>
    private const string OpenedFormat = "yyyy-MM-dd HH:mm";

    #endregion Resx Keys

    #region Private Fields

    private readonly ITradingAdapter _trading;
    private readonly IOrderJournal _journal;
    private readonly IConfirmationService _confirmation;
    private readonly OrderConfirmationComposer _composer;
    private readonly ILocalizationManager _localization;
    private readonly ILogger<PositionsPanelViewModel> _logger;

    /// <summary>Serializes the cancel pipeline; re-entrancy is an error state.</summary>
    private bool _cancelling;

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

    #endregion Properties

    #region Constructor

    /// <summary>
    /// Create a positions panel view model. The view model is transient: a
    /// fresh instance reloads the current state on every panel open.
    /// </summary>
    public PositionsPanelViewModel(
        ITradingAdapter trading,
        IOrderJournal journal,
        IConfirmationService confirmation,
        OrderConfirmationComposer composer,
        ILocalizationManager localization,
        ILogger<PositionsPanelViewModel> logger)
    {
        _trading = trading ?? throw new ArgumentNullException(nameof(trading));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _confirmation = confirmation ?? throw new ArgumentNullException(nameof(confirmation));
        _composer = composer ?? throw new ArgumentNullException(nameof(composer));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        LoadCommand = ReactiveCommand.CreateFromTask(HandleLoadAsync);
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
            ResultMessage = _localization.GetString(CancelledKey, order.Id);
            _logger.LogInformation(
                "Positions panel: cancelled {OrderId} on {Symbol}",
                order.Id,
                order.Symbol.Normalized);

            // Refresh the sections so the cancelled order leaves the lists.
            await ReloadSectionsAsync();
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "Positions panel: cancellation failed");
            ErrorMessage = _localization.GetString(CancelFailedKey, ex.Message);
        }
        finally
        {
            _cancelling = false;
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
        catch (InvalidOperationException ex)
        {
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
        Opened = position.OpenedAt.ToString(OpenedFormat, CultureInfo.InvariantCulture),
    };

    /// <summary>
    /// Build the immutable display row for one order. The per-row cancel
    /// command closes over the full order (adapter symbol + id) and is
    /// enabled exactly when the status is still cancellable upstream.
    /// </summary>
    private OrderRowViewModel CreateOrderRow(Order order)
    {
        var isCancellable = order.Status
            is OrderStatus.Pending
            or OrderStatus.Submitted
            or OrderStatus.PartiallyFilled;

        return new OrderRowViewModel
        {
            OrderId = order.Id,
            Symbol = order.Symbol.Normalized,
            Side = _localization.GetString($"Order.{order.Side}"),
            Quantity = order.Quantity.Value.ToString(CultureInfo.InvariantCulture),
            Price = order.Price.Value.ToString("F8", CultureInfo.InvariantCulture),
            Status = _localization.GetString($"Positions.Status{order.Status}"),
            Opened = order.CreatedAt.ToString(OpenedFormat, CultureInfo.InvariantCulture),
            IsCancellable = isCancellable,
            CancelCommand = ReactiveCommand.CreateFromTask(
                () => HandleCancelAsync(order),
                Observable.Return(isCancellable)),
        };
    }

    #endregion Private Methods
}
