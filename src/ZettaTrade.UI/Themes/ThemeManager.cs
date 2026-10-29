namespace ZettaTrade.UI.Themes;

using System.Reactive.Subjects;
using System.Text.Json;
using Avalonia;
using Avalonia.Styling;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Infrastructure.Configuration;

/// <summary>
/// Manages application themes with support for custom JSON theme files.
/// Users can create/edit themes in the themes directory without recompiling.
/// </summary>
public sealed class ThemeManager : IThemeManager, IAsyncInitializable, IDisposable
{
    private readonly ILogger _logger;
    private readonly BehaviorSubject<string> _themeChanged;
    private readonly List<ThemeInfo> _themes = new();
    private readonly Dictionary<string, ThemeInfo> _themeDefinitions = new();
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string _current = "Dark";
    private bool _disposed;

    /// <summary>
    /// Create a new ThemeManager instance.
    /// </summary>
    public ThemeManager(ILogger logger)
    {
        _logger = logger;
        _themeChanged = new BehaviorSubject<string>(_current);

        RegisterBuiltInThemes();
    }

    /// <summary>
    /// Initialize theme manager by loading custom themes from disk.
    /// Must be called after construction.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await LoadCustomThemesAsync();
    }

    public string CurrentTheme => _current;

    public IReadOnlyList<ThemeInfo> AvailableThemes => _themes;

    public IObservable<string> ThemeChanged => _themeChanged;

    public async Task ApplyThemeAsync(string name, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var theme = _themes.FirstOrDefault(t =>
                t.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                t.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase));

            if (theme is null)
            {
                _logger.LogWarning("Theme not found: {ThemeName}", name);
                return;
            }

            _current = theme.DisplayName;

            if (Application.Current != null)
            {
                Application.Current.RequestedThemeVariant = theme.Name == "light"
                    ? ThemeVariant.Light
                    : ThemeVariant.Dark;
            }

            if (theme.Colors != null)
            {
                ApplyThemeColors(theme.Colors);
            }

            _themeChanged.OnNext(_current);
            _logger.LogInformation("Theme applied: {Theme}", _current);
        }
        finally
        {
            _lock.Release();
        }
    }

    private void RegisterBuiltInThemes()
    {
        var builtInThemes = new[]
        {
            new ThemeInfo("dark", "Dark", true,
                new[] { "#0a0a0f", "#1a1a2e", "#16213e", "#0f3460" },
                new Dictionary<string, string>
                {
                    ["Background"] = "#0a0a0f",
                    ["Surface"] = "#1a1a2e",
                    ["Border"] = "#16213e",
                    ["Accent"] = "#0f3460",
                    ["TextPrimary"] = "#ffffff",
                    ["TextSecondary"] = "#a0a0a0",
                    ["Buy"] = "#22c55e",
                    ["Sell"] = "#ef4444",
                    ["CandleUp"] = "#22c55e",
                    ["CandleDown"] = "#ef4444"
                }),

            new ThemeInfo("light", "Light", true,
                new[] { "#ffffff", "#f8f9fa", "#e9ecef", "#dee2e6" },
                new Dictionary<string, string>
                {
                    ["Background"] = "#ffffff",
                    ["Surface"] = "#f8f9fa",
                    ["Border"] = "#e9ecef",
                    ["Accent"] = "#dee2e6",
                    ["TextPrimary"] = "#000000",
                    ["TextSecondary"] = "#6c757d",
                    ["Buy"] = "#22c55e",
                    ["Sell"] = "#ef4444",
                    ["CandleUp"] = "#22c55e",
                    ["CandleDown"] = "#ef4444"
                }),

            new ThemeInfo("midnight", "Midnight Blue", true,
                new[] { "#0d1117", "#161b22", "#21262d", "#1f6feb" },
                new Dictionary<string, string>
                {
                    ["Background"] = "#0d1117",
                    ["Surface"] = "#161b22",
                    ["Border"] = "#21262d",
                    ["Accent"] = "#1f6feb",
                    ["TextPrimary"] = "#ffffff",
                    ["TextSecondary"] = "#8b949e",
                    ["Buy"] = "#22c55e",
                    ["Sell"] = "#ef4444",
                    ["CandleUp"] = "#22c55e",
                    ["CandleDown"] = "#ef4444"
                }),

            new ThemeInfo("oled", "OLED Black", true,
                new[] { "#000000", "#0a0a0a", "#111111", "#1a1a1a" },
                new Dictionary<string, string>
                {
                    ["Background"] = "#000000",
                    ["Surface"] = "#0a0a0a",
                    ["Border"] = "#111111",
                    ["Accent"] = "#1a1a1a",
                    ["TextPrimary"] = "#ffffff",
                    ["TextSecondary"] = "#888888",
                    ["Buy"] = "#22c55e",
                    ["Sell"] = "#ef4444",
                    ["CandleUp"] = "#22c55e",
                    ["CandleDown"] = "#ef4444"
                })
        };

        foreach (var theme in builtInThemes)
        {
            _themeDefinitions[theme.Name] = theme;
            _themes.Add(theme);
        }
    }

    private async Task LoadCustomThemesAsync()
    {
        var themesDir = ConfigPathResolver.ThemesDirectory;
        if (!Directory.Exists(themesDir))
        {
            Directory.CreateDirectory(themesDir);
            await CreateSampleThemeAsync(themesDir);
            return;
        }

        foreach (var file in Directory.EnumerateFiles(themesDir, "*.json"))
        {
            await LoadCustomThemeAsync(file);
        }
    }

    public async Task LoadCustomThemeAsync(string filePath, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var json = await File.ReadAllTextAsync(filePath, ct);
            var themeDef = JsonSerializer.Deserialize<ThemeDefinition>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (themeDef?.Name is null)
            {
                _logger.LogWarning("Invalid theme file: {Path}", filePath);
                return;
            }

            var themeInfo = new ThemeInfo(
                themeDef.Name,
                themeDef.DisplayName ?? themeDef.Name,
                IsBuiltIn: false,
                themeDef.PreviewColors ?? Array.Empty<string>(),
                themeDef.Colors);

            _themeDefinitions[themeDef.Name] = themeInfo;

            if (!_themes.Any(t => t.Name == themeDef.Name))
            {
                _themes.Add(themeInfo);
            }

            _logger.LogInformation("Custom theme loaded: {Name} from {Path}", themeDef.Name, filePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load custom theme from {Path}", filePath);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task CreateSampleThemeAsync(string themesDir)
    {
        var sampleTheme = new ThemeDefinition
        {
            Name = "sample-custom",
            DisplayName = "Sample Custom Theme",
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
        var filePath = Path.Combine(themesDir, "sample-custom.json");
        await File.WriteAllTextAsync(filePath, json);

        _logger.LogInformation("Created sample theme at {Path}", filePath);
    }

    private void ApplyThemeColors(IReadOnlyDictionary<string, string> colors)
    {
        if (Application.Current?.Resources is null) return;

        foreach (var (key, value) in colors)
        {
            try
            {
                var color = Avalonia.Media.Color.Parse(value);
                Application.Current.Resources[key] = color;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse color {Key}={Value}", key, value);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _themeChanged.Dispose();
        _lock.Dispose();
    }
}

public sealed record ThemeDefinition
{
    public string? Name { get; init; }
    public string? DisplayName { get; init; }
    public Dictionary<string, string>? Colors { get; init; }
    public string[]? PreviewColors { get; init; }
}
