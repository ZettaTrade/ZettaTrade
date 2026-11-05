namespace ZettaTrade.App.ViewModels;

using System.Globalization;
using System.Reactive.Disposables;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ReactiveUI.Primitives;
using ZettaTrade.App.Services;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.Infrastructure.Persistence;

/// <summary>
/// New Order dialog view model. Collects the order inputs, and routes the
/// request through the mandatory pipeline: <c>OrderRequest.Validate</c> →
/// <see cref="IRiskManager"/> (rejection stops here and never opens the
/// confirmation dialog) → <see cref="IConfirmationService"/> →
/// <see cref="ITradingAdapter.PlaceOrderAsync"/>. Window plumbing (close on
/// success or on Cancel) stays in the View via <see cref="CloseRequested"/>.
/// The header names the active exchange: the behavioral facade stream
/// bootstraps <see cref="ActiveExchangeName"/> on subscribe and keeps it
/// current through toolbar switches while the dialog is open. The view
/// model is transient (one instance per window) and implements
/// <see cref="IDisposable"/>: the owning window releases the facade
/// subscription on closure.
/// </summary>
public sealed class NewOrderDialogViewModel : ReactiveObject, IDisposable
{
    #region Resx Keys

    private const string SymbolMissingKey = "NewOrder.SymbolMissing";
    private const string SymbolInvalidKey = "NewOrder.SymbolInvalid";
    private const string QuantityRequiredKey = "NewOrder.QuantityRequired";
    private const string QuantityNotANumberKey = "NewOrder.QuantityNotANumber";
    private const string PriceRequiredKey = "NewOrder.PriceRequired";
    private const string PriceNotANumberKey = "NewOrder.PriceNotANumber";
    private const string PlaceFailedKey = "NewOrder.PlaceFailed";
    private const string OrderRejectedKey = "NewOrder.OrderRejected";
    private const string OrderPlacedKey = "NewOrder.OrderPlaced";

    #endregion Resx Keys

    #region Constants

    /// <summary>Fraction format of the prefilled price text: the same
    /// money-like shape the book widget prints (two forced digits, trailing
    /// zeros beyond that trimmed) so a clicked level reads back exactly.</summary>
    private const string PrefillPriceFormat = "0.00########";

    #endregion Constants

    #region Private Fields

    private readonly ITradingAdapter _trading;
    private readonly SymbolListProvider _symbolList;
    private readonly ActiveExchangeService _activeExchanges;
    private readonly IRiskManager _riskManager;
    private readonly IConfirmationService _confirmation;
    private readonly OrderConfirmationComposer _composer;
    private readonly IOrderJournal _orderJournal;
    private readonly IOrderHistoryStore _orderHistory;
    private readonly ILocalizationManager _localization;
    private readonly ILogger<NewOrderDialogViewModel> _logger;

    /// <summary>Serializes the placement pipeline; re-entrancy is an error state.</summary>
    private bool _placing;

    /// <summary>
    /// The symbol text a click's prefill deposited, held while the field is
    /// UNEDITED: until the user touches the symbol, suggestions stay empty
    /// (the clicked level already names a full symbol — an open dropdown
    /// would only echo it and cover the layout). The first edit that changes
    /// the text releases the hold and suggestions resume; the invariant
    /// <c>_prefilledSymbol is not null ⟹ SymbolInput == _prefilledSymbol</c>
    /// is guarded by the setter.
    /// </summary>
    private string? _prefilledSymbol;

    /// <summary>
    /// Holds the facade subscription: the view model is transient, so the
    /// owning window disposes it on closure (see <see cref="Dispose"/>).
    /// </summary>
    private readonly CompositeDisposable _subscriptions = new();

    #endregion Private Fields

    #region Properties

    private string _symbolInput = string.Empty;
    private string _quantityInput = string.Empty;
    private string _priceInput = string.Empty;
    private int _selectedSideIndex;
    private int _selectedTypeIndex;
    private string? _errorMessage;
    private string? _resultMessage;
    private string _activeExchangeName = string.Empty;

    private IReadOnlyList<string> _allSymbols = [];
    private IReadOnlyList<string> _symbolSuggestions = [];
    private bool _isSymbolSuggestionsOpen;

