namespace ZettaTrade.App;

/// <summary>
/// Settings keys of the tape section (T-245): the whale-print filter's
/// persisted thresholds. Single source of truth for tape settings access —
/// the absolute default rides one key, the per-symbol overrides ride a JSON
/// map, and a symbol without its own entry keeps riding the default. Zero
/// (either shape) is the honest off: everything passes, nothing highlights.
/// </summary>
public static class TapeSettingsKeys
{
    /// <summary>Settings section holding the trade tape's facts.</summary>
    public const string Section = "Tape";

    /// <summary>
    /// Key of the absolute whale threshold (decimal, quote-currency units;
    /// 0 = the filter is off). The default every symbol without its own
    /// override keeps riding.
    /// </summary>
    public const string WhaleThreshold = "WhaleThreshold";

    /// <summary>
    /// Key of the per-symbol whale overrides (a JSON object of normalized
    /// spelling to decimal; 0 = off for that symbol). An entry below zero
    /// names no threshold and drops at load.
    /// </summary>
    public const string WhaleThresholds = "WhaleThresholds";

    /// <summary>
    /// The persist debounce for the header field's keystrokes (milliseconds):
    /// the live filter applies per keystroke, the store takes the settled
    /// value.
    /// </summary>
    public const int WhalePersistDebounceMs = 600;
}
