namespace ZettaTrade.UI.Themes;

using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using Avalonia;
using Avalonia.Styling;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Infrastructure.Configuration;

/// <summary>
/// Manages application themes. Every theme — built-in and custom — is a JSON
/// file following the documented schema (name, displayName, baseTheme, colors,
/// previewColors): built-ins are shipped next to the executable
/// (<c>themes/*.json</c>), user themes live in the settings themes directory.
/// A user file with the same name overrides the shipped theme, so themes can
/// be edited or added without recompiling. While the app runs, both theme
/// directories are watched and a saved file is applied live (hot-reload).
/// </summary>
public sealed class ThemeManager : IThemeManager, IAsyncInitializable, IDisposable
{
    private static ThemeManager? _instance;

    /// <summary>
    /// Get the singleton instance of ThemeManager.
    /// </summary>
    public static ThemeManager Instance => _instance ?? throw new InvalidOperationException("ThemeManager not initialized");

    /// <summary>
    /// True once a <see cref="IThemeManager"/> instance exists; lets controls
    /// (e.g. the chart) subscribe safely even before the app wires the manager.
    /// </summary>
    public static bool IsInitialized => _instance is not null;

    #region Constants

    /// <summary>Theme kept before any stored preference is applied.</summary>
    private const string DefaultThemeName = "Dark";

    /// <summary>App-relative folder with the shipped built-in theme files.</summary>
    private const string BuiltInThemesFolder = "themes";

    /// <summary>baseTheme value that maps a theme onto Avalonia's light variant.</summary>
    private const string BaseThemeLight = "Light";

    /// <summary>baseTheme value that maps a theme onto Avalonia's dark variant.</summary>
    private const string BaseThemeDark = "Dark";

    #endregion Constants

    #region Private Fields

    private readonly ILogger _logger;
    private readonly BehaviorSubject<string> _themeChanged;
    private readonly List<ThemeInfo> _themes = new();
    private readonly Dictionary<string, ThemeVariant> _variantOfTheme = new();
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly string _builtInThemesDirectory;
    private readonly string _userThemesDirectory;
    private ThemeFileWatcher? _fileWatcher;
    private IDisposable? _reloadSubscription;
    private string _current = DefaultThemeName;
    private string? _activeName;
    private bool _disposed;

    #endregion Private Fields

    /// <summary>
    /// Create a new ThemeManager instance. Theme files are loaded by
    /// <see cref="InitializeAsync"/>; the composition root calls it during
    /// startup, before any stored theme preference is applied.
    /// </summary>
    /// <param name="logger">Injected application logger.</param>
    /// <param name="builtInThemesDirectory">
    /// Overrides the shipped themes folder next to the executable; tests use
    /// this to stay away from real application files.
    /// </param>
    /// <param name="userThemesDirectory">
    /// Overrides the settings themes directory; tests use this to stay away
    /// from the real user configuration.
    /// </param>
    public ThemeManager(
        ILogger logger,
        string? builtInThemesDirectory = null,
        string? userThemesDirectory = null)
    {
        _logger = logger;
        _builtInThemesDirectory = builtInThemesDirectory ?? Path.Combine(AppContext.BaseDirectory, BuiltInThemesFolder);
        _userThemesDirectory = userThemesDirectory ?? ConfigPathResolver.ThemesDirectory;
        _themeChanged = new BehaviorSubject<string>(_current);
        _instance = this;
    }

