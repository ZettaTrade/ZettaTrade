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

    /// <summary>
    /// The canonical names of the indicators the loaded plugin set
    /// registers - an immutable snapshot (the scan runs at startup only,
    /// the plugin set is fixed for the session). The settings surface keys
    /// its rows by indicator name against this set: a row renders only for
    /// a name present here, a save never fabricates a row for an absent
    /// plugin.
    /// </summary>
    IReadOnlyCollection<string> LoadedNames { get; }
}
