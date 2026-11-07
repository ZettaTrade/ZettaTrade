namespace ZettaTrade.App;

/// <summary>
/// Settings keys of the position sizing helper (the order ticket's
/// quick-size row). Defaults only: the row's own inputs stay user-owned —
/// a stored default is applied until the user edits the field, and a hot
/// settings change re-applies on the same terms.
/// </summary>
public static class SizerSettingsKeys
{
    /// <summary>
    /// Settings section of the position sizing helper defaults.
    /// </summary>
    public const string Section = "PositionSizer";

    /// <summary>
    /// Settings key of the default risk percent (a positive number, 1 =
    /// one percent): the risk-percent field's applied-until-edited value.
    /// </summary>
    public const string DefaultRiskPercent = "DefaultRiskPercent";

    /// <summary>
    /// The default risk percent applied until a stored setting is loaded:
    /// one percent — a deliberately conservative anchor helpers of this
    /// kind conventionally begin at, and exactly what a fresh install
    /// showed before the setting existed.
    /// </summary>
    public const double DefaultRiskPercentValue = 1.0;
}
