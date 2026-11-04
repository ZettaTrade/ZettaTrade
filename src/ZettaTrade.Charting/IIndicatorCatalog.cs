namespace ZettaTrade.Charting;

/// <summary>
/// Resolves the indicator implementation behind one configured overlay row:
/// looks up <see cref="IIndicator.Name"/> and builds the implementation for
/// the period the settings row carries (the documented plugin ctor
/// convention: a public <c>ctor(int)</c> when the plugin offers one, a
/// parameterless constructor otherwise). A name the loaded plugins do not
/// offer, or a period the implementation rejects, resolves to null - the
/// overlay row is skipped, the chart never crashes on the seam.
/// </summary>
public interface IIndicatorCatalog
{
    /// <summary>
    /// Build the indicator for one settings row, or null when the name is
    /// unknown or the period is unusable for that indicator (the catalog
    /// logs the rejection).
    /// </summary>
    IIndicator? Create(string name, int period);
}
