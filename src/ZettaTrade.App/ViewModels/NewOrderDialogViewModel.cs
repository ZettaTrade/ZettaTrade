namespace ZettaTrade.App.ViewModels;

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Livery.Extensions;
using Livery.Localization;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ZettaTrade.App.Services;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.Infrastructure.Exchange;
using ZettaTrade.Infrastructure.Fees;
using ZettaTrade.Infrastructure.Persistence;
using RxVoid = ReactiveUI.Primitives.RxVoid;

/// <summary>
/// New Order dialog view model. Collects the order inputs (market, limit,
/// stop-limit, trailing stop orders, OCO pairs placed as two either-or
/// legs and brackets placed as one entry carrying its exits, T-186) and
/// routes every request through the mandatory pipeline:
/// <c>OrderRequest.Validate</c> → <see cref="IRiskManager"/> (rejection stops
/// here and never opens the confirmation dialog) →
/// <see cref="IConfirmationService"/> — the pre-submit gate: its content
/// composes ONCE from the submit-time state and its ask-or-skip rides the
/// confirmation policy (T-078), except where the risk engine demands
/// confirmation itself → <see cref="ITradingAdapter"/>. A
/// partially-placed OCO pair is aborted best-effort — the placed leg is
/// cancelled, the pair never goes half-working. The bracket (T-186)
/// checks every leg through the risk manager BEFORE anything is submitted
/// (one rejection refuses the whole bracket atomically) and confirms ONCE
/// naming all three legs. Window plumbing (close on
/// success or on Cancel) stays in the View via <see cref="CloseRequested"/>.
/// The header names the active exchange: the behavioral facade stream
/// bootstraps <see cref="ActiveExchangeName"/> on subscribe and keeps it
/// current through toolbar switches while the dialog is open. The fee
/// estimate line (T-080) re-prices with the ticket inputs and names each
/// leg's expected maker/taker role — an unknown schedule renders the
/// localized dash, never a fabricated zero. A ticket-local quick-size
/// row (<see cref="Sizer"/>, T-079) proposes quantities into the
/// quantity field from captured balance/reference snapshots plus the
/// ticket's own stop context — sizing never bypasses the placement
/// pipeline. The reports route by owner (T-156): a field-tagged parse
/// refusal surfaces next to its field through the per-field error
/// properties, the side reads as the segmented Buy/Sell pair over
/// <see cref="SelectedSideIndex"/>, and the quantity/price captions name
/// the parsed symbol's units. The view
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
    private const string TriggerRequiredKey = "NewOrder.TriggerRequired";
    private const string TriggerNotANumberKey = "NewOrder.TriggerNotANumber";
    private const string TrailRequiredKey = "NewOrder.TrailRequired";
    private const string TrailNotANumberKey = "NewOrder.TrailNotANumber";
    private const string TrailTooLargeKey = "NewOrder.TrailTooLarge";
    private const string SellTriggerBelowMarketKey = "NewOrder.SellTriggerMustBeBelowMarket";
    private const string BuyTriggerAboveMarketKey = "NewOrder.BuyTriggerMustBeAboveMarket";
    private const string SellLimitAboveMarketKey = "NewOrder.SellLimitMustBeAboveMarket";
    private const string BuyLimitBelowMarketKey = "NewOrder.BuyLimitMustBeBelowMarket";
    private const string InvalidParametersKey = "NewOrder.InvalidParameters";
    private const string PlaceFailedKey = "NewOrder.PlaceFailed";
    private const string OrderRejectedKey = "NewOrder.OrderRejected";
    private const string OrderPlacedKey = "NewOrder.OrderPlaced";
    private const string OcoPlacedKey = "NewOrder.OcoPlaced";
    private const string OcoAbortedKey = "NewOrder.OcoAborted";
    private const string BracketPlacedKey = "NewOrder.BracketPlaced";
    private const string UnitHintFormatKey = "NewOrder.UnitHintFormat";
    private const string FeeEstimateMakerKey = "Fees.EstimateMakerFormat";
    private const string FeeEstimateTakerKey = "Fees.EstimateTakerFormat";
    private const string FeeUnknownKey = "Fees.Unknown";
    private const string DailyLossBannerKey = "Trading.DailyLossBannerFormat";
    private const string DailyLossResetKey = "Trading.DailyLossReset";
    private const string DailyLossResetConfirmKey = "Trading.ConfirmDailyLossReset";
    private const string DailyLossResetFailedKey = "NewOrder.DailyLossResetFailed";

    #endregion Resx Keys

    #region Constants

    /// <summary>Fraction format of the prefilled price text: the same
    /// money-like shape the book widget prints (two forced digits, trailing
    /// zeros beyond that trimmed) so a clicked level reads back exactly.</summary>
    private const string PrefillPriceFormat = "0.00########";

    /// <summary>
    /// Combo index of the OCO entry: one-cancels-the-other is a PAIR of
    /// orders (a take-profit limit and a stop-loss stop-limit sharing an
    /// either-or group) rather than an <see cref="OrderType"/>, so the
    /// combo selection for it is a dialog mode that maps onto two
    /// placements, and the direct index-to-enum cast of the pre-T-074
    /// dialog is unsafe from here on (index 4 aliases TrailingStop).
    /// </summary>
    private const int OcoTypeIndex = 4;

    /// <summary>
    /// Combo index of the bracket entry (T-186): an entry (market or limit)
    /// carrying its protective exits — a take-profit limit and a stop-loss
    /// stop-limit — as ONE validated request. Like the OCO slot, the combo
    /// selection is a dialog mode the pair builder assembles, never an
    /// index-to-enum cast.
    /// </summary>
    private const int BracketTypeIndex = 5;

    /// <summary>Prefix of the either-or group id the OCO legs share.</summary>
    private const string OcoGroupIdPrefix = "oco-";

    /// <summary>Decimal places of a displayed fee amount (the schedule's own commission precision).</summary>
    private const string FeeAmountFormat = "F8";

    /// <summary>
    /// Typed-symbol quiet window before the fee estimate's reference-read
    /// fires: typing a symbol must not fetch a bar per keystroke, the next
    /// quiet text does. Short enough to feel live; cancel switches coalesce
    /// the wider scrambles.
    /// </summary>
    private static readonly TimeSpan EstimateReferenceThrottle = TimeSpan.FromMilliseconds(400);

    #endregion Constants

    #region Private Fields

    private readonly ITradingAdapter _trading;
    private readonly SymbolListProvider _symbolList;
    private readonly ActiveExchangeService _activeExchanges;
    private readonly IRiskManager _riskManager;
    private readonly IConfirmationService _confirmation;
    private readonly OrderConfirmationComposer _composer;
    private readonly OrderConfirmPolicy _confirmPolicy;
    private readonly IOrderJournal _orderJournal;
    private readonly IOrderHistoryStore _orderHistory;
    private readonly IFeeScheduleProvider _feeSchedules;
    private readonly ILocalizationManager _localization;
    private readonly PositionSizingService _sizing;
    private readonly ISettingsService _settings;
    private readonly ILogger<NewOrderDialogViewModel> _logger;

    /// <summary>The daily loss governor's state seam (T-189): the banner reads it, the reset writes through it.</summary>
    private readonly IDailyLossLimit _dailyLoss;

    /// <summary>The normalized symbol the banner's last read attributed (null = nothing read yet / nothing attributed).</summary>
    private string? _dailyLossBannerSymbol;

    /// <summary>The banner's visibility backing field.</summary>
    private bool _isDailyLossBlocked;

    /// <summary>The banner's line backing field (empty while not blocked).</summary>
    private string _dailyLossBanner = string.Empty;

    /// <summary>The ticket-local quick-size row (T-079): proposes quantities, places nothing.</summary>
    private readonly OrderSizerRowViewModel _sizer;

    /// <summary>
    /// Combo index → order type for every single-order selection; the OCO
    /// slot (index <see cref="OcoTypeIndex"/>) is decided by the pair
    /// builder instead (it places a take-profit limit and a stop-loss
    /// stop-limit). Never cast the index to <see cref="OrderType"/> directly.
    /// </summary>
    private static readonly OrderType[] TypeValues =
        [OrderType.Market, OrderType.Limit, OrderType.StopLimit, OrderType.TrailingStop, OrderType.Limit];

    /// <summary>Serializes the placement pipeline; re-entrancy is an error state.</summary>
    private readonly SemaphoreSlim _submitGate = new(1, 1);

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

    /// <summary>
    /// The estimate id of the schedule-owning adapter (T-080); empty until
    /// the facade's bootstrap emission lands (an unknown exchange prices
    /// the localized dash).
    /// </summary>
    private string _activeExchangeId = string.Empty;

    /// <summary>
    /// The latest resolved reference close for the price-less types'
    /// notional; null until the throttled read answers (or failed — the
    /// estimate stays unknown either way).
    /// </summary>
    private decimal? _referenceClose;

    #endregion Private Fields

    #region Properties

    private string _symbolInput = string.Empty;
    private string _quantityInput = string.Empty;
    private string _priceInput = string.Empty;
    private string _stopTriggerInput = string.Empty;
    private string _trailDistanceInput = string.Empty;
    private string _ocoStopLimitInput = string.Empty;
    private string _bracketTakeProfitInput = string.Empty;
    private int _bracketEntryKindIndex;
    private int _selectedSideIndex;
    private int _selectedTypeIndex;
    private string? _errorMessage;
    private string? _resultMessage;
    private string? _symbolError;
    private string? _quantityError;
    private string? _priceError;
    private string? _triggerError;
    private string? _trailError;
    private string? _ocoStopLimitError;
    private string? _bracketTakeProfitError;
    private string _activeExchangeName = string.Empty;
    private IReadOnlyList<string> _feeEstimates = [];
    private bool _isTestnetLane;
    private string _quantityUnitHint = string.Empty;
    private string _priceUnitHint = string.Empty;

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
            UpdateUnitHints();
        }
    }

    /// <summary>Quantity text, parsed with the invariant culture.</summary>
    public string QuantityInput
    {
        get => _quantityInput;
        set => this.RaiseAndSetIfChanged(ref _quantityInput, value);
    }

    /// <summary>Limit price text, parsed with the invariant culture. For the
    /// OCO mode this is the take-profit leg's limit price.</summary>
    public string PriceInput
    {
        get => _priceInput;
        set => this.RaiseAndSetIfChanged(ref _priceInput, value);
    }

    /// <summary>Stop trigger text, parsed with the invariant culture. The
    /// stop-limit's own trigger; in the OCO mode the stop-loss leg's
    /// trigger.</summary>
    public string StopTriggerInput
    {
        get => _stopTriggerInput;
        set => this.RaiseAndSetIfChanged(ref _stopTriggerInput, value);
    }

    /// <summary>Trailing distance text, parsed with the invariant culture.</summary>
    public string TrailDistanceInput
    {
        get => _trailDistanceInput;
        set => this.RaiseAndSetIfChanged(ref _trailDistanceInput, value);
    }

    /// <summary>Stop-loss limit price text of the OCO pair's stop leg,
    /// parsed with the invariant culture. In the bracket mode the SAME
    /// field prices the bracket's stop-loss leg (the same stop-limit
    /// shape).</summary>
    public string OcoStopLimitInput
    {
        get => _ocoStopLimitInput;
        set => this.RaiseAndSetIfChanged(ref _ocoStopLimitInput, value);
    }

    /// <summary>The bracket's take-profit leg limit price text, parsed with
    /// the invariant culture (T-186).</summary>
    public string BracketTakeProfitInput
    {
        get => _bracketTakeProfitInput;
        set => this.RaiseAndSetIfChanged(ref _bracketTakeProfitInput, value);
    }

    /// <summary>The bracket entry's kind combo index (T-186): 0 = Market
    /// (places at the market), 1 = Limit (prices the entry). The kind
    /// decides the entry price row and the fee-estimate entry line.</summary>
    public int BracketEntryKindIndex
    {
        get => _bracketEntryKindIndex;
        set
        {
            if (_bracketEntryKindIndex == value)
            {
                return;
            }

            _bracketEntryKindIndex = value;
            this.RaisePropertyChanged(nameof(BracketEntryKindIndex));
            this.RaisePropertyChanged(nameof(IsBracketEntryLimit));
            this.RaisePropertyChanged(nameof(IsPriceRequired));
            this.RaisePropertyChanged(nameof(IsPriceErrorVisible));
        }
    }

    /// <summary>
    /// Side combo box index (matches the <see cref="OrderSide"/> values).
    /// The segmented Buy/Sell control (T-156) rides the same state: the two
    /// buttons set it through their commands, the picked-side visibility
    /// properties raise with every change — the prefill and the tests keep
    /// writing the index directly.
    /// </summary>
    public int SelectedSideIndex
    {
        get => _selectedSideIndex;
        set
        {
            if (_selectedSideIndex == value)
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref _selectedSideIndex, value);
            this.RaisePropertyChanged(nameof(IsBuySide));
            this.RaisePropertyChanged(nameof(IsSellSide));
        }
    }

    /// <summary>True while the picked side is the buy side (T-156): the buy segment's selection state and the CTA's tint.</summary>
    public bool IsBuySide => SelectedSideIndex == (int)OrderSide.Buy;

    /// <summary>True while the picked side is the sell side (T-156): the sell segment's selection state and the CTA's tint.</summary>
    public bool IsSellSide => SelectedSideIndex == (int)OrderSide.Sell;

    /// <summary>
    /// Type combo box index: 0 = Market, 1 = Limit, 2 = StopLimit, 3 =
    /// TrailingStop, 4 = the OCO pair mode, 5 = the bracket mode (both UI
    /// modes, NOT <see cref="OrderType"/> values — the OCO pair places two
    /// either-or legs, the bracket places one entry carrying its exits).
    /// Raises every visibility the input rows follow.
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
            this.RaisePropertyChanged(nameof(IsTriggerVisible));
            this.RaisePropertyChanged(nameof(IsTrailVisible));
            this.RaisePropertyChanged(nameof(IsOcoMode));
            this.RaisePropertyChanged(nameof(IsOcoStopLimitVisible));
            this.RaisePropertyChanged(nameof(IsBracketMode));
            this.RaisePropertyChanged(nameof(IsBracketEntryLimit));
            this.RaisePropertyChanged(nameof(IsBracketTakeProfitVisible));
            this.RaisePropertyChanged(nameof(IsBracketTakeProfitErrorVisible));
            this.RaisePropertyChanged(nameof(IsPlainPriceCaption));
            this.RaisePropertyChanged(nameof(IsOcoStopTriggerCaption));
            this.RaisePropertyChanged(nameof(IsPriceErrorVisible));
            this.RaisePropertyChanged(nameof(IsTriggerErrorVisible));
            this.RaisePropertyChanged(nameof(IsTrailErrorVisible));
            this.RaisePropertyChanged(nameof(IsOcoStopLimitErrorVisible));
        }
    }

    /// <summary>The order type the combo selection maps to; null in the OCO
    /// and bracket modes (their builders decide the request shapes).</summary>
    private OrderType? SelectedType =>
        _selectedTypeIndex >= 0 && _selectedTypeIndex < TypeValues.Length
            && _selectedTypeIndex != OcoTypeIndex
            && _selectedTypeIndex != BracketTypeIndex
            ? TypeValues[_selectedTypeIndex]
            : null;

    /// <summary>True when the selection carries an explicit limit price:
    /// limit and stop-limit orders, the OCO mode (its take-profit leg), and
    /// the bracket mode with a limit entry (the entry's own price).</summary>
    public bool IsPriceRequired =>
        SelectedType is OrderType.Limit or OrderType.StopLimit || IsOcoMode || IsBracketEntryLimit;

    /// <summary>True when the stop trigger row applies: the stop-limit
    /// order, the OCO mode (its stop leg), and the bracket mode (the same
    /// stop-loss leg shape).</summary>
    public bool IsTriggerVisible => SelectedType == OrderType.StopLimit || IsOcoMode || IsBracketMode;

    /// <summary>True when the trailing-distance row applies.</summary>
    public bool IsTrailVisible => SelectedType == OrderType.TrailingStop;

    /// <summary>True when the type combo selects the one-cancels-the-other
    /// PAIR mode (a take-profit limit and a stop-loss stop-limit placed
    /// together).</summary>
    public bool IsOcoMode => _selectedTypeIndex == OcoTypeIndex;

    /// <summary>True when the type combo selects the bracket mode (T-186):
    /// an entry carrying its attached take-profit/stop-loss exits as ONE
    /// validated request.</summary>
    public bool IsBracketMode => _selectedTypeIndex == BracketTypeIndex;

    /// <summary>True while the bracket entry is a limit order pricing its
    /// own entry (T-186); a market entry places at the market.</summary>
    public bool IsBracketEntryLimit => IsBracketMode && _bracketEntryKindIndex == 1;

    /// <summary>True when the bracket's take-profit leg row applies (the
    /// bracket mode only).</summary>
    public bool IsBracketTakeProfitVisible => IsBracketMode;

    /// <summary>True when the stop-loss leg's limit price row applies
    /// (the OCO mode and the bracket mode — the same stop-limit shape).</summary>
    public bool IsOcoStopLimitVisible => IsOcoMode || IsBracketMode;

    /// <summary>Whether the price field's caption is the plain limit-price
    /// label: every mode except the OCO pair (its take-profit caption) and
    /// the bracket (its entry-price caption).</summary>
    public bool IsPlainPriceCaption => !IsOcoMode && !IsBracketMode;

    /// <summary>Whether the trigger field's caption is the stop-loss leg's
    /// label: the OCO pair and the bracket share the same stop-leg
    /// semantics.</summary>
    public bool IsOcoStopTriggerCaption => IsOcoMode || IsBracketMode;

    /// <summary>Whether the price field's inline error applies to the
    /// visible ticket (T-156): the line hides with its own row, so a type
    /// switch never leaves a dangling error under a hidden field.</summary>
    public bool IsPriceErrorVisible => HasPriceError && IsPriceRequired;

    /// <summary>Whether the trigger field's inline error applies to the visible ticket (T-156).</summary>
    public bool IsTriggerErrorVisible => HasTriggerError && IsTriggerVisible;

    /// <summary>Whether the trail field's inline error applies to the visible ticket (T-156).</summary>
    public bool IsTrailErrorVisible => HasTrailError && IsTrailVisible;

    /// <summary>Whether the OCO stop-limit field's inline error applies to the visible ticket (T-156).</summary>
    public bool IsOcoStopLimitErrorVisible => HasOcoStopLimitError && IsOcoStopLimitVisible;

    /// <summary>Whether the bracket take-profit field's inline error applies to the visible ticket (T-186).</summary>
    public bool IsBracketTakeProfitErrorVisible => HasBracketTakeProfitError && IsBracketTakeProfitVisible;

    /// <summary>The attempt's full report (parse failure, risk rejection,
    /// adapter failure) — the contract the behavior-parity battery reads
    /// verbatim. The VIEW routes it (T-156): a field-tagged refusal renders
    /// inline next to its field, the summary line keeps the flow lane.</summary>
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
            this.RaisePropertyChanged(nameof(HasSummaryError));
        }
    }

    /// <summary>True while <see cref="ErrorMessage"/> holds text; drives the error line visibility.</summary>
    public bool HasErrorMessage => _errorMessage is not null;

    /// <summary>
    /// Whether the attempt's report is a FLOW failure (T-156): the summary
    /// line's residents are the market-relative sanity readings, the risk
    /// rejections and the adapter failures — a field-tagged parse refusal
    /// renders next to its field instead, and the summary stays hidden for
    /// it (the attempt still reports in full through
    /// <see cref="ErrorMessage"/>; the behavior-parity battery reads it).
    /// </summary>
    public bool HasSummaryError => HasErrorMessage && !HasFieldError;

    /// <summary>True while any field owns the attempt's inline error (T-156).</summary>
    public bool HasFieldError =>
        SymbolError is not null
        || QuantityError is not null
        || PriceError is not null
        || TriggerError is not null
        || TrailError is not null
        || OcoStopLimitError is not null
        || BracketTakeProfitError is not null;

    /// <summary>
    /// Whether the daily loss governor currently blocks new orders for the
    /// ticket's parsed symbol's quote bucket (T-189): the banner's
    /// visibility. A display fact only — the placement pipeline re-checks
    /// the gate at every submit, so a stale banner can never let a blocked
    /// order through (and an exempt reduce-only exit never sees the block).
    /// </summary>
    public bool IsDailyLossBlocked
    {
        get => _isDailyLossBlocked;
        private set => this.RaiseAndSetIfChanged(ref _isDailyLossBlocked, value);
    }

    /// <summary>
    /// The banner's honest line (T-189): the day loss against the limit,
    /// with the closing nuance and the block's own lift terms. Empty while
    /// not blocked.
    /// </summary>
    public string DailyLossBanner
    {
        get => _dailyLossBanner;
        private set => this.RaiseAndSetIfChanged(ref _dailyLossBanner, value);
    }

    /// <summary>The symbol field's inline parse refusal (T-156): missing or unparsable text.</summary>
    public string? SymbolError
    {
        get => _symbolError;
        private set
        {
            if (_symbolError == value)
            {
                return;
            }

            _symbolError = value;
            this.RaisePropertyChanged(nameof(SymbolError));
            this.RaisePropertyChanged(nameof(HasSymbolError));
        }
    }

    /// <summary>True while the symbol field shows its inline error (T-156).</summary>
    public bool HasSymbolError => _symbolError is not null;

    /// <summary>The quantity field's inline parse refusal (T-156): missing or unparsable text.</summary>
    public string? QuantityError
    {
        get => _quantityError;
        private set
        {
            if (_quantityError == value)
            {
                return;
            }

            _quantityError = value;
            this.RaisePropertyChanged(nameof(QuantityError));
            this.RaisePropertyChanged(nameof(HasQuantityError));
        }
    }

    /// <summary>True while the quantity field shows its inline error (T-156).</summary>
    public bool HasQuantityError => _quantityError is not null;

    /// <summary>The price field's inline parse refusal (T-156) — the limit price, the OCO take-profit leg's price.</summary>
    public string? PriceError
    {
        get => _priceError;
        private set
        {
            if (_priceError == value)
            {
                return;
            }

            _priceError = value;
            this.RaisePropertyChanged(nameof(PriceError));
            this.RaisePropertyChanged(nameof(HasPriceError));
        }
    }

    /// <summary>True while the price field shows its inline error (T-156).</summary>
    public bool HasPriceError => _priceError is not null;

    /// <summary>The trigger field's inline parse refusal (T-156): the stop-limit's and the OCO stop leg's trigger.</summary>
    public string? TriggerError
    {
        get => _triggerError;
        private set
        {
            if (_triggerError == value)
            {
                return;
            }

            _triggerError = value;
            this.RaisePropertyChanged(nameof(TriggerError));
            this.RaisePropertyChanged(nameof(HasTriggerError));
        }
    }

    /// <summary>True while the trigger field shows its inline error (T-156).</summary>
    public bool HasTriggerError => _triggerError is not null;

    /// <summary>The trail field's inline parse refusal (T-156): missing, unparsable or market-swallowing distance.</summary>
    public string? TrailError
    {
        get => _trailError;
        private set
        {
            if (_trailError == value)
            {
                return;
            }

            _trailError = value;
            this.RaisePropertyChanged(nameof(TrailError));
            this.RaisePropertyChanged(nameof(HasTrailError));
        }
    }

    /// <summary>True while the trail field shows its inline error (T-156).</summary>
    public bool HasTrailError => _trailError is not null;

    /// <summary>The OCO stop-limit field's inline parse refusal (T-156): the stop-loss leg's own limit price.</summary>
    public string? OcoStopLimitError
    {
        get => _ocoStopLimitError;
        private set
        {
            if (_ocoStopLimitError == value)
            {
                return;
            }

            _ocoStopLimitError = value;
            this.RaisePropertyChanged(nameof(OcoStopLimitError));
            this.RaisePropertyChanged(nameof(HasOcoStopLimitError));
        }
    }

    /// <summary>True while the OCO stop-limit field shows its inline error (T-156).</summary>
    public bool HasOcoStopLimitError => _ocoStopLimitError is not null;

    /// <summary>The bracket take-profit field's inline parse refusal (T-186): the exit leg's own limit price.</summary>
    public string? BracketTakeProfitError
    {
        get => _bracketTakeProfitError;
        private set
        {
            if (_bracketTakeProfitError == value)
            {
                return;
            }

            _bracketTakeProfitError = value;
            this.RaisePropertyChanged(nameof(BracketTakeProfitError));
            this.RaisePropertyChanged(nameof(HasBracketTakeProfitError));
        }
    }

    /// <summary>True while the bracket take-profit field shows its inline error (T-186).</summary>
    public bool HasBracketTakeProfitError => _bracketTakeProfitError is not null;

    /// <summary>
    /// Route the attempt's build failure to its display surface (T-156): a
    /// field-tagged error lands next to its field and clears every other
    /// field's line, an untagged one clears the inline lane entirely. The
    /// summary's <see cref="ErrorMessage"/> contract is untouched either
    /// way — the caller reports there in full.
    /// </summary>
    private void ApplyFieldError(TicketField field, string? error)
    {
        SymbolError = field == TicketField.Symbol ? error : null;
        QuantityError = field == TicketField.Quantity ? error : null;
        PriceError = field == TicketField.Price ? error : null;
        TriggerError = field == TicketField.Trigger ? error : null;
        TrailError = field == TicketField.Trail ? error : null;
        OcoStopLimitError = field == TicketField.OcoStopLimit ? error : null;
        BracketTakeProfitError = field == TicketField.BracketTakeProfit ? error : null;
        this.RaisePropertyChanged(nameof(HasFieldError));
        this.RaisePropertyChanged(nameof(HasSummaryError));
        this.RaisePropertyChanged(nameof(IsPriceErrorVisible));
        this.RaisePropertyChanged(nameof(IsTriggerErrorVisible));
        this.RaisePropertyChanged(nameof(IsTrailErrorVisible));
        this.RaisePropertyChanged(nameof(IsOcoStopLimitErrorVisible));
        this.RaisePropertyChanged(nameof(IsBracketTakeProfitErrorVisible));
    }

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
    /// Whether the workspace's traded surface currently rides the TESTNET
    /// lane (T-127): the ticket's badge mirrors the status-bar chip so the
    /// operator sees the venue surface the placement pipeline feeds. Set by
    /// the owning window from the trading-mode lane stream — the view model
    /// holds no lane service of its own.
    /// </summary>
    public bool IsTestnetLane
    {
        get => _isTestnetLane;
        set => this.RaiseAndSetIfChanged(ref _isTestnetLane, value);
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
    /// The fee estimate lines of the ticket (T-080): one per leg the ticket
    /// would place, each naming the role the leg is expected to ride
    /// (maker for the resting limit legs, taker for everything else) and
    /// the priced commission from the adapter's schedule — or the localized
    /// dash when the schedule is unknown or the inputs incomplete. The fee
    /// observes only: nothing here feeds a placement decision.
    /// </summary>
    public IReadOnlyList<string> FeeEstimates
    {
        get => _feeEstimates;
        private set => this.RaiseAndSetIfChanged(ref _feeEstimates, value);
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
    /// The quantity caption's unit hint (T-156): the parsed symbol's BASE
    /// asset, formatted through the shared unit-hint format — empty while
    /// the field names no parsable symbol (the caption rides plain).
    /// </summary>
    public string QuantityUnitHint
    {
        get => _quantityUnitHint;
        private set => this.RaiseAndSetIfChanged(ref _quantityUnitHint, value);
    }

    /// <summary>
    /// The price caption's unit hint (T-156): the parsed symbol's QUOTE
    /// asset — empty while the symbol parses to nothing or the instrument
    /// carries no quote asset (a quote-less price has no unit to name).
    /// </summary>
    public string PriceUnitHint
    {
        get => _priceUnitHint;
        private set => this.RaiseAndSetIfChanged(ref _priceUnitHint, value);
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

    /// <summary>
    /// Loads the sizing default once per dialog open — the stored
    /// risk-percent default of the ticket's quick-size row
    /// (settings changes afterwards ride the reactive subscription). The
    /// captures themselves start on the symbol commits; nothing here
    /// fetches at sizing time.
    /// </summary>
    public ReactiveCommand<RxVoid, RxVoid> InitializeSizingCommand { get; }

    /// <summary>
    /// The daily loss limit's operator reset (T-189): ONE confirmation, then
    /// the marker write lifts the block until the day rolls over. Enabled
    /// only while the banner shows (the command rides the banner's own
    /// visibility in the view).
    /// </summary>
    public ReactiveCommand<RxVoid, RxVoid> ResetDailyLossCommand { get; }

    /// <summary>Picks the buy side (T-156): the segmented control's buy half.</summary>
    public ReactiveCommand<RxVoid, RxVoid> SelectBuySideCommand { get; }

    /// <summary>Picks the sell side (T-156): the segmented control's sell half.</summary>
    public ReactiveCommand<RxVoid, RxVoid> SelectSellSideCommand { get; }

    /// <summary>The quick-size row the ticket hosts (hidden for metadata-less instruments).</summary>
    public OrderSizerRowViewModel Sizer { get; }

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
    /// <param name="sizing">The position-sizing captures (T-079): balance and reference snapshots for the row.</param>
    /// <param name="settings">The settings service: the sizing row's stored defaults ride it.</param>
    /// <param name="confirmPolicy">The pre-submit confirmation policy (T-078): whether a placement's confirmation gate opens at all.</param>
    public NewOrderDialogViewModel(
        ITradingAdapter trading,
        SymbolListProvider symbolList,
        ActiveExchangeService activeExchanges,
        IRiskManager riskManager,
        IConfirmationService confirmation,
        OrderConfirmationComposer composer,
        OrderConfirmPolicy confirmPolicy,
        IOrderJournal orderJournal,
        IOrderHistoryStore orderHistory,
        IFeeScheduleProvider feeSchedules,
        ILocalizationManager localization,
        PositionSizingService sizing,
        ISettingsService settings,
        ILogger<NewOrderDialogViewModel> logger,
        ILogger<OrderSizerRowViewModel> sizerLogger,
        IDailyLossLimit dailyLoss)
    {
        _trading = trading ?? throw new ArgumentNullException(nameof(trading));
        _symbolList = symbolList ?? throw new ArgumentNullException(nameof(symbolList));
        _activeExchanges = activeExchanges ?? throw new ArgumentNullException(nameof(activeExchanges));
        _riskManager = riskManager ?? throw new ArgumentNullException(nameof(riskManager));
        _confirmation = confirmation ?? throw new ArgumentNullException(nameof(confirmation));
        _composer = composer ?? throw new ArgumentNullException(nameof(composer));
        _confirmPolicy = confirmPolicy ?? throw new ArgumentNullException(nameof(confirmPolicy));
        _orderJournal = orderJournal ?? throw new ArgumentNullException(nameof(orderJournal));
        _orderHistory = orderHistory ?? throw new ArgumentNullException(nameof(orderHistory));
        _feeSchedules = feeSchedules ?? throw new ArgumentNullException(nameof(feeSchedules));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _sizing = sizing ?? throw new ArgumentNullException(nameof(sizing));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _dailyLoss = dailyLoss ?? throw new ArgumentNullException(nameof(dailyLoss));

        CloseRequested = new Interaction<RxVoid, RxVoid>();
        LoadSymbolsCommand = ReactiveCommand.CreateFromTask(HandleLoadSymbolsAsync);
        PlaceOrderCommand = ReactiveCommand.CreateFromTask(HandlePlaceOrderAsync);
        InitializeSizingCommand = ReactiveCommand.CreateFromTask(HandleInitializeSizingAsync);
        ResetDailyLossCommand = ReactiveCommand.CreateFromTask(HandleResetDailyLossAsync);
        SelectBuySideCommand = ReactiveCommand.Create<RxVoid, RxVoid>(_ =>
        {
            SelectedSideIndex = (int)OrderSide.Buy;
            return RxVoid.Default;
        });
        SelectSellSideCommand = ReactiveCommand.Create<RxVoid, RxVoid>(_ =>
        {
            SelectedSideIndex = (int)OrderSide.Sell;
            return RxVoid.Default;
        });

        // The ticket-local quick-size row: its quantity proposals land in
        // THIS dialog's quantity field (the same field the submit pipeline
        // parses), and the row's subscriptions die with the ticket's.
        _sizer = new OrderSizerRowViewModel(_sizing, _settings, _localization, sizerLogger);
        Sizer = _sizer;
        _subscriptions.Add(_sizer);
        _subscriptions.Add(_submitGate);
        _subscriptions.Add(
            _sizer
                .QuantityProposals
                .Subscribe(quantity => QuantityInput = quantity));

        // The sizing contexts ride the ticket's own inputs: every symbol
        // edit re-commits the metadata-known pair (or hides the row), and
        // every shape input recomputes the entry price and stop distance
        // the row's presets and risk mode ride.
        _subscriptions.Add(
            this.WhenAnyValue(x => x.SymbolInput)
                .Subscribe(_ => UpdateSizerSymbol()));
        _subscriptions.Add(
            this.WhenAnyValue(
                    x => x.SelectedTypeIndex,
                    x => x.SelectedSideIndex,
                    x => x.PriceInput,
                    x => x.StopTriggerInput,
                    x => x.TrailDistanceInput,
                    x => x.BracketEntryKindIndex,
                    x => x.BracketTakeProfitInput)
                .Subscribe(_ => UpdateSizerOrderShape()));

        // The header mirrors the active exchange on the same behavioral
        // stream every other consumer uses: emission zero provides the
        // bootstrap value (the stream emits its current adapter on
        // subscribe), later emissions track toolbar switches. The exchange
        // id rides along — the fee schedule resolves per adapter id (T-080).
        // Subscriptions park in the composite disposable — the transient VM
        // must not outlive its window.
        _subscriptions.Add(
            _activeExchanges
                .AdapterChanges
                .Subscribe(
                    adapter =>
                    {
                        // The id first: the name assignment re-prices the
                        // estimate synchronously (the WhenAnyValue chain),
                        // and it must read the FRESH schedule id — the
                        // reverse order left the lines one switch behind.
                        _activeExchangeId = adapter.Id ?? string.Empty;
                        ActiveExchangeName = adapter.Name;
                    },
                    ex => _logger.LogError(ex, "The dialog exchange header stream stopped unexpectedly")));

        // The estimate lines re-price on every input the notional reads and
        // on the exchange switch (whose id resolves the schedule). The
        // bootstrapping initial emission prices the empty-ticket dash.
        _subscriptions.Add(
            this.WhenAnyValue(
                    x => x.ActiveExchangeName,
                    x => x.SymbolInput,
                    x => x.QuantityInput,
                    x => x.PriceInput,
                    x => x.OcoStopLimitInput,
                    x => x.BracketTakeProfitInput,
                    x => x.SelectedTypeIndex,
                    x => x.BracketEntryKindIndex)
                .Subscribe(_ => RefreshFeeEstimates()));

        // The price-less types (market, trailing) price their notional with
        // the latest close: one throttled convenience read per settled
        // symbol, tolerated to fail (the line keeps its dash — the estimate
        // never blocks, never errors the ticket).
        _subscriptions.Add(
            this.WhenAnyValue(
                    x => x.SymbolInput,
                    x => x.ActiveExchangeName,
                    (symbol, exchange) => (symbol, exchange))
                .Where(context => !string.IsNullOrWhiteSpace(context.symbol))
                .Throttle(EstimateReferenceThrottle)
                .Select(_ => Observable.FromAsync(FetchEstimateReferenceAsync))
                .Switch()
                .ObserveOnUiThread()
                .Subscribe(
                    close =>
                    {
                        _referenceClose = close;
                        RefreshFeeEstimates();
                    },
                    error => _logger.LogWarning(
                        error,
                        "Order dialog: the fee-estimate reference stream stopped unexpectedly")));

        // The daily loss banner (T-189): a throttled state read per settled
        // symbol — the same quiet-window discipline the fee estimate's
        // reference read rides (the bootstrapping initial emission prices
        // the banner at dialog open). The read is tolerated to fail inside
        // the refresh itself: a banner whose state is unknown stays hidden
        // — the submit pipeline's own gate re-checks the truth at every
        // placement.
        _subscriptions.Add(
            this.WhenAnyValue(x => x.SymbolInput)
                .Throttle(EstimateReferenceThrottle)
                .Select(_ => Observable.FromAsync(() => RefreshDailyLossBannerAsync(force: false)))
                .Switch()
                .ObserveOnUiThread()
                .Subscribe(
                    _ => { },
                    error => _logger.LogWarning(
                        error,
                        "Order dialog: the daily loss banner stream stopped unexpectedly")));
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

    #region Fee Estimates

    /// <summary>
    /// Re-price the estimate lines from the current ticket state: the
    /// schedule resolves for the active exchange's id, and every leg the
    /// ticket would place prices maker or taker per its type. Never a
    /// placement input — the recomputation only ever writes display state.
    /// </summary>
    private void RefreshFeeEstimates()
    {
        // The id may be empty before (or without) the facade's bootstrap
        // emission: an exchange without an id has no schedule to name.
        var schedule = _activeExchangeId.Length == 0
            ? null
            : _feeSchedules.GetSchedule(_activeExchangeId);

        // The OCO pair estimates its two legs, the bracket its three (the
        // entry prices by its kind), every single selection one order.
        var legs = SelectedTypeIndex switch
        {
            OcoTypeIndex => new[] { (OrderType.Limit, PriceInput), (OrderType.StopLimit, OcoStopLimitInput) },
            BracketTypeIndex => IsBracketEntryLimit
                ? new[]
                {
                    (OrderType.Limit, PriceInput),
                    (OrderType.Limit, BracketTakeProfitInput),
                    (OrderType.StopLimit, OcoStopLimitInput),
                }
                : new[]
                {
                    (OrderType.Market, string.Empty),
                    (OrderType.Limit, BracketTakeProfitInput),
                    (OrderType.StopLimit, OcoStopLimitInput),
                },
            _ => new[] { SingleEstimateOrder() },
        };

        FeeEstimates = legs
            .Select(leg => FormatEstimateLeg(schedule, leg.Item1, leg.Item2))
            .ToArray();
    }

    /// <summary>
    /// The order shape the current single-selection maps to: the type (the
    /// OCO index never reaches here) and the field that prices the leg —
    /// the limit price for a price-bearing shape, nothing otherwise (the
    /// market reference answers, or stays absent).
    /// </summary>
    private (OrderType Type, string PriceText) SingleEstimateOrder() =>
        SelectedType is not { } type
            ? (OrderType.Market, string.Empty)
            : type is OrderType.Limit or OrderType.StopLimit
                ? (type, PriceInput)
                : (type, string.Empty);

    /// <summary>
    /// One estimate line for one leg: its expected role rides the type
    /// (limit = maker, everything else crosses), the notional needs a
    /// positive quantity and price (the leg's own limit, or the resolved
    /// market reference for the price-less shapes), and the commission is
    /// the schedule's own rounding/floor policy applied — the localized
    /// dash whenever any of these stays unknown, never a fabricated zero.
    /// </summary>
    /// <param name="schedule">The active adapter's fee schedule; null prices the dash.</param>
    /// <param name="type">The leg's order type.</param>
    /// <param name="priceText">The leg's limit-price field when it carries one.</param>
    private string FormatEstimateLeg(FeeSchedule? schedule, OrderType type, string priceText)
    {
        var role = FeeRoleEstimation.ForOrderType(type);
        var quantity = TryParsePositiveAmount(QuantityInput);
        var price = type is OrderType.Limit or OrderType.StopLimit
            ? TryParsePositiveAmount(priceText)
            : _referenceClose;

        var amount = schedule is { } scheduleValue && quantity is { } quantityValue && price is { } priceValue
            ? scheduleValue
                .Commission(role, quantityValue * priceValue)
                .ToString(FeeAmountFormat, CultureInfo.InvariantCulture)
            : _localization.GetString(FeeUnknownKey);

        return _localization.GetString(
            role == FeeRole.Maker ? FeeEstimateMakerKey : FeeEstimateTakerKey,
            amount);
    }

    /// <summary>
    /// Parse a positive invariant-culture amount, or answer nothing: the
    /// estimate stays unknown while a field is blank, unparsable or
    /// non-positive (never a defaulted number).
    /// </summary>
    private static decimal? TryParsePositiveAmount(string? rawText)
    {
        var raw = rawText?.Trim();
        return decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) && value > 0m
            ? value
            : null;
    }

    /// <summary>
    /// The throttled reference read the price-less types' notional rides:
    /// one H1-close fetch per settled symbol/exchange switch, answered with
    /// nothing when the reference cannot resolve (offline, no bars) — a
    /// convenience upgrade of the estimate, never a ticket error.
    /// </summary>
    private async Task<decimal?> FetchEstimateReferenceAsync(CancellationToken ct)
    {
        try
        {
            var symbol = Symbol.Parse(SymbolInput.Trim());
            return (await MarketReference.GetLatestCloseAsync(_trading, symbol, ct)).Value;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Order dialog: no market reference for the fee estimate right now");
            return null;
        }
    }

    #endregion Fee Estimates

    #region Confirmation Facts

    /// <summary>
    /// Whether the placement's venue is the paper adapter: the canonical
    /// venue-id comparison every service rides (BulkCancelService), since
    /// the placement pipeline crosses the registered trading adapter that
    /// the market-data facade's switches never re-route.
    /// </summary>
    private bool IsPaperVenue() =>
        string.Equals(_trading.Id, PaperTradingAdapter.ExchangeId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The schedule's estimated commission for the order's role: the SAME
    /// seam the header estimate lines price (the active venue's schedule,
    /// the maker/taker role the type runs) over the order's estimated
    /// notional. Nothing answers when the schedule or notional is absent —
    /// the summary's unknown line, never a fabricated number.
    /// </summary>
    private decimal? EstimateScheduleFee(OrderRequest request)
    {
        var schedule = _activeExchangeId.Length == 0
            ? null
            : _feeSchedules.GetSchedule(_activeExchangeId);
        var notional = OrderNotional.EstimateUsd(request, _referenceClose);

        return schedule is { } scheduleValue && notional is { } notionalValue
            ? scheduleValue.Commission(FeeRoleEstimation.ForOrderType(request.Type), notionalValue)
            : null;
    }

    #endregion Confirmation Facts

    #region IDisposable

    /// <summary>
    /// Release the facade subscription. The owning window calls this on
    /// closure — idempotent, the composite guards repeated calls.
    /// </summary>
    public void Dispose() => _subscriptions.Dispose();

    #endregion IDisposable

    #region Private Methods

    /// <summary>
    /// The sizing default load (one dialog open pays it once): the stored
    /// risk-percent default applies onto the row's untouched field. A
    /// failure keeps the hardcoded default — a convenience load, never a
    /// dialog error. The execution token rides the comment's lifetime:
    /// the window disposal cancels the command's token.
    /// </summary>
    private async Task HandleInitializeSizingAsync(CancellationToken ct)
    {
        try
        {
            await _sizer.InitializeDefaultsAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // The dialog closed while the load ran in flight: nothing
            // user-visible remains.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Order dialog: the sizing default load failed; the hardcoded default stays");
        }
    }

    /// <summary>
    /// The daily loss banner read (T-189): the parsed symbol's quote
    /// bucket's honest state on the current lane. A symbol change skips
    /// when the parse didn't move (the throttle already coalesced the
    /// keystrokes); a forced read re-reads the same symbol (a failed
    /// placement or a reset may have moved the state). Tolerated to fail:
    /// the banner stays hidden on a fault — a display convenience, never a
    /// gate, and never a dialog error.
    /// </summary>
    private async Task RefreshDailyLossBannerAsync(bool force)
    {
        Symbol? symbol = null;
        var raw = SymbolInput.Trim();
        if (raw.Length > 0)
        {
            try
            {
                symbol = Symbol.Parse(raw);
            }
            catch (ArgumentException)
            {
                // Unparsable text owns no quote bucket to attribute.
            }
        }

        if (!force && symbol?.Normalized == _dailyLossBannerSymbol)
        {
            return;
        }

        if (symbol is null)
        {
            _dailyLossBannerSymbol = null;
            IsDailyLossBlocked = false;
            DailyLossBanner = string.Empty;
            return;
        }

        try
        {
            var state = await _dailyLoss.ReadStateAsync(symbol);
            _dailyLossBannerSymbol = symbol.Normalized;
            IsDailyLossBlocked = state.Enabled && state.IsBlocked;
            DailyLossBanner = IsDailyLossBlocked
                ? _localization.GetString(
                    DailyLossBannerKey,
                    $"{Math.Abs(state.DayLoss).ToString("F2", CultureInfo.InvariantCulture)} {state.Quote}",
                    $"{state.Limit.ToString("F2", CultureInfo.InvariantCulture)} {state.Quote}")
                : string.Empty;
        }
        catch (OperationCanceledException)
        {
            // The dialog closed while the read ran: nothing user-visible remains.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Order dialog: the daily loss banner read failed; the banner stays hidden");
        }
    }

    /// <summary>
    /// The operator's daily loss reset (T-189): ONE confirmation names what
    /// the reset does, then the marker write lifts the block until the day
    /// rolls over, and the banner re-reads the fresh state.
    /// </summary>
    private async Task HandleResetDailyLossAsync()
    {
        try
        {
            var confirmed = await _confirmation.RequestConfirmationAsync(
                new ConfirmationRequest { MessageKey = DailyLossResetConfirmKey });
            if (!confirmed)
            {
                return;
            }

            await _dailyLoss.ResetAsync();
            await RefreshDailyLossBannerAsync(force: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Order dialog: the daily loss reset failed");
            ErrorMessage = _localization.GetString(DailyLossResetFailedKey, ex.Message);
        }
    }

    /// <summary>
    /// Re-derive the unit hints (T-156) from the typed symbol: a clean parse
    /// names the caption units (the BASE sizes the quantity, the QUOTE
    /// prices the field), garbage or empty text clears both hints. Display
    /// state only — nothing here feeds the build.
    /// </summary>
    private void UpdateUnitHints()
    {
        Symbol? symbol = null;
        var raw = SymbolInput.Trim();
        if (raw.Length > 0)
        {
            try
            {
                symbol = Symbol.Parse(raw);
            }
            catch (ArgumentException)
            {
                // Unparsable text leaves the captions plain; no unit is
                // derived from garbage input.
            }
        }

        QuantityUnitHint = symbol is null
            ? string.Empty
            : _localization.GetString(UnitHintFormatKey, symbol.BaseAsset);
        PriceUnitHint = symbol?.QuoteAsset is { } quote
            ? _localization.GetString(UnitHintFormatKey, quote)
            : string.Empty;
    }

    /// <summary>
    /// Push the ticket's parsed symbol to the sizing row: exact parses
    /// commit (a metadata-known commit also schedules the fresh
    /// capture), everything else hides the row without stale context.
    /// </summary>
    private void UpdateSizerSymbol()
    {
        var raw = SymbolInput.Trim();
        Symbol? symbol = null;
        if (raw.Length > 0)
        {
            try
            {
                symbol = Symbol.Parse(raw);
            }
            catch (ArgumentException)
            {
                // Unparsable text keeps the row parked; no sizing context
                // is derived from garbage input.
            }
        }

        _sizer.UpdateSymbol(symbol);
    }

    /// <summary>
    /// Push the ticket's current order shape to the sizing row: the side,
    /// the execution price the type carries (the limit price for limit,
    /// stop-limit and the OCO take-profit leg), and the type's stop
    /// distance when it anchors one — a stop-limit's |limit − trigger|, a
    /// trailing stop's trail. Market and plain-limit orders (and the OCO
    /// pair) carry none: the risk mode stays hidden for exactly those.
    /// Unparsable or empty fields contribute nothing — no sizing input
    /// substitutes a default for a field the user has not finished
    /// entering.
    /// </summary>
    /// <summary>
    /// Push the ticket's current order shape to the sizing row: the side,
    /// the execution price the type carries (the limit price for limit,
    /// stop-limit and the OCO take-profit leg), and the type's stop
    /// distance when it anchors one — a stop-limit's |limit − trigger|, a
    /// trailing stop's trail. Market and plain-limit orders (and the OCO
    /// pair) carry none: the risk mode stays hidden for exactly those. The
    /// bracket (T-186) anchors its distance on a LIMIT entry against its
    /// stop-loss trigger; a market entry prices no entry, so the risk mode
    /// stays hidden for it too. Unparsable or empty fields contribute
    /// nothing — no sizing input substitutes a default for a field the user
    /// has not finished entering.
    /// </summary>
    private void UpdateSizerOrderShape()
    {
        var side = (OrderSide)SelectedSideIndex;
        var type = SelectedType;
        var selectsLimitPrice = type is OrderType.Limit or OrderType.StopLimit || IsOcoMode;
        var entryPrice = selectsLimitPrice ? TryParsePositiveOrNull(PriceInput) : null;

        decimal? stopDistance = type switch
        {
            OrderType.StopLimit => TryParsePositiveOrNull(StopTriggerInput) is { } trigger
                && entryPrice is { } limit
                ? Math.Abs(limit - trigger)
                : null,
            OrderType.TrailingStop => TryParsePositiveOrNull(TrailDistanceInput),
            _ => null,
        };

        if (IsBracketMode)
        {
            var bracketEntryPrice = IsBracketEntryLimit ? TryParsePositiveOrNull(PriceInput) : null;
            decimal? bracketStopDistance = bracketEntryPrice is { } entryLimit
                && TryParsePositiveOrNull(StopTriggerInput) is { } stopTrigger
                    ? Math.Abs(entryLimit - stopTrigger)
                    : null;

            _sizer.UpdateOrderShape(side, null, bracketEntryPrice, bracketStopDistance);
            return;
        }

        _sizer.UpdateOrderShape(side, type, entryPrice, stopDistance);
    }

    /// <summary>Parse an invariant-culture positive number, or null when the field is empty, garbage or non-positive.</summary>
    private static decimal? TryParsePositiveOrNull(string raw) =>
        decimal.TryParse(raw.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
            && value > 0m
            ? value
            : null;

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
        // click, double Enter) must still be impossible: never two pipelined
        // placements. The command's invocations may run concurrently, so the
        // gate is an atomic try-acquire at the TOP of the handler — the whole
        // submit rides it, the build's yields included.
        if (!await _submitGate.WaitAsync(TimeSpan.Zero))
        {
            return;
        }

        try
        {
            await RunPlaceOrderAsync();
        }
        finally
        {
            _submitGate.Release();
        }
    }

    private async Task RunPlaceOrderAsync()
    {
        // A fresh attempt replaces whatever the previous attempt reported.
        ErrorMessage = null;
        ResultMessage = null;
        ApplyFieldError(TicketField.None, null);

        var build = (RequestBuild?)null;
        var placed = new List<Order>();
        try
        {
            // The conditional types ride a market reference rate fetched during
            // the build, so even the build can fail over the network — the same
            // localized-error-line treatment as a failed placement, never an
            // escape into the unobserved ReactiveCommand.ThrownExceptions.
            try
            {
                build = await BuildRequestsAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Order dialog: building the order requests failed");
                ErrorMessage = _localization.GetString(PlaceFailedKey, ex.Message);
                return;
            }

            if (build.Failed)
            {
                // The report routes by owner (T-156): a field-tagged parse
                // refusal renders next to its field, a flow failure stays
                // on the summary line. ErrorMessage carries the full text
                // either way — the behavior-parity battery reads it.
                ApplyFieldError(build.Field, build.Field == TicketField.None ? null : build.Error);
                ErrorMessage = build.Error;
                return;
            }

            if (build.IsBracket)
            {
                // The bracket submits as ONE request (T-186): the entry
                // carries its exits to the adapter, so the per-request loop
                // below never runs — there is no partial pair to tear down,
                // the exits are never placed separately.
                await PlaceBracketAsync(build.Requests[0]);
                return;
            }

            foreach (var request in build.Requests)
            {
                var order = await PlaceSingleAsync(request);
                if (order is null)
                {
                    // A null result already reported itself (risk rejection,
                    // invalid parameters) or is the silently-stopped user
                    // confirmation path; either way no further leg runs.
                    break;
                }

                placed.Add(order);
            }

            if (placed.Count < build.Requests.Count)
            {
                // An incomplete OCO pair must not leave its placed half
                // working: the either-or contract is either whole or absent.
                await CancelPlacedLegsAsync(placed);
                if (build.Requests.Count > 1)
                {
                    // A single aborted placement stays silent on the
                    // user-cancelled path (the reported rejection remains
                    // otherwise); the pair abort always names itself.
                    ErrorMessage ??= _localization.GetString(OcoAbortedKey);
                }

                // An aborted pair may have moved the day's loss: the banner
                // re-reads its state honestly (T-189).
                await RefreshDailyLossBannerAsync(force: true);
                return;
            }

            ResultMessage = build.Requests.Count == 1
                ? _localization.GetString(OrderPlacedKey, placed[0].Id)
                : _localization.GetString(OcoPlacedKey, placed[0].Id, placed[1].Id);
            _logger.LogInformation(
                "Order dialog: placed {OrderId} ({Status})",
                placed[0].Id,
                placed[0].Status);

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

            var single = (build?.Requests.Count ?? 0) == 1;
            await CancelPlacedLegsAsync(single ? [] : placed);
            ErrorMessage = single
                ? _localization.GetString(PlaceFailedKey, ex.Message)
                : _localization.GetString(OcoAbortedKey);

            // A failed placement may have moved the day's loss: the banner
            // re-reads its state honestly (T-189).
            await RefreshDailyLossBannerAsync(force: true);
        }
    }

    /// <summary>
    /// The bracket's atomic submit (T-186): the entry validates WITH its
    /// exits (one validated request), EVERY leg passes the risk manager
    /// before anything is submitted — one rejected leg refuses the whole
    /// bracket, nothing has been sent anywhere — and ONE confirmation names
    /// all three legs (the ask decision rides any leg's mandated gate plus
    /// the policy's reading of the entry, the operation's dominant leg).
    /// The placement is ONE adapter call: the surface assembles the whole
    /// shape or refuses it before anything registers. A refusal reports
    /// through the error line and keeps the dialog open.
    /// </summary>
    private async Task PlaceBracketAsync(OrderRequest entry)
    {
        try
        {
            // The intrinsic request matrix runs here ahead of any other
            // gate: the final shape guard an unparsable bracket cannot slip
            // past (the legs validate with the entry).
            entry.Validate();
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(
                ex,
                "Order dialog: the built bracket request is invalid and was never sent to a gate");
            ErrorMessage = _localization.GetString(InvalidParametersKey, ex.Message);
            return;
        }

        var legs = new[] { entry, entry.BracketLegs!.TakeProfit, entry.BracketLegs.StopLoss };
        var riskResults = new List<RiskCheckResult>(legs.Length);
        foreach (var leg in legs)
        {
            var legRisk = await _riskManager.CheckOrderAsync(leg);
            if (!legRisk.IsApproved)
            {
                _logger.LogWarning(
                    "Order dialog: risk manager rejected the bracket's {Type} {Side} leg {Quantity} {Symbol}: {Reason}",
                    leg.Type, leg.Side, leg.Quantity.Value, leg.Symbol.Normalized, legRisk.Reason);
                ErrorMessage = _localization.GetString(OrderRejectedKey, legRisk.Reason ?? string.Empty);

                // A risk rejection may be the daily loss governor's own
                // block (T-189): the banner re-reads its state honestly.
                await RefreshDailyLossBannerAsync(force: true);
                return;
            }

            riskResults.Add(legRisk);
        }

        var isPaper = IsPaperVenue();
        var facts = new OrderPlaceFacts(
            IsPaper: isPaper,
            LiveVenueName: _trading.Name,
            EstimatedNotionalUsd: OrderNotional.EstimateUsd(entry, _referenceClose),
            EstimatedFee: EstimateScheduleFee(entry));
        var asksConfirmation = riskResults.Any(legRisk => legRisk.RequiresConfirmation)
            || await _confirmPolicy.ShouldConfirmAsync(isPaper, entry.Type, facts.EstimatedNotionalUsd);

        var confirmed = !asksConfirmation
            || await _confirmation.RequestConfirmationAsync(_composer.ForBracket(entry, entry.BracketLegs!, facts));
        if (!confirmed)
        {
            _logger.LogInformation("Order dialog: user cancelled the bracket confirmation; nothing placed");
            return;
        }

        try
        {
            var order = await _trading.PlaceOrderAsync(entry);
            _orderJournal.RecordPlaced(order);

            // Persist the placed entry into the SQLite journal. Best effort:
            // the adapter accepted the bracket already, so a persistence
            // fault must never turn a successful placement into a dialog
            // error.
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

            ResultMessage = _localization.GetString(BracketPlacedKey, order.Id);
            _logger.LogInformation(
                "Order dialog: placed bracket entry {OrderId} ({Status})",
                order.Id,
                order.Status);
            await CloseRequested.Handle(RxVoid.Default);
        }
        catch (Exception ex)
        {
            // The adapter assembles the bracket from the ONE request: a
            // refusal (an unsupported surface, an unparsable shape) leaves
            // NOTHING on the surface — the error line reports the reason and
            // the dialog stays open (BUG-017 discipline: never a silent
            // unplaced order).
            _logger.LogError(ex, "Order dialog: bracket placement failed");
            ErrorMessage = _localization.GetString(PlaceFailedKey, ex.Message);

            // A failed bracket may have moved the day's loss: the banner
            // re-reads its state honestly (T-189).
            await RefreshDailyLossBannerAsync(force: true);
        }
    }

    /// <summary>
    /// Validate → risk → confirmation → placement of ONE order request, plus
    /// its journal and history records. Returns the placed order, or null
    /// when the pipeline stopped before a placement (rule rejection,
    /// unparsable parameters, or the user cancelling the confirmation dialog);
    /// a rejected attempt reports through <see cref="ErrorMessage"/> and keeps
    /// the dialog open.
    /// </summary>
    private async Task<Order?> PlaceSingleAsync(OrderRequest request)
    {
        try
        {
            // The intrinsic request matrix runs here ahead of any other gate:
            // the final shape guard an unparsable request cannot slip past.
            request.Validate();
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(
                ex,
                "Order dialog: the built request is invalid and was never sent to a gate");
            ErrorMessage = _localization.GetString(InvalidParametersKey, ex.Message);
            return null;
        }

        var risk = await _riskManager.CheckOrderAsync(request);
        if (!risk.IsApproved)
        {
            _logger.LogWarning(
                "Order dialog: risk manager rejected {Side} {Quantity} {Symbol}: {Reason}",
                request.Side, request.Quantity.Value, request.Symbol.Normalized, risk.Reason);
            ErrorMessage = _localization.GetString(OrderRejectedKey, risk.Reason ?? string.Empty);

            // A risk rejection may be the daily loss governor's own block
            // (T-189): the banner re-reads its state honestly.
            await RefreshDailyLossBannerAsync(force: true);
            return null;
        }

        // The confirm composes ONCE from the final ticket state (T-078): the
        // facts and the ask decision are submit-time snapshots — inputs stop
        // moving while the placement pipeline runs, so no per-keystroke
        // re-valuation ever rides the dialog. The risk engine's own demand
        // is never relaxed: a large-order gate the manager mandated stands
        // whatever the policy says (and paper's master switch, however it
        // is set, cannot remove a mandated gate either).
        var isPaper = IsPaperVenue();
        var facts = new OrderPlaceFacts(
            IsPaper: isPaper,
            LiveVenueName: _trading.Name,
            EstimatedNotionalUsd: OrderNotional.EstimateUsd(request, _referenceClose),
            EstimatedFee: EstimateScheduleFee(request));
        var asksConfirmation = risk.RequiresConfirmation
            || await _confirmPolicy.ShouldConfirmAsync(isPaper, request.Type, facts.EstimatedNotionalUsd);

        var confirmed = !asksConfirmation
            || await _confirmation.RequestConfirmationAsync(_composer.ForPlace(request, facts));
        if (!confirmed)
        {
            _logger.LogInformation("Order dialog: user cancelled the confirmation; nothing placed");
            return null;
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

        _logger.LogInformation("Order dialog: placed {OrderId} ({Status})", order.Id, order.Status);
        return order;
    }

    /// <summary>
    /// Best-effort teardown of the legs a placement attempt already put on
    /// the surface while its partner leg refused: each cancel runs detached
    /// from the failure path (a refusing cancel logs, never masks the
    /// original failure), so no half pair stays working.
    /// </summary>
    private async Task CancelPlacedLegsAsync(IReadOnlyList<Order> placed)
    {
        foreach (var order in placed)
        {
            try
            {
                await _trading.CancelOrderAsync(order.Symbol, order.Id);
                _logger.LogInformation(
                    "Order dialog: cancelled {OrderId} while aborting the incomplete OCO pair",
                    order.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Order dialog: failed to cancel {OrderId} while aborting the incomplete OCO pair",
                    order.Id);
            }
        }
    }

    /// <summary>
    /// The ticket field a build failure belongs to (T-156): the inline
    /// error's routing key. <see cref="None"/> marks a flow-level failure
    /// (a market-relative sanity reading, an invalid built request) that no
    /// single field owns — the summary line's resident.
    /// </summary>
    private enum TicketField
    {
        None,
        Symbol,
        Quantity,
        Price,
        Trigger,
        Trail,
        OcoStopLimit,
        BracketTakeProfit,
    }

    /// <summary>Whether an order-building attempt produced requests at all.</summary>
    /// <param name="Requests">The built requests, empty on a failed attempt.</param>
    /// <param name="Error">The localized build error of a failed attempt.</param>
    /// <param name="Field">The field the failure belongs to (T-156): a
    /// field-tagged failure renders next to its field, an untagged one stays
    /// on the summary line. The parse sequence and every message are
    /// untouched — only the display surface of a failure moves.</param>
    private sealed record RequestBuild(IReadOnlyList<OrderRequest> Requests, string? Error, TicketField Field = TicketField.None)
    {
        /// <summary>True when the inputs failed to build requests.</summary>
        public bool Failed => Error is not null;

        /// <summary>
        /// True when the single request is a bracket entry (T-186): the
        /// entry carries its exits to the adapter, so the submit pipeline
        /// runs the bracket's own atomic gate instead of the per-request
        /// loop (there is no partial pair to tear down — the exits are
        /// never placed separately).
        /// </summary>
        public bool IsBracket { get; private init; }

        /// <summary>A successful attempt over the given requests.</summary>
        public static RequestBuild Ok(IReadOnlyList<OrderRequest> requests) => new(requests, null);

        /// <summary>A successful bracket attempt: the ONE entry request carrying its exits (T-186).</summary>
        public static RequestBuild OkBracket(OrderRequest entry) => new([entry], null) { IsBracket = true };

        /// <summary>A failed attempt with its localized error.</summary>
        public static RequestBuild Fail(string error) => new([], error);

        /// <summary>A failed attempt with its localized error, tagged with the field that owns it (T-156).</summary>
        public static RequestBuild Fail(string error, TicketField field) => new([], error, field);
    }

    /// <summary>
    /// Parse the dialog inputs into the order requests: ONE request for every
    /// single type, TWO either-or legs in the OCO mode (a take-profit limit
    /// and a stop-loss stop-limit sharing a fresh group id), and ONE bracket
    /// entry in the bracket mode (T-186: the entry carries its protective
    /// exits). Conditional
    /// shapes resolve a market reference rate first: the trailing stop derives
    /// its trigger seed from it, every stop trigger and take-profit level is
    /// checked against the market direction it must sit at. Every unparsable
    /// or market-inverted parameter is a localized error — never a defaulted
    /// number (the trading-safety rule: no silent substitution).
    /// </summary>
    private async Task<RequestBuild> BuildRequestsAsync()
    {
        var rawSymbol = SymbolInput.Trim();
        if (rawSymbol.Length == 0)
        {
            return RequestBuild.Fail(_localization.GetString(SymbolMissingKey), TicketField.Symbol);
        }

        Symbol symbol;
        try
        {
            symbol = Symbol.Parse(rawSymbol);
        }
        catch (ArgumentException)
        {
            return RequestBuild.Fail(_localization.GetString(SymbolInvalidKey, rawSymbol), TicketField.Symbol);
        }

        // Empty (or whitespace) means MISSING, not unparsable: "not a
        // number" with a blank argument would dangle. The display argument
        // is trimmed so the message never shows padding.
        if (!TryParsePositiveDecimal(
                QuantityInput,
                QuantityRequiredKey,
                QuantityNotANumberKey,
                out var quantity,
                out var quantityError))
        {
            return RequestBuild.Fail(quantityError, TicketField.Quantity);
        }

        var side = (OrderSide)SelectedSideIndex;
        var typeIndex = SelectedTypeIndex;
        if (typeIndex == OcoTypeIndex)
        {
            return await BuildOcoPairAsync(symbol, side, quantity);
        }

        if (typeIndex == BracketTypeIndex)
        {
            return await BuildBracketAsync(symbol, side, quantity);
        }

        if (typeIndex < 0 || typeIndex >= TypeValues.Length)
        {
            return RequestBuild.Fail(_localization.GetString(InvalidParametersKey, typeIndex));
        }

        return TypeValues[typeIndex] switch
        {
            OrderType.Market => RequestBuild.Ok([Request(symbol, side, OrderType.Market, quantity)]),

            OrderType.Limit => BuildLimitCore(symbol, side, quantity),

            OrderType.StopLimit => await BuildStopLimitCoreAsync(symbol, side, quantity),

            OrderType.TrailingStop => await BuildTrailingStopCoreAsync(symbol, side, quantity),

            _ => RequestBuild.Fail(_localization.GetString(InvalidParametersKey, typeIndex)),
        };
    }

    /// <summary>A plain order request (no conditional shape carries extra prices).</summary>
    private static OrderRequest Request(
        Symbol symbol,
        OrderSide side,
        OrderType type,
        decimal quantity) => new()
        {
            Symbol = symbol,
            Side = side,
            Type = type,
            Quantity = new Quantity(quantity),
        };

    /// <summary>A limit-order request: the limit price is part of the shape.</summary>
    private static OrderRequest LimitRequest(
        Symbol symbol,
        OrderSide side,
        decimal quantity,
        Price limit) => new()
        {
            Symbol = symbol,
            Side = side,
            Type = OrderType.Limit,
            Quantity = new Quantity(quantity),
            Price = limit,
        };

    /// <summary>A stop-limit request: the trigger arms it, the limit protects it.</summary>
    private static OrderRequest StopLimitRequest(
        Symbol symbol,
        OrderSide side,
        decimal quantity,
        Price trigger,
        Price limit) => new()
        {
            Symbol = symbol,
            Side = side,
            Type = OrderType.StopLimit,
            Quantity = new Quantity(quantity),
            Price = limit,
            StopPrice = trigger,
        };

    /// <summary>A trailing-stop request: the trigger seed arms it, the distance drags it.</summary>
    private static OrderRequest TrailingStopRequest(
        Symbol symbol,
        OrderSide side,
        decimal quantity,
        Price seed,
        decimal trail) => new()
        {
            Symbol = symbol,
            Side = side,
            Type = OrderType.TrailingStop,
            Quantity = new Quantity(quantity),
            StopPrice = seed,
            TrailDistance = new Price(trail),
        };

    /// <summary>The take-profit limit request of an OCO pair (the group id shared).</summary>
    private static OrderRequest OcoLimitLeg(
        Symbol symbol,
        OrderSide side,
        decimal quantity,
        Price takeProfit,
        string ocoGroupId) => new()
        {
            Symbol = symbol,
            Side = side,
            Type = OrderType.Limit,
            Quantity = new Quantity(quantity),
            Price = takeProfit,
            OcoGroupId = ocoGroupId,
        };

    /// <summary>The stop-loss stop-limit request of an OCO pair (the group id shared).</summary>
    private static OrderRequest OcoStopLimitLeg(
        Symbol symbol,
        OrderSide side,
        decimal quantity,
        Price trigger,
        Price limit,
        string ocoGroupId) => new()
        {
            Symbol = symbol,
            Side = side,
            Type = OrderType.StopLimit,
            Quantity = new Quantity(quantity),
            Price = limit,
            StopPrice = trigger,
            OcoGroupId = ocoGroupId,
        };

    /// <summary>The limit-order parse: a required, positive invariant-culture limit price.</summary>
    private RequestBuild BuildLimitCore(Symbol symbol, OrderSide side, decimal quantity)
    {
        if (!TryParsePositiveDecimal(
                PriceInput,
                PriceRequiredKey,
                PriceNotANumberKey,
                out var price,
                out var error))
        {
            return RequestBuild.Fail(error, TicketField.Price);
        }

        return RequestBuild.Ok([LimitRequest(symbol, side, quantity, new Price(price))]);
    }

    /// <summary>
    /// The stop-limit parse: trigger and limit, then the market-relative
    /// sanity check — a stop arms strictly beyond the market (sell below,
    /// buy above), so an immediately-triggering stop is an explicit dialog
    /// error instead of an adapter refusal on a raw exception trail.
    /// </summary>
    private async Task<RequestBuild> BuildStopLimitCoreAsync(Symbol symbol, OrderSide side, decimal quantity)
    {
        if (!TryParsePositiveDecimal(
                PriceInput,
                PriceRequiredKey,
                PriceNotANumberKey,
                out var limitPrice,
                out var limitError))
        {
            return RequestBuild.Fail(limitError, TicketField.Price);
        }

        if (!TryParsePositiveDecimal(
                StopTriggerInput,
                TriggerRequiredKey,
                TriggerNotANumberKey,
                out var trigger,
                out var triggerError))
        {
            return RequestBuild.Fail(triggerError, TicketField.Trigger);
        }

        var referenceClose = await MarketReference.GetLatestCloseAsync(_trading, symbol, CancellationToken.None);
        var marketError = CheckStopTriggerDirection(side, new Price(trigger), referenceClose);
        if (marketError is not null)
        {
            return RequestBuild.Fail(marketError);
        }

        return RequestBuild.Ok([StopLimitRequest(symbol, side, quantity, new Price(trigger), new Price(limitPrice))]);
    }

    /// <summary>
    /// The trailing-stop parse: the trail distance plus the seed derivation —
    /// one trail below the market for sells (refused when the distance eats
    /// the whole price), one above for buys.
    /// </summary>
    private async Task<RequestBuild> BuildTrailingStopCoreAsync(Symbol symbol, OrderSide side, decimal quantity)
    {
        if (!TryParsePositiveDecimal(
                TrailDistanceInput,
                TrailRequiredKey,
                TrailNotANumberKey,
                out var trail,
                out var trailError))
        {
            return RequestBuild.Fail(trailError, TicketField.Trail);
        }

        var referenceClose = await MarketReference.GetLatestCloseAsync(_trading, symbol, CancellationToken.None);

        // The seed must stay a positive price: for a sell it is one trail
        // BELOW the market, so a trail at or beyond the whole reference close
        // would seed a non-positive (non-parsable) price — refused, never a
        // default.
        if (side == OrderSide.Sell && trail >= referenceClose.Value)
        {
            return RequestBuild.Fail(
                _localization.GetString(TrailTooLargeKey, trail, referenceClose.Value),
                TicketField.Trail);
        }

        var seed = side == OrderSide.Sell
            ? referenceClose.Value - trail
            : referenceClose.Value + trail;

        return RequestBuild.Ok([TrailingStopRequest(symbol, side, quantity, new Price(seed), trail)]);
    }

    /// <summary>
    /// The OCO pair parse: shared symbol, side and quantity; the take-profit
    /// limit and the stop-loss stop-limit price their legs from the matching
    /// fields; both derive and check against the market reference rate. The
    /// legs share a fresh either-or group id, the take-profit leg placed
    /// first.
    /// </summary>
    private async Task<RequestBuild> BuildOcoPairAsync(Symbol symbol, OrderSide side, decimal quantity)
    {
        if (!TryParsePositiveDecimal(
                PriceInput,
                PriceRequiredKey,
                PriceNotANumberKey,
                out var takeProfit,
                out var takeProfitError))
        {
            return RequestBuild.Fail(takeProfitError, TicketField.Price);
        }

        if (!TryParsePositiveDecimal(
                StopTriggerInput,
                TriggerRequiredKey,
                TriggerNotANumberKey,
                out var stopTrigger,
                out var stopTriggerError))
        {
            return RequestBuild.Fail(stopTriggerError, TicketField.Trigger);
        }

        if (!TryParsePositiveDecimal(
                OcoStopLimitInput,
                PriceRequiredKey,
                PriceNotANumberKey,
                out var stopLimitPrice,
                out var stopLimitError))
        {
            return RequestBuild.Fail(stopLimitError, TicketField.OcoStopLimit);
        }

        var referenceClose = await MarketReference.GetLatestCloseAsync(_trading, symbol, CancellationToken.None);

        // The take-profit limit must rest beyond the market it exits into,
        // the stop trigger must arm strictly beyond it — the market-relative
        // sanity readings of a placed pair.
        var triggerError = CheckStopTriggerDirection(side, new Price(stopTrigger), referenceClose);
        if (triggerError is not null)
        {
            return RequestBuild.Fail(triggerError);
        }

        var limitError = CheckTakeProfitDirection(side, new Price(takeProfit), referenceClose);
        if (limitError is not null)
        {
            return RequestBuild.Fail(limitError);
        }

        var groupId = $"{OcoGroupIdPrefix}{Guid.NewGuid().ToString("N")}";
        return RequestBuild.Ok(
        [
            OcoLimitLeg(symbol, side, quantity, new Price(takeProfit), groupId),
            OcoStopLimitLeg(symbol, side, quantity, new Price(stopTrigger), new Price(stopLimitPrice), groupId),
        ]);
    }

    /// <summary>
    /// The bracket parse (T-186): the entry kind decides whether the entry
    /// prices itself (a limit entry, the shared price field) or places at
    /// the market; the take-profit limit, the stop trigger and the
    /// stop-loss limit price the exits from their fields; both exits are
    /// checked against the market reference on THEIR OWN side (they are the
    /// opposite side of the entry — the take-profit rests beyond the market
    /// it exits into, the stop trigger arms strictly beyond it). The exits
    /// share a fresh either-or group id and ride the entry as ONE request;
    /// the take-profit leg is listed first (the pair order).
    /// </summary>
    private async Task<RequestBuild> BuildBracketAsync(Symbol symbol, OrderSide side, decimal quantity)
    {
        decimal? entryPrice = null;
        if (IsBracketEntryLimit)
        {
            if (!TryParsePositiveDecimal(
                    PriceInput,
                    PriceRequiredKey,
                    PriceNotANumberKey,
                    out var entryLimit,
                    out var entryError))
            {
                return RequestBuild.Fail(entryError, TicketField.Price);
            }

            entryPrice = entryLimit;
        }

        if (!TryParsePositiveDecimal(
                BracketTakeProfitInput,
                PriceRequiredKey,
                PriceNotANumberKey,
                out var takeProfit,
                out var takeProfitError))
        {
            return RequestBuild.Fail(takeProfitError, TicketField.BracketTakeProfit);
        }

        if (!TryParsePositiveDecimal(
                StopTriggerInput,
                TriggerRequiredKey,
                TriggerNotANumberKey,
                out var stopTrigger,
                out var stopTriggerError))
        {
            return RequestBuild.Fail(stopTriggerError, TicketField.Trigger);
        }

        if (!TryParsePositiveDecimal(
                OcoStopLimitInput,
                PriceRequiredKey,
                PriceNotANumberKey,
                out var stopLimitPrice,
                out var stopLimitError))
        {
            return RequestBuild.Fail(stopLimitError, TicketField.OcoStopLimit);
        }

        var referenceClose = await MarketReference.GetLatestCloseAsync(_trading, symbol, CancellationToken.None);

        // The exits close the entry: they ride the OPPOSITE side, so the
        // market-relative sanity readings run on the exit's own side — the
        // same readings the OCO pair takes.
        var exitSide = side == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy;

        var triggerError = CheckStopTriggerDirection(exitSide, new Price(stopTrigger), referenceClose);
        if (triggerError is not null)
        {
            return RequestBuild.Fail(triggerError);
        }

        var limitError = CheckTakeProfitDirection(exitSide, new Price(takeProfit), referenceClose);
        if (limitError is not null)
        {
            return RequestBuild.Fail(limitError);
        }

        var groupId = $"{OcoGroupIdPrefix}{Guid.NewGuid().ToString("N")}";
        var entry = entryPrice is { } entryLimitValue
            ? LimitRequest(symbol, side, quantity, new Price(entryLimitValue))
            : Request(symbol, side, OrderType.Market, quantity);

        return RequestBuild.OkBracket(entry with
        {
            BracketLegs = new BracketLegs
            {
                TakeProfit = OcoLimitLeg(symbol, exitSide, quantity, new Price(takeProfit), groupId),
                StopLoss = OcoStopLimitLeg(symbol, exitSide, quantity, new Price(stopTrigger), new Price(stopLimitPrice), groupId),
            },
        });
    }

    /// <summary>
    /// Market-relative reading of a stop trigger: it must arm strictly
    /// beyond the market (a sell stop below it, a buy stop above), or the
    /// order would trigger immediately — a localized dialog error, never a
    /// degraded execution.
    /// </summary>
    private string? CheckStopTriggerDirection(OrderSide side, Price trigger, Price referenceClose) =>
        (side, trigger.Value.CompareTo(referenceClose.Value)) switch
        {
            (OrderSide.Sell, >= 0) => _localization.GetString(SellTriggerBelowMarketKey, trigger.Value, referenceClose.Value),
            (OrderSide.Buy, <= 0) => _localization.GetString(BuyTriggerAboveMarketKey, trigger.Value, referenceClose.Value),
            _ => null,
        };

    /// <summary>
    /// Market-relative reading of a take-profit limit: it must rest beyond
    /// the market it exits into (a sell exit above it, a buy exit below) so
    /// the leg works as a resting risk-free-profit exit, not an instant
    /// crossing fill.
    /// </summary>
    private string? CheckTakeProfitDirection(OrderSide side, Price takeProfit, Price referenceClose) =>
        (side, takeProfit.Value.CompareTo(referenceClose.Value)) switch
        {
            (OrderSide.Sell, <= 0) => _localization.GetString(SellLimitAboveMarketKey, takeProfit.Value, referenceClose.Value),
            (OrderSide.Buy, >= 0) => _localization.GetString(BuyLimitBelowMarketKey, takeProfit.Value, referenceClose.Value),
            _ => null,
        };

    /// <summary>
    /// Parse a positive decimal field: empty (whitespace) is REQUIRED-not-parsed;
    /// unparsable or non-positive is a not-a-number error, the raw text shown.
    /// Never a default.
    /// </summary>
    private bool TryParsePositiveDecimal(
        string rawInput,
        string requiredKey,
        string notANumberKey,
        out decimal value,
        [NotNullWhen(false)] out string? error)
    {
        value = 0m;
        var raw = rawInput.Trim();
        if (raw.Length == 0)
        {
            error = _localization.GetString(requiredKey);
            return false;
        }

        if (!decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out value)
            || value <= 0)
        {
            error = _localization.GetString(notANumberKey, raw);
            return false;
        }

        error = null;
        return true;
    }

    #endregion Private Methods
}

