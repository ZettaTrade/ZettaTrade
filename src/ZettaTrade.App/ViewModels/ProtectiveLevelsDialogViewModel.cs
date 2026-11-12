namespace ZettaTrade.App.ViewModels;

using System.Globalization;
using Livery.Localization;
using ReactiveUI;
using ZettaTrade.App.Services;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;

/// <summary>
/// Protective-levels dialog view model (T-187). Collects the user's edits
/// over one open position — the stop-loss and take-profit levels, each
/// optional: an empty field clears that level, both empty clear the pair —
/// and hands a parsed <see cref="ProtectiveLevelsEdits"/> back to the panel.
/// The pipeline itself (the fresh state re-read, the risk gates, the one
/// confirmation, the cancel+place rewrite) runs in
/// <see cref="ProtectiveLevelsService"/>; this VM only parses and refuses
/// unparsable or misdirected input with a localized error line, never a
/// defaulted value. The direction check runs against the CURRENT price the
/// position carried at open time — a level on the wrong side either fires
/// at once or never arms, so both readings are inline refusals here and the
/// service re-checks against the fresh price before anything moves. Display
/// facts of the editing target are precomputed at <see cref="Initialize"/>
/// time — the same load-time precompute the amend dialog applies; the VM is
/// not a localization surface.
/// </summary>
public sealed class ProtectiveLevelsDialogViewModel : ReactiveObject
{
    #region Resx Keys

    private const string SummaryKey = "Positions.ProtectSummary";
    private const string StopLossNotANumberKey = "Positions.ProtectStopNotANumber";
    private const string TakeProfitNotANumberKey = "Positions.ProtectTakeProfitNotANumber";
    private const string StopBelowMarketKey = "Positions.ProtectStopMustBeBelowMarket";
    private const string StopAboveMarketKey = "Positions.ProtectStopMustBeAboveMarket";
    private const string TakeProfitAboveMarketKey = "Positions.ProtectTakeProfitMustBeAboveMarket";
    private const string TakeProfitBelowMarketKey = "Positions.ProtectTakeProfitMustBeBelowMarket";

    /// <summary>Fraction format of the prefilled level text: the trimmed
    /// money-like shape the amend dialog's prefill uses.</summary>
    private const string PrefillLevelFormat = "0.00########";

    /// <summary>Fraction format of the direction errors' market price.</summary>
    private const string MarketPriceFormat = "F8";

    #endregion Resx Keys

    private readonly ILocalizationManager _localization;

    /// <summary>The position the dialog edits; assigned by <see cref="Initialize"/> before the window shows.</summary>
    private Position _position = null!;

    /// <summary>Create a protective-levels dialog view model (transient per open).</summary>
    public ProtectiveLevelsDialogViewModel(ILocalizationManager localization)
    {
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
    }

    #region Properties

    private string _stopLossInput = string.Empty;
    private string _takeProfitInput = string.Empty;
    private string _summary = string.Empty;
    private string? _errorMessage;

    /// <summary>Stop-loss level text, in the invariant culture; prefilled from the position.</summary>
    public string StopLossInput
    {
        get => _stopLossInput;
        set => this.RaiseAndSetIfChanged(ref _stopLossInput, value);
    }

    /// <summary>Take-profit level text, in the invariant culture; prefilled from the position.</summary>
    public string TakeProfitInput
    {
        get => _takeProfitInput;
        set => this.RaiseAndSetIfChanged(ref _takeProfitInput, value);
    }

    /// <summary>One-line summary of the editing target, precomputed at initialize time.</summary>
    public string Summary
    {
        get => _summary;
        private set => this.RaiseAndSetIfChanged(ref _summary, value);
    }