    /// <summary>
    /// Initialize theme manager by loading the shipped built-in themes and the
    /// user themes from disk, then start watching both theme directories so a
    /// saved file is picked up without a restart. Built-ins load first, so a
    /// user file overrides them by name.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await LoadBuiltInThemesAsync(ct);
        await LoadCustomThemesAsync(ct);
        StartThemeHotReload();
    }

    #region Properties

    public string CurrentTheme => _current;

    public IReadOnlyList<ThemeInfo> AvailableThemes => _themes;

    public IObservable<string> ThemeChanged => _themeChanged;

    #endregion Properties

    #region Applying

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
            _activeName = theme.Name;

            if (Application.Current != null)
            {
                // Themes shipped by earlier versions did not declare baseTheme;
                // they default to the dark base variant (previous behavior).
                Application.Current.RequestedThemeVariant =
                    _variantOfTheme.TryGetValue(theme.Name, out var variant)
                        ? variant
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

    #endregion Applying

    #region Theme Loading

    /// <summary>
    /// Load a custom theme from a JSON file. A file whose name matches an
    /// already registered theme (typically a built-in) replaces it.
    /// </summary>
    public async Task LoadCustomThemeAsync(string filePath, CancellationToken ct = default)
    {
        await LoadThemeFileAsync(
            filePath,
            isBuiltIn: false,
            reapplyIfActive: false,
            ct);
    }

    private async Task LoadBuiltInThemesAsync(CancellationToken ct)
    {
        if (!Directory.Exists(_builtInThemesDirectory))
        {
            _logger.LogWarning("Built-in themes directory not found: {Directory}", _builtInThemesDirectory);
            return;
        }

        foreach (var file in Directory.EnumerateFiles(_builtInThemesDirectory, "*.json"))
        {
            await LoadThemeFileAsync(
                file,
                isBuiltIn: true,
                reapplyIfActive: false,
                ct);
        }
    }

    private async Task LoadCustomThemesAsync(CancellationToken ct)
    {
        if (!Directory.Exists(_userThemesDirectory))
        {
            Directory.CreateDirectory(_userThemesDirectory);
            await CreateSampleThemeAsync(_userThemesDirectory);
            return;
        }

        foreach (var file in Directory.EnumerateFiles(_userThemesDirectory, "*.json"))
        {
            await LoadThemeFileAsync(
                file,
                isBuiltIn: false,
                reapplyIfActive: false,
                ct);
        }
    }

    private async Task LoadThemeFileAsync(
        string filePath,
        bool isBuiltIn,
        bool reapplyIfActive,
        CancellationToken ct)
    {
        string? loadedName = null;
        var reapply = false;
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

            loadedName = themeDef.Name;

            var themeInfo = new ThemeInfo(
                themeDef.Name,
                themeDef.DisplayName ?? themeDef.Name,
                isBuiltIn,
                themeDef.PreviewColors ?? Array.Empty<string>(),
                themeDef.Colors);

            _variantOfTheme[themeInfo.Name] = ResolveVariant(themeDef.BaseTheme);

            // Replace an existing registration in place (user override of a
            // built-in); otherwise register a new theme at the end of the list.
            var index = _themes.FindIndex(t => t.Name == themeInfo.Name);
            if (index >= 0)
            {
                _themes[index] = themeInfo;
            }
            else
            {
                _themes.Add(themeInfo);
            }

            _logger.LogInformation(
                "{Kind} theme loaded: {Name} from {Path}",
                isBuiltIn ? "Built-in" : "Custom",
                themeDef.Name,
                filePath);

            // A hot reload of the active theme re-applies it after the lock is
            // released, so a saved file retunes the running window; bulk loads
            // at startup never trigger this.
            reapply = reapplyIfActive &&
                      string.Equals(loadedName, _activeName, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load theme from {Path}", filePath);
        }
        finally
        {
            _lock.Release();
        }

        if (reapply)
        {
            await ReapplyThemeAsync(loadedName!, ct);
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

    #endregion Theme Loading

    #region Theme Hot Reload

    /// <summary>
    /// Start watching both theme directories. Each debounced change is
    /// processed asynchronously and serially, so one malformed file must not
    /// stop the pipeline.
    /// </summary>
    private void StartThemeHotReload()
    {
        _fileWatcher = new ThemeFileWatcher(
            _logger,
            _builtInThemesDirectory,
            _userThemesDirectory);

        _reloadSubscription = _fileWatcher.FileChanged
            .Select(change => Observable.FromAsync(token => HandleThemeFileChangeAsync(change, token)))
            .Concat()
            .Subscribe(_ => { }, ex => _logger.LogError(ex, "Theme hot-reload pipeline stopped"));
    }

    /// <summary>
    /// Process one debounced file change: reload the registration and, when
    /// the file belongs to the active theme, re-apply it so the running
    /// window picks up the new colors without a restart.
    /// </summary>
    private async Task HandleThemeFileChangeAsync(
        ThemeFileChange change,
        CancellationToken ct)
    {
        if (_disposed) return;
        try
        {
            if (!File.Exists(change.FilePath))
            {
                // The event raced with a delete; a deleted file never removes
                // its registration, the last loaded version stays.
                return;
            }

            await LoadThemeFileAsync(
                change.FilePath,
                change.IsBuiltIn,
                reapplyIfActive: true,
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown: processing stops normally.
        }
        catch (Exception ex)
        {
            // Never let one bad file kill the hot-reload pipeline.
            _logger.LogError(ex, "Theme hot-reload failed for {Path}", change.FilePath);
        }
    }

    /// <summary>
    /// Re-apply a theme after a hot reload. Avalonia resources and the base
    /// variant must be touched on the UI thread; the watcher pipeline runs on
    /// the thread pool, so it dispatches.
    /// </summary>
    private async Task ReapplyThemeAsync(
        string themeName,
        CancellationToken ct)
    {
        if (_disposed) return;

        if (Application.Current is null)
        {
            // Unit tests (and design-time): no Avalonia app to dispatch to.
            await ApplyThemeAsync(themeName, ct);
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() => ApplyThemeAsync(themeName, ct));
    }

    #endregion Theme Hot Reload

    #region Color Mapping

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

    #endregion Color Mapping

    #region Variant Mapping

    /// <summary>
    /// Map a theme's <c>baseTheme</c> field onto the Avalonia base variant:
    /// the light value maps onto <see cref="ThemeVariant.Light"/>, everything
    /// else (including a missing value) onto the dark variant.
    /// </summary>
    private static ThemeVariant ResolveVariant(string? baseTheme)
    {
        return baseTheme is not null &&
               baseTheme.Equals(BaseThemeLight, StringComparison.OrdinalIgnoreCase)
            ? ThemeVariant.Light
            : ThemeVariant.Dark;
    }

    #endregion Variant Mapping

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _reloadSubscription?.Dispose();
        _fileWatcher?.Dispose();
        _themeChanged.Dispose();
        _lock.Dispose();
    }
}

/// <summary>
/// JSON schema of a theme file: name, displayName, baseTheme ("Dark" or
/// "Light"), the named color palette and the colors shown in the preview.
/// </summary>
public sealed record ThemeDefinition
{
    public string? Name { get; init; }
    public string? DisplayName { get; init; }
    public string? BaseTheme { get; init; }
    public Dictionary<string, string>? Colors { get; init; }
    public string[]? PreviewColors { get; init; }
}
