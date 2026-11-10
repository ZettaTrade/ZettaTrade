namespace ZettaTrade.Infrastructure.Configuration;

using ZettaTrade.Core.Constants;

/// <summary>
/// Resolves platform-specific configuration and data paths.
/// Follows XDG Base Directory Specification on Linux.
/// </summary>
public static class ConfigPathResolver
{

    /// <summary>
    /// Root configuration directory.
    /// Windows: %APPDATA%/ZettaTrade/config/<br/>
    /// macOS: ~/Library/Application Support/ZettaTrade/config/<br/>
    /// Linux: ~/.config/ZettaTrade/config/ (XDG_CONFIG_HOME)
    /// </summary>
    public static string ConfigDirectory => Path.Combine(GetAppDataRoot(), "config");

    /// <summary>
    /// Root data directory (databases, logs, cache).
    /// Windows: %LOCALAPPDATA%/ZettaTrade/data/<br/>
    /// macOS: ~/Library/Application Support/ZettaTrade/data/<br/>
    /// Linux: ~/.local/share/ZettaTrade/data/ (XDG_DATA_HOME)
    /// </summary>
    public static string DataDirectory => Path.Combine(GetDataRoot(), "data");

    /// <summary>
    /// Directory for log files.
    /// Linux: ~/.local/state/ZettaTrade/logs/ (XDG_STATE_HOME)
    /// </summary>
    public static string LogDirectory => Path.Combine(GetStateRoot(), "logs");

    /// <summary>Directory for custom themes.</summary>
    public static string ThemesDirectory => Path.Combine(ConfigDirectory, "themes");

    /// <summary>Directory for plugin assemblies.</summary>
    public static string PluginsDirectory => Path.Combine(DataDirectory, "plugins");

    /// <summary>Path to the main application settings file.</summary>
    public static string MainSettingsFile => Path.Combine(ConfigDirectory, "appsettings.json");

    /// <summary>Path to MCP configuration file.</summary>
    public static string McpConfigFile => Path.Combine(ConfigDirectory, "mcp.config.json");

    /// <summary>
    /// Path to the SQLite database holding the persistent order history
    /// (the trading adapters themselves never touch it; writes go through
    /// the Infrastructure persistence module).
    /// </summary>
    public static string OrderHistoryDatabaseFile => Path.Combine(DataDirectory, "orders-history.db");

    /// <summary>
    /// Path to the SQLite database holding the realized-trade accounting
    /// (the PnL engine's own journal of record; read-only bookkeeping —
    /// it never touches the trading flow).
    /// </summary>
    public static string PnlDatabaseFile => Path.Combine(DataDirectory, "pnl.db");

    /// <summary>
    /// Path to the SQLite database holding the local candle cache
    /// (T-085): the write-through ledger of adapter-served bars — a
    /// disposable bookkeeping, rebuilt from the feeds as the stale window
    /// refreshes, never a source of truth the trading flow depends on.
    /// </summary>
    public static string CandleCacheDatabaseFile => Path.Combine(DataDirectory, "candles.db");

    /// <summary>
    /// Path to the SQLite database holding the persisted chart drawings
    /// (T-091): the trader's annotations keyed by (adapter, symbol,
    /// interval) — a small owned surface, saved on every commit/edit and
    /// reloaded per chart rebase.
    /// </summary>
    public static string ChartDrawingsDatabaseFile => Path.Combine(DataDirectory, "chart-drawings.db");

    /// <summary>
    /// Ensure all required directories exist.
    /// </summary>
    public static void EnsureDirectoriesExist()
    {
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(ThemesDirectory);
        Directory.CreateDirectory(PluginsDirectory);
    }

    private static string GetAppDataRoot()
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ApplicationConstants.ApplicationName);
        if (OperatingSystem.IsMacOS())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", ApplicationConstants.ApplicationName);
        return LinuxConfigRoot(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"));
    }

    private static string GetDataRoot()
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ApplicationConstants.ApplicationName);
        if (OperatingSystem.IsMacOS())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", ApplicationConstants.ApplicationName);
        return LinuxDataRoot(Environment.GetEnvironmentVariable("XDG_DATA_HOME"));
    }

    private static string GetStateRoot()
    {
        if (OperatingSystem.IsLinux())
        {
            return LinuxStateRoot(Environment.GetEnvironmentVariable("XDG_STATE_HOME"));
        }
        return GetDataRoot();
    }

    /// <summary>
    /// One XDG root: the variable's value wins when it carries a real path,
    /// and the platform default applies when the variable is UNSET (null),
    /// EMPTY or whitespace-only. The pre-fix fallback (`??`) caught only
    /// null, so an empty variable fell through and <c>Path.Combine</c>
    /// produced a CWD-relative path — broken locations in containers and
    /// scripts that export variables without values (BUG-044). Whitespace
    /// counts as unset too: it is a relative path the application could
    /// never create nor write through.
    /// </summary>
    /// <param name="envValue">The raw <c>XDG_*</c> variable value.</param>
    /// <param name="defaultRoot">The spec default the empty variable
    /// falls back to.</param>
    internal static string ResolveXdgRoot(string? envValue, string defaultRoot) =>
        Path.Combine(
            string.IsNullOrWhiteSpace(envValue) ? defaultRoot : envValue,
            ApplicationConstants.ApplicationName);

    /// <summary>
    /// The Linux configuration root from the raw <c>XDG_CONFIG_HOME</c>
    /// value (see <see cref="ResolveXdgRoot"/> for the fallback rule). The
    /// parameter stands in for the process environment — the facts matrix
    /// runs on Windows, where the OS-guarded resolvers above never reach
    /// their XDG branches.
    /// </summary>
    internal static string LinuxConfigRoot(string? xdgConfigHome) =>
        ResolveXdgRoot(
            xdgConfigHome,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"));

    /// <summary>The Linux data root from the raw <c>XDG_DATA_HOME</c> value.</summary>
    internal static string LinuxDataRoot(string? xdgDataHome) =>
        ResolveXdgRoot(
            xdgDataHome,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share"));

    /// <summary>The Linux state root from the raw <c>XDG_STATE_HOME</c> value.</summary>
    internal static string LinuxStateRoot(string? xdgStateHome) =>
        ResolveXdgRoot(
            xdgStateHome,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state"));
}
