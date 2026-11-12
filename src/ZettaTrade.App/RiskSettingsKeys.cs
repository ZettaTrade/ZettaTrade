namespace ZettaTrade.App;

/// <summary>
/// Settings keys of the daily loss limit (T-189): the risk governor's
/// protective day-loss threshold. Defaults only — the governor re-reads
/// the store at every order check, so a stored change (the settings file
/// or an import) applies at the next decision without a restart.
/// </summary>
public static class RiskSettingsKeys
{
    /// <summary>
    /// Settings section of the risk governor's knobs.
    /// </summary>
    public const string Section = "Risk";

    /// <summary>
    /// Settings key of the master switch: whether the daily loss governor
    /// blocks new orders at all.
    /// </summary>
    public const string DailyLossLimitEnabled = "DailyLossLimitEnabled";

    /// <summary>
    /// The default applied until a stored setting is loaded: OFF — the
    /// shipped behavior carries no day-loss gate, and the block is a
    /// deliberate operator choice (the off state keeps every risk decision
    /// bit-identical to the ungoverned past).
    /// </summary>
    public const bool DailyLossLimitEnabledValue = false;

    /// <summary>
    /// Settings key of the loss threshold in the symbol's quote currency
    /// (a positive amount): the day's net realized result at which new
    /// orders are blocked. The realized rows are already net of their
    /// commissions, so the threshold compares against the plain window
    /// sum — no second fee subtraction ever happens.
    /// </summary>
    public const string DailyLossLimitAmount = "DailyLossLimitAmount";

    /// <summary>
    /// The default threshold: 1000 quote units — a conservative paper-scale
    /// anchor; the shipped posture stays OFF, so the number applies only
    /// once the operator turns the governor on.
    /// </summary>
    public const decimal DailyLossLimitAmountValue = 1000m;

    /// <summary>
    /// Settings key of the manual reset marker (an ISO-8601 UTC timestamp
    /// string, or absence): the moment the operator lifted the block. The
    /// loss window's start tightens to it until the day rolls over;
    /// absence is the default — the window then spans the whole local day.
    /// </summary>
    public const string DailyLossResetAt = "DailyLossResetAt";
}