    /// <summary>Last parse or direction error (localized); drives the error line visibility.</summary>
    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => this.RaiseAndSetIfChanged(ref _errorMessage, value);
    }

    /// <summary>True while <see cref="ErrorMessage"/> holds text.</summary>
    public bool HasErrorMessage => _errorMessage is not null;

    #endregion Properties

    #region Public Methods

    /// <summary>Point the dialog at the open position and prefill the fields with the
    /// levels the position reads now (a level the venue does not report
    /// prefills empty — the honest absent state, never a fabricated number).
    /// Called once before the window shows; the prefill is this open's
    /// deliberate seed, like the amend dialog's. An undefined side never
    /// opens the dialog: the direction matrix cannot derive rules for it,
    /// so it is an explicit refusal (trading safety: never substitute).</summary>
    /// <param name="position">The open position the panel is protecting.</param>
    public void Initialize(Position position)
    {
        _position = position ?? throw new ArgumentNullException(nameof(position));

        if (_position.Side is not (OrderSide.Buy or OrderSide.Sell))
        {
            throw new InvalidOperationException(
                $"Unsupported position side {_position.Side}; the protective dialog cannot derive direction rules for {_position.Symbol.Normalized}.");
        }

        StopLossInput = position.StopLoss is { } stop
            ? stop.Value.ToString(PrefillLevelFormat, CultureInfo.InvariantCulture)
            : string.Empty;
        TakeProfitInput = position.TakeProfit is { } takeProfit
            ? takeProfit.Value.ToString(PrefillLevelFormat, CultureInfo.InvariantCulture)
            : string.Empty;

        Summary = _localization.GetString(
            SummaryKey,
            _localization.GetString($"Order.{position.Side}"),
            position.Quantity.Value.ToString(CultureInfo.InvariantCulture),
            position.Symbol.Normalized,
            position.CurrentPrice.Value.ToString(MarketPriceFormat, CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Parse the dialog inputs into the level edits. Each field may stay
    /// empty (a cleared level — an explicit null) or hold a strictly positive
    /// number; a non-empty unparsable field and a level on the wrong side of
    /// the market report a localized error line and return null — never a
    /// substituted default.
    /// </summary>
    /// <returns>The parsed edits, or null when the input is refused.</returns>
    public ProtectiveLevelsEdits? TryBuildEdits()
    {
        decimal? stopLoss;
        if (!TryParseOptionalPositiveDecimal(StopLossInput, StopLossNotANumberKey, out var parsedStopLoss))
        {
            return null;
        }

        stopLoss = parsedStopLoss;

        if (!TryParseOptionalPositiveDecimal(TakeProfitInput, TakeProfitNotANumberKey, out var parsedTakeProfit))
        {
            return null;
        }

        var directionError = FindDirectionError(stopLoss, parsedTakeProfit);
        if (directionError is not null)
        {
            ErrorMessage = directionError;
            return null;
        }

        return new ProtectiveLevelsEdits
        {
            StopLoss = stopLoss,
            TakeProfit = parsedTakeProfit,
        };
    }

    #endregion Public Methods

    #region Private Methods

    /// <summary>
    /// The dialog-time direction matrix, the same shape the service
    /// re-checks against the fresh price: for a long the stop-loss must sit
    /// strictly below and the take-profit strictly above the current price;
    /// for a short mirrored. A level AT the market is a refusal too (it
    /// either refuses to arm or fills at placement), and an undefined side
    /// never derives a rule.
    /// </summary>
    private string? FindDirectionError(decimal? stopLoss, decimal? takeProfit)
    {
        var current = _position.CurrentPrice.Value;

        if (stopLoss is { } stop)
        {
            var wrong = _position.Side == OrderSide.Buy
                ? stop >= current
                : stop <= current;
            if (wrong)
            {
                return _localization.GetString(
                    _position.Side == OrderSide.Buy ? StopBelowMarketKey : StopAboveMarketKey,
                    MarketText());
            }
        }

        if (takeProfit is { } takeProfitLevel)
        {
            var wrong = _position.Side == OrderSide.Buy
                ? takeProfitLevel <= current
                : takeProfitLevel >= current;
            if (wrong)
            {
                return _localization.GetString(
                    _position.Side == OrderSide.Buy ? TakeProfitAboveMarketKey : TakeProfitBelowMarketKey,
                    MarketText());
            }
        }

        return null;
    }

    /// <summary>The current price the direction errors name, F8 invariant.</summary>
    private string MarketText() =>
        _position.CurrentPrice.Value.ToString(MarketPriceFormat, CultureInfo.InvariantCulture);

    /// <summary>
    /// Parse an optional, strictly positive decimal field: empty keeps the
    /// level cleared (an explicit null out), non-empty must parse positive.
    /// </summary>
    private bool TryParseOptionalPositiveDecimal(string rawInput, string invalidKey, out decimal? value)
    {
        value = null;
        var raw = rawInput.Trim();
        if (raw.Length == 0)
        {
            return true;
        }

        if (!decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var level)
            || level <= 0)
        {
            ErrorMessage = _localization.GetString(invalidKey, raw);
            return false;
        }

        value = level;
        return true;
    }

    #endregion Private Methods
}
