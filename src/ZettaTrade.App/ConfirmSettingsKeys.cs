namespace ZettaTrade.App;

/// <summary>
/// Settings keys of the order ticket's pre-submit confirmation (T-078):
/// whether the confirmation dialog is asked before a placement at all.
/// Defaults only — the decision re-reads the settings at every submit, so
/// a stored change (the settings UI or an external file edit) applies at
/// the next decision without a restart.
/// </summary>
public static class ConfirmSettingsKeys
{
    /// <summary>
    /// Settings section of the pre-submit confirmation policy.
    /// </summary>
    public const string Section = "OrderConfirm";

    /// <summary>
    /// Settings key of the live master switch: whether a live placement is
    /// asked its confirmation summary. Paper placements never read this key.
    /// </summary>
    public const string Enabled = "Enabled";

    /// <summary>
    /// The live default applied until a stored setting is loaded: ON — the
    /// summary is the gate a new exchange rider conventionally starts with,
    /// and only an explicit user turn-off removes it (market orders keep
    /// their own always-confirm rule regardless).
    /// </summary>
    public const bool EnabledValue = true;

    /// <summary>
    /// Settings key of the paper master switch: whether a paper placement
    /// is asked its confirmation summary. Paper is the sandbox — the
    /// summary is OFF there by default, so the demo flow never trains the
    /// habit and the sandbox never gates every demo order.
    /// </summary>
    public const string EnabledInPaper = "EnabledInPaper";

    /// <summary>
    /// The paper default applied until a stored setting is loaded: OFF —
    /// the sandbox never gates itself.
    /// </summary>
    public const bool EnabledInPaperValue = false;

    /// <summary>
    /// Settings key of the skip-below-notional threshold (quote currency,
    /// the risk engine's own USD-equivalent denominator): a placement whose
    /// estimated notional stays below it skips the summary. Zero — the
    /// default and the floor — never skips; the threshold only ever
    /// releases orders from the gate, never widens it.
    /// </summary>
    public const string SkipBelowNotionalUsd = "SkipBelowNotionalUsd";

    /// <summary>
    /// The default skip threshold: zero — no small-order release until the
    /// user configures one deliberately.
    /// </summary>
    public const decimal SkipBelowNotionalValue = 0m;
}
