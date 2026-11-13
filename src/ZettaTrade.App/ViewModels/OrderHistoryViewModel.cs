namespace ZettaTrade.App.ViewModels;

using System.Globalization;
using System.Linq;
using Livery.Localization;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ReactiveUI.Primitives;
using ZettaTrade.App.Services;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Infrastructure.Export;
using ZettaTrade.Infrastructure.Persistence;

/// <summary>
/// Order history window view model (modal). Loads the persisted journal
/// from <see cref="IOrderHistoryStore"/> once per activation; rows carry
/// precomputed localized strings. The view model is transient: every open
/// reads the database state as it is right now.
/// <para>
/// The export actions ride the same loaded set: the orders CSV mirrors the
/// journal fields, the fills CSV extracts the executions (via
/// <see cref="ICsvExportService"/>), and the destination comes through the
/// save-file interactions the window answers. Write faults surface on the
/// shared error line.
/// </para>
/// <para>
/// The user trade annotations (T-211): the load joins each row's note and
/// tags through the store's bulk meta read (only annotated rows come back),
/// and a per-row edit affordance writes the applied dialog result through
/// <see cref="IOrderHistoryStore.SetTradeMetaAsync"/> — the fresh row
/// instance replaces the edited one in place.
/// </para>
/// </summary>
public sealed class OrderHistoryViewModel : ReactiveObject
{
    #region Resx Keys

    private const string LoadFailedKey = "History.LoadFailed";
    private const string ExportFailedKey = "History.ExportFailed";
    private const string EditFailedKey = "History.EditFailedFormat";
    private const string FeeUnknownKey = "Fees.Unknown";
    private const string PostOnlyFlagKey = "Order.FlagPostOnly";
    private const string ReduceOnlyFlagKey = "Order.FlagReduceOnly";
    private const string FeeDisplayFormat = "F8";
    private const string FlagsSeparator = ", ";

    #endregion Resx Keys

    #region Private Fields

    /// <summary>Rows shown per load; the store clamps and orders newest first.</summary>
    private const int DefaultLoadLimit = 200;

    private readonly IOrderHistoryStore _store;
    private readonly ICsvExportService _export;
    private readonly ILocalizationManager _localization;
    private readonly ILogger<OrderHistoryViewModel> _logger;

    /// <summary>The raw journal rows behind the loaded display rows: the export set.</summary>
    private IReadOnlyList<Order> _loadedOrders = [];

    /// <summary>Whether the traded surface currently rides the TESTNET lane (T-127; set by the owning window).</summary>
    private bool _isTestnetLane;

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

    /// <summary>
    /// Whether the workspace's traded surface currently rides the TESTNET
    /// lane (T-127): the history's badge mirrors the status-bar chip so the
    /// operator sees the venue surface the journal rows persist. Set by the
    /// owning window from the trading-mode lane stream — the view model
    /// holds no lane service of its own.
    /// </summary>
    public bool IsTestnetLane
    {
        get => _isTestnetLane;
        set => this.RaiseAndSetIfChanged(ref _isTestnetLane, value);
    }

    /// <summary>Reloads the persisted rows from the database.</summary>
    public ReactiveCommand<RxVoid, RxVoid> LoadCommand { get; }

    /// <summary>Exports the loaded journal rows as an orders CSV file.</summary>
    public ReactiveCommand<RxVoid, RxVoid> ExportOrdersCommand { get; }

    /// <summary>Exports the executions found in the loaded rows as a fills CSV file.</summary>
    public ReactiveCommand<RxVoid, RxVoid> ExportFillsCommand { get; }

    /// <summary>Opens the note-and-tags dialog for one history row (T-211); the applied result lands in the journal.</summary>
    public ReactiveCommand<OrderHistoryRowViewModel, RxVoid> EditTradeCommand { get; }

    /// <summary>Asks the view for the orders CSV destination (a save-file dialog); null cancels.</summary>
    public Interaction<string, string?> SaveOrdersCsvRequested { get; }

    /// <summary>Asks the view for the fills CSV destination (a save-file dialog); null cancels.</summary>
    public Interaction<string, string?> SaveFillsCsvRequested { get; }

