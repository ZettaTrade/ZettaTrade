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
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        return Path.Combine(xdg, ApplicationConstants.ApplicationName);
    }

    private static string GetDataRoot()
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ApplicationConstants.ApplicationName);
        if (OperatingSystem.IsMacOS())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", ApplicationConstants.ApplicationName);
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        return Path.Combine(xdg, ApplicationConstants.ApplicationName);
    }

    private static string GetStateRoot()
    {
        if (OperatingSystem.IsLinux())
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_STATE_HOME")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state");
            return Path.Combine(xdg, ApplicationConstants.ApplicationName);
        }
        return GetDataRoot();
    }
}
