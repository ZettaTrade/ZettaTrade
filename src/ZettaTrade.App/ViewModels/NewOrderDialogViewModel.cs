namespace ZettaTrade.App.ViewModels;

using System.Globalization;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ReactiveUI.Primitives;
using ZettaTrade.App.Services;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// New Order dialog view model. Collects the order inputs, and routes the
/// request through the mandatory pipeline: <c>OrderRequest.Validate</c> →
/// <see cref="IRiskManager"/> (rejection stops here and never opens the
/// confirmation dialog) → <see cref="IConfirmationService"/> →
/// <see cref="ITradingAdapter.PlaceOrderAsync"/>. Window plumbing (close on
/// success or on Cancel) stays in the View via <see cref="CloseRequested"/>.
/// </summary>
public sealed class NewOrderDialogViewModel : ReactiveObject
{
    #region Resx Keys

    private const string SymbolMissingKey = "NewOrder.SymbolMissing";
    private const string SymbolInvalidKey = "NewOrder.SymbolInvalid";
    private const string QuantityNotANumberKey = "NewOrder.QuantityNotANumber";
    private const string PriceNotANumberKey = "NewOrder.PriceNotANumber";
    private const string PlaceFailedKey = "NewOrder.PlaceFailed";
    private const string OrderRejectedKey = "NewOrder.OrderRejected";
    private const string OrderPlacedKey = "NewOrder.OrderPlaced";

    #endregion Resx Keys

    #region Private Fields

    private readonly ITradingAdapter _trading;
    private readonly IRiskManager _riskManager;
    private readonly IConfirmationService _confirmation;
    private readonly OrderConfirmationComposer _composer;
    private readonly IOrderJournal _orderJournal;
    private readonly ILocalizationManager _localization;
    private readonly ILogger<NewOrderDialogViewModel> _logger;

    /// <summary>Serializes the placement pipeline; re-entrancy is an error state.</summary>
    private bool _placing;

    #endregion Private Fields

    #region Properties

    private string _symbolInput = string.Empty;
    private string _quantityInput = string.Empty;
    private string _priceInput = string.Empty;
    private int _selectedSideIndex;
    private int _selectedTypeIndex;
    private string? _errorMessage;
    private string? _resultMessage;

    /// <summary>Normalized symbol text (BASE/QUOTE, e.g. "BTC/USDT").</summary>
    public string SymbolInput
    {
        get => _symbolInput;
        set => this.RaiseAndSetIfChanged(ref _symbolInput, value);
    }

    /// <summary>Quantity text, parsed with the invariant culture.</summary>
    public string QuantityInput
    {
        get => _quantityInput;
        set => this.RaiseAndSetIfChanged(ref _quantityInput, value);
    }

    /// <summary>Limit price text, parsed with the invariant culture.</summary>
    public string PriceInput
    {
        get => _priceInput;
        set => this.RaiseAndSetIfChanged(ref _priceInput, value);
    }

    /// <summary>Side combo box index (matches the <see cref="OrderSide"/> values).</summary>
    public int SelectedSideIndex
    {
        get => _selectedSideIndex;
        set => this.RaiseAndSetIfChanged(ref _selectedSideIndex, value);
    }

    /// <summary>
    /// Type combo box index (0 = Market, 1 = Limit — the only T-008 types).
    /// Raises <see cref="IsPriceRequired"/> so the price field follows the type.
    /// </summary>
    public int SelectedTypeIndex
    {
        get => _selectedTypeIndex;
        set
        {
            if (_selectedTypeIndex == value)
            {
                return;
            }

            _selectedTypeIndex = value;
            this.RaisePropertyChanged(nameof(SelectedTypeIndex));
            this.RaisePropertyChanged(nameof(IsPriceRequired));
        }
    }

    /// <summary>True when the selected order type carries an explicit limit price.</summary>
    public bool IsPriceRequired => (OrderType)_selectedTypeIndex == OrderType.Limit;

    /// <summary>Last flow error (parse failure, risk rejection, adapter failure).</summary>
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

    /// <summary>Text of a successfully placed order (before the dialog closes).</summary>
    public string? ResultMessage
    {
        get => _resultMessage;
        private set => this.RaiseAndSetIfChanged(ref _resultMessage, value);
    }

    /// <summary>
    /// Raised when the dialog must close (order placed or user cancelled);
    /// the View owns the actual window close.
    /// </summary>
    public Interaction<RxVoid, RxVoid> CloseRequested { get; }

    /// <summary>Runs the full validation → confirmation → placement pipeline.</summary>
    public ReactiveCommand<RxVoid, RxVoid> PlaceOrderCommand { get; }

