namespace ZettaTrade.Plugins.BrokenBase;

/// <summary>
/// T-054 (BUG-014) test fixture: the deliberately missing base class of the
/// Broken plugin. The assembly is never staged into the plugins directory
/// the catalog scans, so the plugin's <c>GetTypes()</c> hits a genuinely
/// missing dependency and throws <c>ReflectionTypeLoadException</c> — the
/// exact surface BUG-014 protects.
/// </summary>
public abstract class MissingBase;
