namespace ZettaTrade.App.ViewModels;

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Livery.Localization;
using ReactiveUI;
using ZettaTrade.App.Services;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// Amend order dialog view model (T-075). Collects the user's edits over a
/// working order — the quantity, and the limit price for the
/// limit-bearing types (a trailing stop amends by quantity only: its
/// displayed price is the ratcheting trigger, not an editable level) — and
/// hands a parsed <see cref="OrderAmendRequest"/> back to the panel. The
/// pipeline itself (state re-read, risk, confirmation, replace) runs in
/// <see cref="OrderAmendService"/>; this VM only parses and refuses
/// unparsable input with a localized error line, never a defaulted value.
/// Display facts of the editing target (side, quantity, price, status) are
/// precomputed at <see cref="Initialize"/> time — the same load-time
/// precompute the orders panel rows apply; the VM is not a localization
/// surface.
/// </summary>
public sealed class AmendOrderDialogViewModel : ReactiveObject
{
    #region Resx Keys

    private const string SummaryKey = "Positions.AmendSummary";
    private const string QuantityRequiredKey = "Positions.AmendQuantityRequired";
    private const string QuantityInvalidKey = "Positions.AmendQuantityInvalid";
    private const string PriceInvalidKey = "Positions.AmendPriceInvalid";

    /// <summary>Fraction format of the prefilled price text: the trimmed
    /// money-like shape the new-order dialog's prefill uses.</summary>
    private const string PrefillPriceFormat = "0.00########";

    #endregion Resx Keys

    private readonly ILocalizationManager _localization;

    /// <summary>The working order the dialog edits; assigned by <see cref="Initialize"/> before the window shows.</summary>
    private Order _working = null!;

    /// <summary>Create an amend order dialog view model (transient per open).</summary>
    public AmendOrderDialogViewModel(ILocalizationManager localization)
    {
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
    }

    #region Properties

    private string _quantityInput = string.Empty;
    private string _priceInput = string.Empty;
    private bool _isPriceEditable;
    private string _summary = string.Empty;
    private string? _errorMessage;

    /// <summary>Quantity text, in the invariant culture; prefilled from the order.</summary>
    public string QuantityInput
    {
        get => _quantityInput;
        set => this.RaiseAndSetIfChanged(ref _quantityInput, value);
    }

    /// <summary>Limit price text, in the invariant culture; empty keeps the original price.
    /// The field is hidden (and its content ignored) for the types without an editable price.</summary>
    public string PriceInput
    {
        get => _priceInput;
        set => this.RaiseAndSetIfChanged(ref _priceInput, value);
    }

    /// <summary>True when the order type carries an editable limit price.</summary>
    public bool IsPriceEditable
    {
        get => _isPriceEditable;
        private set => this.RaiseAndSetIfChanged(ref _isPriceEditable, value);
    }

    /// <summary>One-line summary of the editing target, precomputed at initialize time.</summary>
    public string Summary
    {
        get => _summary;
        private set => this.RaiseAndSetIfChanged(ref _summary, value);
    }

    /// <summary>Last parse error (localized); drives the error line visibility.</summary>
    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => this.RaiseAndSetIfChanged(ref _errorMessage, value);
    }

    /// <summary>True while <see cref="ErrorMessage"/> holds text.</summary>
    public bool HasErrorMessage => _errorMessage is not null;

    #endregion Properties

    #region Public Methods

    /// <summary>
    /// Point the dialog at the working order and prefill the fields: the
    /// values the panel displays now, before any edit. Called once before
    /// the window shows; the prefill is this open's deliberate seed, like
    /// the new-order dialog's book-level prefill.
    /// </summary>
    /// <param name="working">The working order the panel is amending.</param>
    public void Initialize(Order working)
    {
        _working = working ?? throw new ArgumentNullException(nameof(working));

        IsPriceEditable = working.Type is OrderType.Limit or OrderType.StopLimit;
        QuantityInput = working.Quantity.Value.ToString(CultureInfo.InvariantCulture);
        PriceInput = IsPriceEditable && working.Price is { } price
            ? price.Value.ToString(PrefillPriceFormat, CultureInfo.InvariantCulture)
            : string.Empty;

        var sideText = _localization.GetString($"Order.{working.Side}");
        var quantityText = working.Quantity.Value.ToString(CultureInfo.InvariantCulture);
        var priceText = working.Price is { } priceState
            ? priceState.Value.ToString("F8", CultureInfo.InvariantCulture)
            : string.Empty;
        var statusText = _localization.GetString($"Positions.Status{working.Status}");
        Summary = _localization.GetString(
            SummaryKey,
            sideText,
            quantityText,
            working.Symbol.Normalized,
            priceText,
            statusText);
    }

    /// <summary>
    /// Parse the dialog inputs into an amend request. The quantity is
    /// required and strictly positive; the price may stay empty to keep the
    /// original limit price, and is otherwise a strictly positive number;
    /// for a type without an editable price the field content is ignored by
    /// construction (the row is hidden). An unparsable input reports a
    /// localized error line and returns null — never a substituted default.
    /// </summary>
    /// <returns>The parsed request, or null when the input is unparsable.</returns>
    public OrderAmendRequest? TryBuildEdits()
    {
        if (!TryParsePositiveDecimal(
                QuantityInput,
                QuantityRequiredKey,
                QuantityInvalidKey,
                out var quantity))
        {
            return null;
        }

        decimal? limitPrice = null;
        if (IsPriceEditable)
        {
            if (!TryParseOptionalPositiveDecimal(PriceInput, PriceInvalidKey, out limitPrice))
            {
                return null;
            }
        }

        return new OrderAmendRequest
        {
            Working = _working,
            NewQuantity = quantity,
            NewLimitPrice = limitPrice,
        };
    }

    #endregion Public Methods

    #region Private Methods

    /// <summary>Parse a required, strictly positive decimal field; an unparsable one reports the key and fails.</summary>
    private bool TryParsePositiveDecimal(string rawInput, string requiredKey, string invalidKey, out decimal value)
    {
        value = 0m;
        var raw = rawInput.Trim();
        if (raw.Length == 0)
        {
            ErrorMessage = _localization.GetString(requiredKey);
            return false;
        }

        if (!decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out value) || value <= 0)
        {
            value = 0m;
            return FailNotANumber(invalidKey, raw);
        }

        return true;
    }

    /// <summary>
    /// Parse an optional, strictly positive decimal field: empty keeps the
    /// original (an explicit null out), non-empty must parse positive.
    /// </summary>
    private bool TryParseOptionalPositiveDecimal(string rawInput, string invalidKey, out decimal? value)
    {
        value = null;
        var raw = rawInput.Trim();
        if (raw.Length == 0)
        {
            return true;
        }

        if (!decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var price)
            || price <= 0)
        {
            return FailNotANumber(invalidKey, raw);
        }

        value = price;
        return true;
    }

    private bool FailNotANumber(string invalidKey, string raw)
    {
        ErrorMessage = _localization.GetString(invalidKey, raw);
        return false;
    }

    #endregion Private Methods
}