    #endregion Properties

    #region Constructor

    /// <summary>
    /// Create a new order dialog view model.
    /// </summary>
    public NewOrderDialogViewModel(
        ITradingAdapter trading,
        IRiskManager riskManager,
        IConfirmationService confirmation,
        OrderConfirmationComposer composer,
        IOrderJournal orderJournal,
        ILocalizationManager localization,
        ILogger<NewOrderDialogViewModel> logger)
    {
        _trading = trading ?? throw new ArgumentNullException(nameof(trading));
        _riskManager = riskManager ?? throw new ArgumentNullException(nameof(riskManager));
        _confirmation = confirmation ?? throw new ArgumentNullException(nameof(confirmation));
        _composer = composer ?? throw new ArgumentNullException(nameof(composer));
        _orderJournal = orderJournal ?? throw new ArgumentNullException(nameof(orderJournal));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        CloseRequested = new Interaction<RxVoid, RxVoid>();
        PlaceOrderCommand = ReactiveCommand.CreateFromTask(HandlePlaceOrderAsync);
    }

    #endregion Constructor

    #region Private Methods

    private async Task HandlePlaceOrderAsync()
    {
        // The command can only fire from one UI place, but re-entrancy (double
        // click) must still be impossible: never two pipelined placements.
        if (_placing)
        {
            return;
        }

        // A fresh attempt replaces whatever the previous attempt reported.
        ErrorMessage = null;
        ResultMessage = null;

        if (!TryBuildRequest(out var request, out var parseError))
        {
            ErrorMessage = parseError;
            return;
        }

        _placing = true;
        try
        {
            request.Validate();

            var risk = await _riskManager.CheckOrderAsync(request);
            if (!risk.IsApproved)
            {
                _logger.LogWarning(
                    "Order dialog: risk manager rejected {Side} {Quantity} {Symbol}: {Reason}",
                    request.Side, request.Quantity.Value, request.Symbol.Normalized, risk.Reason);
                ErrorMessage = _localization.GetString(OrderRejectedKey, risk.Reason ?? string.Empty);
                return;
            }

            var confirmed = await _confirmation.RequestConfirmationAsync(_composer.ForPlace(request));
            if (!confirmed)
            {
                _logger.LogInformation("Order dialog: user cancelled the confirmation; nothing placed");
                return;
            }

            var order = await _trading.PlaceOrderAsync(request);
            _orderJournal.RecordPlaced(order);
            ResultMessage = _localization.GetString(OrderPlacedKey, order.Id);
            _logger.LogInformation("Order dialog: placed {OrderId} ({Status})", order.Id, order.Status);

            await CloseRequested.Handle(RxVoid.Default);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "Order dialog: placement failed");
            ErrorMessage = _localization.GetString(PlaceFailedKey, ex.Message);
        }
        finally
        {
            _placing = false;
        }
    }

    /// <summary>
    /// Parse the dialog inputs into an <see cref="OrderRequest"/>. Every
    /// unparsable value becomes a localized error — never a defaulted number
    /// (the trading-safety rule: no silent substitution).
    /// </summary>
    private bool TryBuildRequest(out OrderRequest request, out string? error)
    {
        request = null!;
        error = null;

        var rawSymbol = SymbolInput.Trim();
        if (rawSymbol.Length == 0)
        {
            error = _localization.GetString(SymbolMissingKey);
            return false;
        }

        Symbol symbol;
        try
        {
            symbol = Symbol.Parse(rawSymbol);
        }
        catch (ArgumentException)
        {
            error = _localization.GetString(SymbolInvalidKey, rawSymbol);
            return false;
        }

        if (!decimal.TryParse(QuantityInput, NumberStyles.Number, CultureInfo.InvariantCulture, out var quantity)
            || quantity <= 0)
        {
            error = _localization.GetString(QuantityNotANumberKey, QuantityInput);
            return false;
        }

        var type = (OrderType)SelectedTypeIndex;
        Price? limitPrice = null;
        if (type == OrderType.Limit)
        {
            if (!decimal.TryParse(PriceInput, NumberStyles.Number, CultureInfo.InvariantCulture, out var price)
                || price <= 0)
            {
                error = _localization.GetString(PriceNotANumberKey, PriceInput);
                return false;
            }

            limitPrice = new Price(price);
        }

        request = new OrderRequest
        {
            Symbol = symbol,
            Side = (OrderSide)SelectedSideIndex,
            Type = type,
            Quantity = new Quantity(quantity),
            Price = limitPrice,
        };
        return true;
    }

    #endregion Private Methods
}
