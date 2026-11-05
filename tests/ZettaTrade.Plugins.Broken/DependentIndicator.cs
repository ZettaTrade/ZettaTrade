namespace ZettaTrade.Plugins.Broken;

using ZettaTrade.Plugins.BrokenBase;

/// <summary>
/// T-054 (BUG-014) test fixture: the plugin type whose base lives in a
/// dependency assembly that is missing at scan time. Loading this type —
/// what <c>assembly.GetTypes()</c> does — throws
/// <c>ReflectionTypeLoadException</c> instead of landing in the catalog.
/// </summary>
public sealed class DependentIndicator : MissingBase;