    /// <summary>
    /// Asks the view to show the note-and-tags dialog over one history row
    /// (T-211); the applied result comes back, null closes without one.
    /// </summary>
    public Interaction<OrderHistoryRowViewModel, TradeNoteEditResult?> EditTradeRequested { get; }

    #endregion Properties

    #region Constructor

    /// <summary>
    /// Create an order history view model.
    /// </summary>
    public OrderHistoryViewModel(
        IOrderHistoryStore store,
        ICsvExportService export,
        ILocalizationManager localization,
        ILogger<OrderHistoryViewModel> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _export = export ?? throw new ArgumentNullException(nameof(export));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        LoadCommand = ReactiveCommand.CreateFromTask(HandleLoadAsync);
        ExportOrdersCommand = ReactiveCommand.CreateFromTask(HandleExportOrdersAsync);
        ExportFillsCommand = ReactiveCommand.CreateFromTask(HandleExportFillsAsync);
        EditTradeCommand = ReactiveCommand.CreateFromTask<OrderHistoryRowViewModel, RxVoid>(
            (row, ct) => HandleEditTradeAsync(row, ct));
        SaveOrdersCsvRequested = new Interaction<string, string?>();
        SaveFillsCsvRequested = new Interaction<string, string?>();
        EditTradeRequested = new Interaction<OrderHistoryRowViewModel, TradeNoteEditResult?>();
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
            _loadedOrders = orders;

            // The annotation join (T-211): one bulk meta read over the
            // loaded rows; only annotated rows come back, the rest stays
            // honestly null on the display rows.
            var meta = await _store.GetTradeMetaAsync(
                orders.Select(order => (order.ExchangeId, order.Id)).ToArray());
            var metaByKey = meta.ToDictionary(
                entry => (entry.ExchangeId, entry.OrderId),
                entry => entry);
            Rows = orders
                .Select(order => metaByKey.TryGetValue(
                    (order.ExchangeId, order.Id),
                    out var entry)
                    ? CreateRow(order, entry.Note, entry.Tags)
                    : CreateRow(order, null, null))
                .ToArray();

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

    /// <summary>
    /// One annotation edit (T-211): the view answers the dialog interaction
    /// (null closes without an applied edit), the applied result rides the
    /// journal write, and the edited display row is replaced with the fresh
    /// annotations. A refused write (the journal row vanished mid-session)
    /// surfaces the honest failure line, never a fabricated save.
    /// </summary>
    private async Task<RxVoid> HandleEditTradeAsync(
        OrderHistoryRowViewModel row,
        CancellationToken ct)
    {
        ErrorMessage = null;

        try
        {
            var result = await EditTradeRequested.Handle(row);
            if (result is null)
            {
                _logger.LogInformation(
                    "Order history window: the trade note edit ended on a cancelled dialog");
                return RxVoid.Default;
            }

            var saved = await _store.SetTradeMetaAsync(
                row.Exchange,
                row.OrderId,
                result.Note,
                result.Tags,
                ct);
            if (!saved)
            {
                _logger.LogWarning(
                    "Order history window: the trade meta write for {ExchangeId}/{OrderId} found no recorded order",
                    row.Exchange,
                    row.OrderId);
                ErrorMessage = _localization.GetString(EditFailedKey, "no recorded order");
                return RxVoid.Default;
            }

            var fresh = _rows.ToList();
            var index = fresh.IndexOf(row);
            if (index >= 0)
            {
                fresh[index] = row with { Note = result.Note, Tags = result.Tags };
                Rows = fresh;
            }

            _logger.LogInformation(
                "Order history window: trade meta saved for {ExchangeId}/{OrderId} (note {HasNote}, tags {Tags})",
                row.Exchange,
                row.OrderId,
                result.Note is not null,
                result.Tags ?? string.Empty);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Broader than the annotation flow: the journal write rides the
            // local disk; the window stays usable with its error line.
            _logger.LogError(ex, "Order history window: the trade note edit failed");
            ErrorMessage = _localization.GetString(EditFailedKey, ex.Message);
        }

        return RxVoid.Default;
    }

    private async Task HandleExportOrdersAsync(CancellationToken cancellationToken)
    {
        // The export set is exactly the displayed set: the window shows the
        // journal rows it loaded, the CSV carries the same rows' raw fields.
        await ExportAsync(
            SaveOrdersCsvRequested,
            suggestedFileName: "orders.csv",
            exportAsync: (path, ct) => _export.ExportOrdersAsync(path, _loadedOrders, ct),
            cancellationToken);
    }

    private async Task HandleExportFillsAsync(CancellationToken cancellationToken)
    {
        await ExportAsync(
            SaveFillsCsvRequested,
            suggestedFileName: "fills.csv",
            exportAsync: (path, ct) => _export.ExportFillsAsync(path, _loadedOrders, ct),
            cancellationToken);
    }

    /// <summary>
    /// One export flow: ask the view for a destination, export without one
    /// (the user cancelled the dialog — not an error), and surface any fault
    /// on the window's error line. A previous error line clears first: the
    /// flow's outcome is the only message on display.
    /// </summary>
    private async Task ExportAsync(
        Interaction<string, string?> destinationRequest,
        string suggestedFileName,
        Func<string, CancellationToken, Task> exportAsync,
        CancellationToken cancellationToken)
    {
        ErrorMessage = null;

        try
        {
            // The dialog pick has no cancellation of its own: the user either
            // confirms a path or cancels the dialog (a null destination).
            var path = await destinationRequest.Handle(suggestedFileName);
            if (path is null)
            {
                _logger.LogInformation(
                    "Order history window: {Export} export ended on a cancelled save dialog",
                    suggestedFileName);
                return;
            }

            await exportAsync(path, cancellationToken);
            _logger.LogInformation(
                "Order history window: exported {FileName} to {Path}",
                suggestedFileName,
                path);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Broader than the trade flows: the write can fail on a missing
            // directory, a locked file or any local IO fault; the window
            // stays usable with its error line and the data stays intact.
            _logger.LogError(ex, "Order history window: export failed");
            ErrorMessage = _localization.GetString(ExportFailedKey, ex.Message);
        }
    }

    /// <summary>Build the immutable display row for one persisted order.</summary>
    private OrderHistoryRowViewModel CreateRow(Order order, string? note, string? tags) => new()
    {
        // The journal stores UTC; display follows the local wall clock, like
        // the positions panel does.
        Opened = order.CreatedAt.ToLocalTime().ToString(TimeFormats.OrderTimestamp, CultureInfo.InvariantCulture),
        Exchange = order.ExchangeId,
        Symbol = order.Symbol.Normalized,
        Side = _localization.GetString($"Order.{order.Side}"),
        IsBuySide = order.Side is OrderSide.Buy,
        Type = _localization.GetString($"Order.{order.Type}"),
        Quantity = order.Quantity.Value.ToString(CultureInfo.InvariantCulture),
        Price = order.Price.Value.ToString("F8", CultureInfo.InvariantCulture),
        Status = _localization.GetString($"Positions.Status{order.Status}"),
        Fee = FormatFee(order.Commission),
        Flags = FormatFlags(order.ReduceOnly, order.PostOnly),
        Note = note,
        Tags = tags,
        OrderId = order.Id,
    };

    /// <summary>
    /// The commission the order's execution carried, invariant-formatted
    /// like the other money cells; an order without recorded execution fee
    /// data shows the localized dash — an honest unknown, never a
    /// fabricated zero.
    /// </summary>
    private string FormatFee(decimal? commission) =>
        commission is { } fee
            ? fee.ToString(FeeDisplayFormat, CultureInfo.InvariantCulture)
            : _localization.GetString(FeeUnknownKey);

    /// <summary>
    /// The order's flags as a localized list (post-only, reduce-only); an
    /// order placed without either contract carries an empty cell — the
    /// honest "no flags", never a fabricated dash.
    /// </summary>
    private string FormatFlags(bool reduceOnly, bool postOnly)
    {
        var flags = new List<string>(2);
        if (postOnly)
        {
            flags.Add(_localization.GetString(PostOnlyFlagKey));
        }

        if (reduceOnly)
        {
            flags.Add(_localization.GetString(ReduceOnlyFlagKey));
        }

        return string.Join(FlagsSeparator, flags);
    }

    #endregion Private Methods
}

