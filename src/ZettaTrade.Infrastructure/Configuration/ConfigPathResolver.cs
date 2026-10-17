namespace ZettaTrade.Infrastructure.Configuration;

/// <summary>
/// Resolves platform-specific configuration and data paths.
/// Follows XDG Base Directory Specification on Linux.
/// </summary>
public static class ConfigPathResolver
{
    private const string AppName = "ZettaTrade";

    /// <summary>
    /// Root configuration directory.
    /// Windows: %APPDATA%/ZettaTrade/config/
    /// macOS:   ~/Library/Application Support/ZettaTrade/config/
    /// Linux:   ~/.config/ZettaTrade/config/ (XDG_CONFIG_HOME)
    /// </summary>
    public static string ConfigDirectory => Path.Combine(GetAppDataRoot(), "config");

    /// <summary>
    /// Root data directory (databases, logs, cache).
    /// Windows: %LOCALAPPDATA%/ZettaTrade/data/
    /// macOS:   ~/Library/Application Support/ZettaTrade/data/
    /// Linux:   ~/.local/share/ZettaTrade/data/ (XDG_DATA_HOME)
    /// </summary>
    public static string DataDirectory => Path.Combine(GetDataRoot(), "data");

    /// <summary>
    /// Directory for log files.
    /// Linux: ~/.local/state/ZettaTrade/logs/ (XDG_STATE_HOME)
    /// </summary>
    public static string LogDirectory => Path.Combine(GetStateRoot(), "logs");

    /// <summary>
    /// Directory for custom themes.
    /// </summary>
    public static string ThemesDirectory => Path.Combine(ConfigDirectory, "themes");

    /// <summary>
    /// Directory for plugin assemblies.
    /// </summary>
    public static string PluginsDirectory => Path.Combine(DataDirectory, "plugins");

    /// <summary>
    /// Path to the main application settings file.
    /// </summary>
    public static string MainSettingsFile => Path.Combine(ConfigDirectory, "appsettings.json");

    /// <summary>
    /// Path to MCP configuration file.
    /// </summary>
    public static string McpConfigFile => Path.Combine(ConfigDirectory, "mcp.config.json");

    /// <summary>
    /// Path to exchange configuration file.
    /// </summary>
    public static string ExchangesConfigFile => Path.Combine(ConfigDirectory, "exchanges.config.json");

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
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                AppName);
        }

        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "Application Support", AppName);
        }

        // Linux / other Unix — XDG_CONFIG_HOME
        var xdgConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrEmpty(xdgConfig))
        {
            xdgConfig = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config");
        }

        return Path.Combine(xdgConfig, AppName);
    }

    private static string GetDataRoot()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                AppName);
        }

        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "Application Support", AppName);
        }

        // Linux — XDG_DATA_HOME
        var xdgData = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrEmpty(xdgData))
        {
            xdgData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local", "share");
        }

        return Path.Combine(xdgData, AppName);
    }

    private static string GetStateRoot()
    {
        if (OperatingSystem.IsLinux())
        {
            // XDG_STATE_HOME
            var xdgState = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
            if (string.IsNullOrEmpty(xdgState))
            {
                xdgState = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".local", "state");
            }

            return Path.Combine(xdgState, AppName);
        }

        // Windows/macOS: same as data root
        return GetDataRoot();
    }
}
