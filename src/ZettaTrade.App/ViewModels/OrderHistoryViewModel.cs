namespace ZettaTrade.App.ViewModels;

using System.Globalization;
using System.Linq;
using Livery.Localization;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ReactiveUI.Primitives;
using ZettaTrade.App.Services;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Infrastructure.Persistence;

/// <summary>
/// Order history window view model (modal). Loads the persisted journal
/// from <see cref="IOrderHistoryStore"/> once per activation; rows carry
/// precomputed localized strings. The view model is transient: every open
/// reads the database state as it is right now.
/// </summary>
public sealed class OrderHistoryViewModel : ReactiveObject
{
    #region Resx Keys

    private const string LoadFailedKey = "History.LoadFailed";

    #endregion Resx Keys

    #region Private Fields

    /// <summary>Rows shown per load; the store clamps and orders newest first.</summary>
    private const int DefaultLoadLimit = 200;

    private readonly IOrderHistoryStore _store;
    private readonly ILocalizationManager _localization;
    private readonly ILogger<OrderHistoryViewModel> _logger;

    #endregion Private Fields

    #region Properties

    private IReadOnlyList<OrderHistoryRowViewModel> _rows = [];
    private string? _errorMessage;

    /// <summary>Loaded journal rows, newest first.</summary>
    public IReadOnlyList<OrderHistoryRowViewModel> Rows
    {
        get => _rows;
        private set
        {
            if (Rows == value)
            {
                return;
            }

            _rows = value;
            this.RaisePropertyChanged(nameof(Rows));
            this.RaisePropertyChanged(nameof(HasRows));
        }
    }

    /// <summary>True while <see cref="Rows"/> is non-empty; drives the empty state.</summary>
    public bool HasRows => _rows.Count > 0;

    /// <summary>Last load error, for the error line in the window footer.</summary>
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

    /// <summary>Reloads the persisted rows from the database.</summary>
    public ReactiveCommand<RxVoid, RxVoid> LoadCommand { get; }

    #endregion Properties

    #region Constructor

    /// <summary>
    /// Create an order history view model.
    /// </summary>
    public OrderHistoryViewModel(
        IOrderHistoryStore store,
        ILocalizationManager localization,
        ILogger<OrderHistoryViewModel> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        LoadCommand = ReactiveCommand.CreateFromTask(HandleLoadAsync);
    }

    #endregion Constructor

    #region Private Methods

    private async Task HandleLoadAsync()
    {
        // A fresh load replaces whatever a previous load reported.
        ErrorMessage = null;

        try
        {
            var orders = await _store.GetRecentAsync(DefaultLoadLimit);
            Rows = orders.Select(CreateRow).ToArray();
            _logger.LogInformation(
                "Order history window: loaded {RowCount} orders from the journal",
                Rows.Count);
        }
        catch (Exception ex)
        {
            // Broader than the trade flows: a local file/database fault can
            // surface as anything (IO, SQLite, EF); the load fails as a whole
            // and the window stays usable with its error line.
            _logger.LogError(ex, "Order history window: load failed");
            ErrorMessage = _localization.GetString(LoadFailedKey, ex.Message);
        }
    }

    /// <summary>Build the immutable display row for one persisted order.</summary>
    private OrderHistoryRowViewModel CreateRow(Order order) => new()
    {
        // The journal stores UTC; display follows the local wall clock, like
        // the positions panel does.
        Opened = order.CreatedAt.ToLocalTime().ToString(TimeFormats.OrderTimestamp, CultureInfo.InvariantCulture),
        Exchange = order.ExchangeId,
        Symbol = order.Symbol.Normalized,
        Side = _localization.GetString($"Order.{order.Side}"),
        Type = _localization.GetString($"Order.{order.Type}"),
        Quantity = order.Quantity.Value.ToString(CultureInfo.InvariantCulture),
        Price = order.Price.Value.ToString("F8", CultureInfo.InvariantCulture),
        Status = _localization.GetString($"Positions.Status{order.Status}"),
    };

    #endregion Private Methods
}