    /// <summary>
    /// Symbol text with autocomplete suggestions derived from the adapter
    /// symbol list on every change (free-form text preserved: typed input is
    /// never forced into a list item, so the dialog works when the symbol
    /// list is unavailable).
    /// </summary>
    public string SymbolInput
    {
        get => _symbolInput;
        set
        {
            if (_symbolInput == value)
            {
                return;
            }

            if (_prefilledSymbol is not null && value != _prefilledSymbol)
            {
                // A text edit is the user's own intent: relieve the prefill
                // hold so typed suggestions resume.
                _prefilledSymbol = null;
            }

            _symbolInput = value;
            this.RaisePropertyChanged(nameof(SymbolInput));
            RefreshSymbolSuggestions();
        }
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
    /// Display name of the currently active exchange (the dialog header's
    /// dynamic half). The behavioral facade stream bootstraps it on
    /// subscribe — the dialog opens against the exchange already picked in
    /// the toolbar — and updates it on every switch while the dialog stays
    /// open, so the header can never silently fall behind the toolbar.
    /// </summary>
    public string ActiveExchangeName
    {
        get => _activeExchangeName;
        private set => this.RaiseAndSetIfChanged(ref _activeExchangeName, value);
    }

    /// <summary>
    /// Case-insensitive <c>contains</c> matches for <see cref="SymbolInput"/>
    /// over the loaded symbol list, prefix matches first (see
    /// <c>SuggestSymbols</c>). Empty while nothing matches or nothing loads.
    /// </summary>
    public IReadOnlyList<string> SymbolSuggestions
    {
        get => _symbolSuggestions;
        private set => this.RaiseAndSetIfChanged(ref _symbolSuggestions, value);
    }

    /// <summary>
    /// Whether the symbol suggestion dropdown should be visible. The
    /// AutoCompleteBox refreshes the popup only on a text change, so when
    /// the symbol list arrives AFTER the user already typed (slow network),
    /// the load handler opens the dropdown itself — mid-typing suggestions
    /// appear without forcing a retype. A delivered prefill is NOT a
    /// typist's intent: while the field holds its untouched click payload
    /// (see <see cref="_prefilledSymbol"/>), suggestions stay empty and this
    /// gate cannot open the dropdown.
    /// </summary>
    public bool IsSymbolSuggestionsOpen
    {
        get => _isSymbolSuggestionsOpen;
        private set => this.RaiseAndSetIfChanged(ref _isSymbolSuggestionsOpen, value);
    }

    /// <summary>
    /// Raised when the dialog must close (order placed or user cancelled);
    /// the View owns the actual window close.
    /// </summary>
    public Interaction<RxVoid, RxVoid> CloseRequested { get; }

    /// <summary>
    /// Loads the adapter symbol list once per dialog open for the
    /// autocomplete suggestions; a failure silently keeps the empty
    /// suggestion state (see <see cref="HandleLoadSymbolsAsync"/>).
    /// </summary>
    public ReactiveCommand<RxVoid, RxVoid> LoadSymbolsCommand { get; }

    /// <summary>Runs the full validation → confirmation → placement pipeline.</summary>
    public ReactiveCommand<RxVoid, RxVoid> PlaceOrderCommand { get; }

    #endregion Properties

    #region Constructor

    /// <summary>
    /// Create a new order dialog view model.
    /// </summary>
    /// <param name="trading">The trading adapter the placement pipeline rides.</param>
    /// <param name="symbolList">The application-lifetime symbol list provider for the autocomplete.</param>
    /// <param name="activeExchanges">
    /// The active-exchange facade: its behavioral stream names the exchange
    /// the dialog's price context rides (the header property).
    /// </param>
    public NewOrderDialogViewModel(
        ITradingAdapter trading,
        SymbolListProvider symbolList,
        ActiveExchangeService activeExchanges,
        IRiskManager riskManager,
        IConfirmationService confirmation,
        OrderConfirmationComposer composer,
        IOrderJournal orderJournal,
        IOrderHistoryStore orderHistory,
        ILocalizationManager localization,
        ILogger<NewOrderDialogViewModel> logger)
    {
        _trading = trading ?? throw new ArgumentNullException(nameof(trading));
        _symbolList = symbolList ?? throw new ArgumentNullException(nameof(symbolList));
        _activeExchanges = activeExchanges ?? throw new ArgumentNullException(nameof(activeExchanges));
        _riskManager = riskManager ?? throw new ArgumentNullException(nameof(riskManager));
        _confirmation = confirmation ?? throw new ArgumentNullException(nameof(confirmation));
        _composer = composer ?? throw new ArgumentNullException(nameof(composer));
        _orderJournal = orderJournal ?? throw new ArgumentNullException(nameof(orderJournal));
        _orderHistory = orderHistory ?? throw new ArgumentNullException(nameof(orderHistory));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        CloseRequested = new Interaction<RxVoid, RxVoid>();
        LoadSymbolsCommand = ReactiveCommand.CreateFromTask(HandleLoadSymbolsAsync);
        PlaceOrderCommand = ReactiveCommand.CreateFromTask(HandlePlaceOrderAsync);

        // The header mirrors the active exchange on the same behavioral
        // stream every other consumer uses: emission zero provides the
        // bootstrap value (the stream emits its current adapter on
        // subscribe), later emissions track toolbar switches. Switches fire
        // on the UI thread; the restore emission precedes any dialog
        // lifetime. The subscription parks in the composite disposable —
        // the transient VM must not outlive its window.
        _subscriptions.Add(
            _activeExchanges
                .AdapterChanges
                .Subscribe(
                    adapter => ActiveExchangeName = adapter.Name,
                    ex => _logger.LogError(ex, "The dialog exchange header stream stopped unexpectedly")));
    }

    #endregion Constructor

    #region Public Methods

    /// <summary>
    /// Apply a deliberate prefill from the trading surface (a clicked order
    /// book level today): the clicked symbol, the clicked side (bids buy,
    /// asks sell) and an explicit LIMIT type carrying the clicked price;
    /// the quantity stays EMPTY — the prefill never assumes a size — and
    /// every field remains fully editable. The suggestion dropdown never
    /// opens for the untouched prefill (the click already names the full
    /// symbol); the first edit of the field restores typed suggestions.
    /// Called before the dialog shows, on the UI thread.
    /// </summary>
    public void Prefill(OrderPrefill prefill)
    {
        if (prefill is null)
        {
            throw new ArgumentNullException(nameof(prefill));
        }

        // Park the prefill hold BEFORE the symbol assignment: the setter's
        // suggestion refresh reads it and returns the empty list, so the
        // dropdown cannot echo the clicked symbol over the layout.
        _prefilledSymbol = prefill.Symbol.Normalized;
        SymbolInput = prefill.Symbol.Normalized;
        SelectedSideIndex = (int)prefill.Side;
        SelectedTypeIndex = (int)OrderType.Limit;
        PriceInput = prefill.Price.ToString(PrefillPriceFormat, CultureInfo.InvariantCulture);
    }

    #endregion Public Methods

    #region IDisposable

    /// <summary>
    /// Release the facade subscription. The owning window calls this on
    /// closure — idempotent, the composite guards repeated calls.
    /// </summary>
    public void Dispose() => _subscriptions.Dispose();

    #endregion IDisposable

    #region Private Methods

    /// <summary>
    /// Load the tradable symbol list from the shared application-lifetime
    /// provider (warm at startup, cached across dialog opens — the
    /// suggestions appear instantly except the very first cold open). The
    /// fetch lives exactly as long as the execution it serves:
    /// <see cref="LoadSymbolsCommand"/> passes its execution token to
    /// <see cref="HandleLoadSymbolsAsync"/>, so the window disposal cancels
    /// an in-flight download instead of leaving it abandoned. A failure
    /// (typically offline) is a convenience downgrade, never a dialog
    /// error: the free-form symbol field keeps working with the empty
    /// suggestion state.
    /// </summary>
    private async Task HandleLoadSymbolsAsync(CancellationToken ct)
    {
        try
        {
            // The provider answers with ready-to-display names ("BASE/QUOTE").
            var symbols = await _symbolList.GetSymbolsAsync(ct);
            _allSymbols = symbols is string[] cached ? cached : symbols.ToArray();
            _logger.LogInformation(
                "Order dialog: {SymbolCount} tradable symbols loaded for autocomplete",
                _allSymbols.Count);
        }
        catch (OperationCanceledException)
        {
            // The dialog closed while the fetch was in flight (the window
            // disposal cancels this command's execution token): nothing
            // user-visible remains, and a cancelled convenience fetch is not
            // a service failure — no warning noise in the log.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Order dialog: symbol list unavailable, autocomplete stays empty");
        }

        RefreshSymbolSuggestions();

        // Late-load UX: the AutoCompleteBox refreshed the open popup only
        // on a text change, so typing during the symbol-list fetch left the
        // user with a stale empty dropdown. The load arrival opens a
        // populated dropdown for any text already present; a fresh dropdown
        // stays closed for untouched or no-match text.
        IsSymbolSuggestionsOpen = SymbolSuggestions.Count > 0 && SymbolInput.Trim().Length > 0;
    }

    /// <summary>
    /// Re-derive <see cref="SymbolSuggestions"/> from
    /// <see cref="SymbolInput"/> against the loaded symbol list.
    /// </summary>
    private void RefreshSymbolSuggestions()
    {
        // An untouched prefill contributes no suggestions: the click already
        // named the full symbol, and an echoing dropdown would just cover
        // the layout. The first user edit of the field releases the hold
        // (see the <see cref="_prefilledSymbol"/> invariant) and typed text
        // resumes the suggestion pipeline.
        SymbolSuggestions = _prefilledSymbol is null
            ? SuggestSymbols(_allSymbols, SymbolInput)
            : [];
    }

    /// <summary>
    /// Case-insensitive <c>contains</c> matches for
    /// <paramref name="input"/> over <paramref name="symbols"/>, with
    /// prefix matches ordered ahead of strictly-contains matches (
    /// likeliest typist intent) and the original adapter order kept inside
    /// every bucket. Whitespace-only input yields no suggestions.
    /// </summary>
    private static IReadOnlyList<string> SuggestSymbols(IReadOnlyList<string> symbols, string input)
    {
        var text = input.Trim();
        if (text.Length == 0)
        {
            return [];
        }

        var prefixAndContains = new List<string>();
        var contains = new List<string>();
        foreach (var symbol in symbols)
        {
            var position = symbol.IndexOf(text, StringComparison.OrdinalIgnoreCase);
            if (position < 0)
            {
                continue;
            }

            if (position == 0)
            {
                prefixAndContains.Add(symbol);
            }
            else
            {
                contains.Add(symbol);
            }
        }

        prefixAndContains.AddRange(contains);
        return prefixAndContains;
    }

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

            // Persist the placed order into the SQLite journal. Best effort:
            // the adapter accepted the order already, so a persistence fault
            // must never turn a successful placement into a dialog error.
            try
            {
                await _orderHistory.RecordPlacedAsync(order);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Order dialog: failed to persist {OrderId} to the order history",
                    order.Id);
            }

