namespace ZettaTrade.UI.Themes;
using System.Text.Json;
using Avalonia;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// Manages application themes with support for custom JSON theme files.
/// Users can create, edit, and share themes without recompiling the application.
/// </summary>
public class ThemeManager : IThemeManager
{
    private readonly ILogger<ThemeManager> _logger;
    private readonly string _themesDirectory;
    private ThemeInfo _currentTheme;
    private readonly List<ThemeInfo> _availableThemes;

    public ThemeManager(ILogger<ThemeManager> logger)
    {
        _logger = logger;
        _themesDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ZettaTrade", "themes");
        
        _availableThemes = new List<ThemeInfo>();
        _currentTheme = new ThemeInfo("dark", "Dark", true, new[] { "#0a0a0f", "#1a1a2e", "#16213e", "#0f3460" });
        
        // Create themes directory if it doesn't exist
        if (!Directory.Exists(_themesDirectory))
        {
            Directory.CreateDirectory(_themesDirectory);
            CreateSampleTheme();
        }
        
        LoadThemes();
    }

    public ThemeInfo CurrentTheme => _currentTheme;
    public IReadOnlyList<ThemeInfo> AvailableThemes => _availableThemes;

    public event EventHandler<ThemeInfo>? ThemeChanged;

    public void ApplyTheme(string themeName)
    {
        var theme = _availableThemes.FirstOrDefault(t => t.Name == themeName);
        if (theme == null)
        {
            _logger.LogWarning("Theme not found: {ThemeName}", themeName);
            return;
        }

        _currentTheme = theme;
        
        // Apply theme colors to Avalonia resources
        if (Application.Current?.Resources != null)
        {
            foreach (var (key, value) in theme.Colors)
            {
                Application.Current.Resources[key] = value;
            }
        }

        ThemeChanged?.Invoke(this, theme);
        _logger.LogInformation("Theme applied: {ThemeName}", themeName);
    }

    public void LoadThemes()
    {
        _availableThemes.Clear();
        
        // Add built-in themes
        _availableThemes.Add(new ThemeInfo("dark", "Dark", true, new[] { "#0a0a0f", "#1a1a2e", "#16213e", "#0f3460" }));
        _availableThemes.Add(new ThemeInfo("light", "Light", true, new[] { "#ffffff", "#f8f9fa", "#e9ecef", "#dee2e6" }));
        _availableThemes.Add(new ThemeInfo("midnight", "Midnight Blue", true, new[] { "#0d1117", "#161b22", "#21262d", "#1f6feb" }));
        _availableThemes.Add(new ThemeInfo("oled", "OLED Black", true, new[] { "#000000", "#0a0a0a", "#111111", "#1a1a1a" }));
        
        // Load custom themes from JSON files
        if (Directory.Exists(_themesDirectory))
        {
            foreach (var file in Directory.GetFiles(_themesDirectory, "*.json"))
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var themeDef = JsonSerializer.Deserialize<ThemeDefinition>(json);
                    
                    if (themeDef != null)
                    {
                        var theme = new ThemeInfo(
                            themeDef.Name,
                            themeDef.DisplayName,
                            false,
                            themeDef.PreviewColors ?? Array.Empty<string>());
                        
                        theme.Colors = themeDef.Colors ?? new Dictionary<string, string>();
                        _availableThemes.Add(theme);
                        
                        _logger.LogInformation("Loaded custom theme: {ThemeName} from {File}", themeDef.Name, file);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to load theme from {File}", file);
                }
            }
        }
    }

    private void CreateSampleTheme()
    {
        var sampleTheme = new ThemeDefinition
        {
            Name = "sample-custom",
            DisplayName = "Sample Custom Theme",
            BaseTheme = "Dark",
            Colors = new Dictionary<string, string>
            {
                ["Background"] = "#1a1a2e",
                ["Surface"] = "#16213e",
                ["Border"] = "#0f3460",
                ["Accent"] = "#e94560",
                ["TextPrimary"] = "#ffffff",
                ["TextSecondary"] = "#a0a0a0",
                ["Buy"] = "#00ff88",
                ["Sell"] = "#ff0055",
                ["CandleUp"] = "#00ff88",
                ["CandleDown"] = "#ff0055"
            },
            PreviewColors = new[] { "#1a1a2e", "#16213e", "#0f3460", "#e94560" }
        };

        var json = JsonSerializer.Serialize(sampleTheme, new JsonSerializerOptions { WriteIndented = true });
        var filePath = Path.Combine(_themesDirectory, "sample-custom.json");
        File.WriteAllText(filePath, json);
        
        _logger.LogInformation("Created sample theme at {Path}", filePath);
    }
}

/// <summary>
/// Theme definition loaded from JSON file.
/// </summary>
public class ThemeDefinition
{
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? BaseTheme { get; set; }
    public Dictionary<string, string>? Colors { get; set; }
    public string[]? PreviewColors { get; set; }
}
