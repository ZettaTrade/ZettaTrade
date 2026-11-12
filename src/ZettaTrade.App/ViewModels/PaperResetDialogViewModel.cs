namespace ZettaTrade.App.ViewModels;

using System.Globalization;
using Livery.Localization;
using ReactiveUI;

/// <summary>
/// Paper reset dialog view model (T-192). Collects the starting balance the
/// confirmed reset will restart the simulation at: the input is prefilled
/// with the persisted choice, a non-positive or unparsable entry is an
/// inline localized refusal (never a defaulted value), and the parsed
/// <see cref="ChosenBalance"/> hands back to the panel through the dialog
/// window. The reset pipeline itself (the fresh plan read, the one
/// confirmation, the wipes) runs in <see cref="Services.PaperResetService"/>;
/// this VM only parses and refuses. The VM is not a localization surface —
/// the summary precomputes at <see cref="Initialize"/> time, the same
/// load-time precompute the protective-levels dialog applies.
/// </summary>
public sealed class PaperResetDialogViewModel : ReactiveObject
{
    #region Resx Keys

    private const string SummaryKey = "Trading.PaperResetSummary";
    private const string NotANumberKey = "Trading.PaperResetNotANumber";

    /// <summary>Fraction format of the prefilled balance text: the trimmed
    /// money-like shape the amend dialog's prefill uses.</summary>
    private const string PrefillBalanceFormat = "0.00########";

    #endregion Resx Keys

    private readonly ILocalizationManager _localization;

    /// <summary>Create a paper reset dialog view model (transient per open).</summary>
    public PaperResetDialogViewModel(ILocalizationManager localization)
    {
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
    }

    #region Properties

    private string _balanceInput = string.Empty;
    private string _summary = string.Empty;
    private string? _errorMessage;

    /// <summary>Starting balance text, in the invariant culture; prefilled from the persisted choice.</summary>
    public string BalanceInput
    {
        get => _balanceInput;
        set => this.RaiseAndSetIfChanged(ref _balanceInput, value);
    }

    /// <summary>One-line summary of what the reset does, precomputed at initialize time.</summary>
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

    /// <summary>The parsed starting balance, or null when closed without an applied choice.</summary>
    public decimal? ChosenBalance { get; private set; }

    #endregion Properties

    #region Public Methods

    /// <summary>Prefill the dialog for one open: the balance input seeds with
    /// the persisted starting balance (the honest stored choice, never a
    /// fabricated figure) and the summary composes once. Called before the
    /// window shows.</summary>
    /// <param name="currentBalance">The persisted starting balance to prefill.</param>
    public void Initialize(decimal currentBalance)
    {
        BalanceInput = currentBalance.ToString(PrefillBalanceFormat, CultureInfo.InvariantCulture);
        Summary = _localization.GetString(SummaryKey);
    }

    /// <summary>
    /// Parse the balance input into the reset's choice. The input must parse
    /// as a strictly positive number; anything else (empty, unparsable,
    /// zero, negative) is an inline localized refusal and the dialog stays
    /// open — never a substituted default.
    /// </summary>
    /// <returns>True when <see cref="ChosenBalance"/> carries the parsed choice.</returns>
    public bool TryBuildChoice()
    {
        var raw = BalanceInput.Trim();
        if (!decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var balance)
            || balance <= decimal.Zero)
        {
            ErrorMessage = _localization.GetString(NotANumberKey, raw);
            return false;
        }

        ChosenBalance = balance;
        return true;
    }

    #endregion Public Methods
}
