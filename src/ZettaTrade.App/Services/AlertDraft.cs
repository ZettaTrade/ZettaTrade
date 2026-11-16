namespace ZettaTrade.App.Services;

/// <summary>
/// The editor's submission shape (T-096): the draft the manager hands the
/// engine for a create or an edit (an edit re-arms). The engine validates it
/// against <see cref="AlertEdge"/>-exact semantics before it ever becomes a
/// definition — an invalid draft is an error, never a silently coerced
/// alert.
/// </summary>
/// <param name="SymbolName">The normalized symbol the alert evaluates.</param>
/// <param name="Kind">Which evaluation the alert performs.</param>
/// <param name="Trigger">The Price kind's edge trigger, the IndicatorCross and FundingCross kinds' cross direction, and the WhalePrint kind's fixed Above reading (T-245); unused (neutral slot) for the PercentMove kind.</param>
/// <param name="TargetPrice">The Price kind's level, the FundingCross kind's signed threshold in PERCENT units (T-240), and the WhalePrint kind's notional threshold in absolute quote-currency units (T-245); unused (zero) for the PercentMove kind.</param>
/// <param name="ThresholdPercent">The PercentMove kind's percent threshold (non-negative); unused (zero) for the Price kind.</param>
/// <param name="WindowMinutes">The PercentMove kind's trailing window in minutes; unused (zero) for the Price kind.</param>
/// <param name="Note">The user's free-text note; empty when left off.</param>
/// <param name="Indicator">The IndicatorCross kind's facts (T-203); null for the other kinds.</param>
/// <param name="ReArm">The fire's aftermath (T-204, <see cref="AlertReArmMode"/>); the terminal one-shot unless stated.</param>
/// <param name="CooldownMinutes">The Cooldown re-arm's window in minutes; unused (zero) for the other modes.</param>
/// <param name="Sound">The fire's sound choice (T-204); the beep unless stated.</param>
public sealed record AlertDraft(
    string SymbolName,
    PriceAlertKind Kind,
    AlertTrigger Trigger,
    decimal TargetPrice,
    decimal ThresholdPercent,
    int WindowMinutes,
    string Note,
    AlertIndicatorSpec? Indicator = null,
    AlertReArmMode ReArm = AlertReArmMode.OneShot,
    int CooldownMinutes = 0,
    AlertSoundChoice Sound = AlertSoundChoice.Beep)
{
    /// <summary>
    /// Validate the draft against the kind it names: the symbol spelling
    /// must be parsable under this app's exact vocabulary (the seed for the
    /// engine's own refuse-not-default gate), the Price kind requires a
    /// strictly positive target, the PercentMove kind a strictly positive
    /// threshold and window, the IndicatorCross kind (T-203) a present spec
    /// with a named indicator, a strictly positive period and a real cross
    /// edge (At is meaningless for a cross and is refused); any other kind
    /// value is a fault. The re-arm facts (T-204) parse by their exact enum
    /// names and a Cooldown mode demands a strictly positive window. The
    /// spec's level is unconstrained — an oscillator's
    /// canonical level can be 70, a zero-cross's 0, an oscillator band's
    /// negative — only its presence is demanded for the indicator-value
    /// source. Errors are specific, never defaults (the trading-safety canon
    /// reaches every submitted value).
    /// </summary>
    /// <exception cref="ArgumentException">One required fact is missing or malformed for the named kind.</exception>
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(SymbolName);
        if (!IsAParsableSpelling(SymbolName))
        {
            throw new ArgumentException(
                $"The symbol spelling '{SymbolName}' is not parsable under the alerts vocabulary.",
                nameof(SymbolName));
        }

        switch (Kind)
        {
            case PriceAlertKind.Price:
                if (TargetPrice <= 0m)
                {
                    throw new ArgumentException(
                        "A price trigger needs a strictly positive target price.",
                        nameof(TargetPrice));
                }

                break;

            case PriceAlertKind.PercentMove:
                if (ThresholdPercent <= 0m)
                {
                    throw new ArgumentException(
                        "A percent-move trigger needs a strictly positive percent.",
                        nameof(ThresholdPercent));
                }

                if (WindowMinutes <= 0)
                {
                    throw new ArgumentException(
                        "A percent-move trigger needs a strictly positive window.",
                        nameof(WindowMinutes));
                }

                break;

            case PriceAlertKind.FundingCross:
                // The threshold lives on TargetPrice in PERCENT units and any
                // signed value is honest (zero and negatives included — a
                // sub-zero funding regime is exactly what an operator may
                // alert on): a decimal carries no invalid value, only the
                // cross edge below is demanded.
                break;

            case PriceAlertKind.WhalePrint:
                // The threshold lives on TargetPrice in ABSOLUTE quote units
                // (T-245): a print either reaches it or it does not, so zero
                // and negatives are no thresholds at all (everything and
                // nothing would satisfy them) — a strictly positive notional
                // is demanded, the same refusal the price level keeps.
                if (TargetPrice <= 0m)
                {
                    throw new ArgumentException(
                        "A whale-print trigger needs a strictly positive notional threshold.",
                        nameof(TargetPrice));
                }

                break;

            case PriceAlertKind.IndicatorCross:
                if (Indicator is not { } indicator)
                {
                    throw new ArgumentException(
                        "An indicator-cross trigger needs its indicator facts.",
                        nameof(Indicator));
                }

                if (string.IsNullOrWhiteSpace(indicator.Name))
                {
                    throw new ArgumentException(
                        "An indicator-cross trigger needs a named indicator.",
                        nameof(Indicator));
                }

                if (indicator.Period <= 0)
                {
                    throw new ArgumentException(
                        "An indicator-cross trigger needs a strictly positive period.",
                        nameof(Indicator));
                }

                // The level is deliberately unconstrained (an oscillator's 70,
                // a zero-cross's 0, a band's negative): a decimal carries no
                // invalid value, the source demarks whether it is read at all.

                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(Kind), Kind, "The draft names no known alert kind.");
        }

        if (Trigger is not (AlertTrigger.Above or AlertTrigger.Below or AlertTrigger.At))
        {
            throw new ArgumentOutOfRangeException(nameof(Trigger), Trigger, "The draft names no known trigger edge.");
        }

        if (Kind == PriceAlertKind.IndicatorCross && Trigger == AlertTrigger.At)
        {
            throw new ArgumentException(
                "An indicator-cross trigger reads only the Above or Below edge; At is meaningless for a cross.",
                nameof(Trigger));
        }

        if (Kind == PriceAlertKind.FundingCross && Trigger == AlertTrigger.At)
        {
            throw new ArgumentException(
                "A funding-cross trigger reads only the Above or Below edge; At is meaningless for a cross.",
                nameof(Trigger));
        }

        if (Kind == PriceAlertKind.WhalePrint && Trigger is not AlertTrigger.Above)
        {
            throw new ArgumentException(
                "A whale-print trigger reads only the Above edge; a trade alert is 'bigger than', At and Below are meaningless.",
                nameof(Trigger));
        }

        if (!Enum.IsDefined(ReArm))
        {
            throw new ArgumentOutOfRangeException(nameof(ReArm), ReArm, "The draft names no known re-arm mode.");
        }

        if (!Enum.IsDefined(Sound))
        {
            throw new ArgumentOutOfRangeException(nameof(Sound), Sound, "The draft names no known sound choice.");
        }

        if (ReArm == AlertReArmMode.Cooldown && CooldownMinutes <= 0)
        {
            throw new ArgumentException(
                "A cooldown re-arm needs a strictly positive cooldown (minutes).",
                nameof(CooldownMinutes));
        }
    }

    /// <summary>Build the armed definition a fresh create persists: a new identity, the supplied facts, no history.</summary>
    /// <param name="occurredUtc">The UTC creation stamp (a manager-op wall-clock read; never a polling clock).</param>
    /// <returns>The definition as the engine persists and evaluates it.</returns>
    public AlertDefinition ToCreated(DateTimeOffset occurredUtc) => new(
        Guid.NewGuid(),
        SymbolName,
        Kind,
        Trigger,
        TargetPrice,
        ThresholdPercent,
        WindowMinutes,
        Note,
        AlertStatus.Armed,
        occurredUtc,
        SnoozedUntilUtc: null,
        LastFiredUtc: null,
        Indicator: Indicator,
        ReArm: ReArm,
        CooldownMinutes: ReArm == AlertReArmMode.Cooldown ? CooldownMinutes : 0,
        Sound: Sound);

    #region Private Fields

    /// <summary>The normalized spelling's part separator (the two-side crypto spelling).</summary>
    private const char SpellingSeparator = '/';

    /// <summary>The ccxt-style perpetual suffix's separator (T-240: "BASE/QUOTE:SETTLE" — a funding alert needs the settle).</summary>
    private const char SettleMark = ':';

    /// <summary>The one spelling's length ceiling (an asset token never runs longer; the bound is honest, not decorative).</summary>
    private const int MaxSpellingLength = 32;

    #endregion Private Fields

    /// <summary>
    /// The draft-spelling vocabulary (T-096; the settle suffix rides it
    /// since T-240): ONE place the parsable symbol spelling is decided,
    /// because <see cref="ZettaTrade.Core.ValueObjects.Symbol"/> itself
    /// parses leniently (a one-part spelling is a legitimate stock ticker).
    /// A parsable spelling here is at most two non-empty asset tokens of
    /// letters, digits, dots or dashes, optionally carrying ONE ccxt-style
    /// perpetual settle suffix ("BASE/QUOTE:SETTLE" — a funding alert needs
    /// the settle; the suffix is legal on the two-asset pair form only, a
    /// settle-marked single asset is the malformed shape the parse refuses).
    /// Whitespace and stray separators are a refused spelling, never a
    /// coerced asset name.
    /// </summary>
    /// <param name="spelling">The user-typed symbol spelling.</param>
    /// <returns>Whether the spelling parses exactly under this vocabulary.</returns>
    public static bool IsAParsableSpelling(string spelling)
    {
        if (string.IsNullOrWhiteSpace(spelling) || spelling.Length > MaxSpellingLength)
        {
            return false;
        }

        var settleParts = spelling.Split(SettleMark);
        if (settleParts.Length > 2)
        {
            return false;
        }

        var settle = settleParts.Length == 2 ? settleParts[1] : null;
        var tokens = settleParts[0].Split(SpellingSeparator);
        if (tokens.Length != 2 && settle is not null)
        {
            // The settle mark rides the pair form only: a single asset with
            // a settle ("USDT:USDT") is malformed, never a coerced pair.
            return false;
        }

        return tokens.All(IsAnAssetToken) && (settle is null || IsAnAssetToken(settle));
    }

    /// <summary>Whether one asset token is a non-empty run of letters, digits, dots or dashes.</summary>
    private static bool IsAnAssetToken(string token) =>
        token.Length > 0 &&
        token.All(character => char.IsLetterOrDigit(character) || character is '.' or '-');
}
