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
/// <param name="Trigger">The Price kind's edge trigger; unused (neutral slot) for the PercentMove kind.</param>
/// <param name="TargetPrice">The Price kind's level; unused (zero) for the PercentMove kind.</param>
/// <param name="ThresholdPercent">The PercentMove kind's percent threshold (non-negative); unused (zero) for the Price kind.</param>
/// <param name="WindowMinutes">The PercentMove kind's trailing window in minutes; unused (zero) for the Price kind.</param>
/// <param name="Note">The user's free-text note; empty when left off.</param>
public sealed record AlertDraft(
    string SymbolName,
    PriceAlertKind Kind,
    AlertTrigger Trigger,
    decimal TargetPrice,
    decimal ThresholdPercent,
    int WindowMinutes,
    string Note)
{
    /// <summary>
    /// Validate the draft against the kind it names: the symbol spelling
    /// must be parsable under this app's exact vocabulary (the seed for the
    /// engine's own refuse-not-default gate), the Price kind requires a
    /// strictly positive target, the PercentMove kind a strictly positive
    /// threshold and window; any other kind value is a fault. Errors are
    /// specific, never defaults (the trading-safety canon reaches every
    /// submitted value).
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

            default:
                throw new ArgumentOutOfRangeException(nameof(Kind), Kind, "The draft names no known alert kind.");
        }

        if (Trigger is not (AlertTrigger.Above or AlertTrigger.Below or AlertTrigger.At))
        {
            throw new ArgumentOutOfRangeException(nameof(Trigger), Trigger, "The draft names no known trigger edge.");
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
        LastFiredUtc: null);

    #region Private Fields

    /// <summary>The normalized spelling's part separator (the two-side crypto spelling).</summary>
    private const char SpellingSeparator = '/';

    /// <summary>The one spelling's length ceiling (an asset token never runs longer; the bound is honest, not decorative).</summary>
    private const int MaxSpellingLength = 32;

    #endregion Private Fields

    /// <summary>
    /// The draft-spelling vocabulary (T-096): ONE place the parsable symbol
    /// spelling is decided, because <see cref="ZettaTrade.Core.ValueObjects.Symbol"/>
    /// itself parses leniently (a one-part spelling is a legitimate stock
    /// ticker). A parsable spelling here is at most two non-empty asset
    /// tokens of letters, digits, dots or dashes — whitespace and stray
    /// separators are a refused spelling, never a coerced asset name.
    /// </summary>
    /// <param name="spelling">The user-typed symbol spelling.</param>
    /// <returns>Whether the spelling parses exactly under this vocabulary.</returns>
    public static bool IsAParsableSpelling(string spelling)
    {
        if (string.IsNullOrWhiteSpace(spelling) || spelling.Length > MaxSpellingLength)
        {
            return false;
        }

        var tokens = spelling.Split(SpellingSeparator);
        if (tokens.Length is > 2)
        {
            return false;
        }

        return tokens.All(token =>
            token.Length > 0 &&
            token.All(character => char.IsLetterOrDigit(character) || character is '.' or '-'));
    }
}