            ResultMessage = _localization.GetString(OrderPlacedKey, order.Id);
            _logger.LogInformation("Order dialog: placed {OrderId} ({Status})", order.Id, order.Status);

            await CloseRequested.Handle(RxVoid.Default);
        }
        catch (Exception ex)
        {
            // Broader than the adapter's validation refusals: the placement
            // reaches the paper adapter over the same surface its reference
            // rate request rides, so a failed placement can surface as a
            // network exception (HttpRequestException, TaskCanceledException)
            // as easily as a validation fault. Every failure lands as the
            // localized error line instead of escaping into the unobserved
            // ReactiveCommand.ThrownExceptions — an order silently not
            // placed, no message, no journal entry (BUG-017).
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

        // Empty (or whitespace) means MISSING, not unparsable: "not a
        // number" with a blank argument would dangle. The display argument
        // is trimmed so the message never shows padding.
        var rawQuantity = QuantityInput.Trim();
        if (rawQuantity.Length == 0)
        {
            error = _localization.GetString(QuantityRequiredKey);
            return false;
        }

        if (!decimal.TryParse(rawQuantity, NumberStyles.Number, CultureInfo.InvariantCulture, out var quantity)
            || quantity <= 0)
        {
            error = _localization.GetString(QuantityNotANumberKey, rawQuantity);
            return false;
        }

        var type = (OrderType)SelectedTypeIndex;
        Price? limitPrice = null;
        if (type == OrderType.Limit)
        {
            var rawPrice = PriceInput.Trim();
            if (rawPrice.Length == 0)
            {
                error = _localization.GetString(PriceRequiredKey);
                return false;
            }

            if (!decimal.TryParse(rawPrice, NumberStyles.Number, CultureInfo.InvariantCulture, out var price)
                || price <= 0)
            {
                error = _localization.GetString(PriceNotANumberKey, rawPrice);
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
