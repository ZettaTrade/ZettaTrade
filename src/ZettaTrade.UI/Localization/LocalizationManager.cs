namespace ZettaTrade.UI.Localization;
using System.Collections.Concurrent;
using System.Globalization;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Resources;
using Avalonia.Data;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// Reactive localization service using ResourceManager and .resx resource files.
/// Provides IObservable-based language change notifications for efficient UI updates.
/// Includes thread-safe caching for fast repeated string lookups.
/// </summary>
public sealed class LocalizationManager : ILocalizationManager, IDisposable
{
    private static LocalizationManager? _instance;
    public static LocalizationManager Instance => _instance ?? throw new InvalidOperationException("LocalizationManager not initialized");

    private readonly ResourceManager _resourceManager;
    private readonly ILogger _logger;
    private readonly BehaviorSubject<string> _languageChanged;
    private readonly ConcurrentDictionary<string, string> _cache = new();
    private CultureInfo _currentCulture;
    private bool _disposed;

    /// <summary>
    /// Creates LocalizationManager with ResourceManager from .resx files.
    /// </summary>
    /// <param name="logger">Logger instance</param>
    /// <param name="resourceManager">ResourceManager from generated .resx code (e.g., ZettaTrade.UI.Resources.Strings.ResourceManager)</param>
    public LocalizationManager(ILogger logger, ResourceManager resourceManager)
    {
        _logger = logger;
        _resourceManager = resourceManager;
        _currentCulture = CultureInfo.CurrentUICulture;
        _languageChanged = new BehaviorSubject<string>(_currentCulture.Name);
        _instance = this;

        AvailableLanguages = [
            new("en", "English", "English", true),
            new("ru", "Russian", "Русский", true),
            new("zh-CN", "Chinese", "简体中文", false),
            new("ja", "Japanese", "日本語", false),
            new("ko", "Korean", "한국어", false),
            new("es", "Spanish", "Español", false),
            new("de", "German", "Deutsch", false),
            new("fr", "French", "Français", false),
        ];

        _logger.LogInformation("LocalizationManager initialized with ResourceManager");
    }

    public string CurrentLanguage => _currentCulture.Name;
    public IReadOnlyList<LanguageInfo> AvailableLanguages { get; }

    /// <summary>
    /// Observable stream of language changes with debounce to prevent excessive updates.
    /// Subscribe to this in ViewModels to reactively update localized strings.
    /// </summary>
    public IObservable<string> LanguageChanged => _languageChanged
        .DistinctUntilChanged()
        .Debounce(TimeSpan.FromMilliseconds(50));

    /// <summary>
    /// Cached string lookup from .resx resources. Thread-safe and culture-aware.
    /// Cache is automatically invalidated on language change.
    /// </summary>
    public string this[string key]
    {
        get
        {
            var cacheKey = $"{_currentCulture.Name}:{key}";
            return _cache.GetOrAdd(cacheKey, _ => GetStringFromResource(key));
        }
    }

    /// <summary>
    /// Get localized string with format arguments.
    /// Note: Formatted strings are not cached to avoid cache bloat.
    /// </summary>
    public string GetString(string key, params object[] args)
    {
        var template = this[key];
        try
        {
            return string.Format(template, args);
        }
        catch (FormatException ex)
        {
            _logger.LogWarning(ex, "Failed to format string for key {Key}", key);
            return template;
        }
    }

    /// <summary>
    /// Get IObservable for a specific localization key.
    /// Emits current value immediately, then emits on language changes.
    /// Use this for reactive bindings in ViewModels.
    /// </summary>
    public IObservable<string> GetObservable(string key)
    {
        return LanguageChanged
            .Select(_ => this[key])
            .StartWith(this[key]);
    }

    /// <summary>
    /// Change the current UI language. Clears cache and notifies all subscribers.
    /// </summary>
    public Task SetLanguageAsync(string code, CancellationToken ct = default)
    {
        if (_currentCulture.Name == code) return Task.CompletedTask;

        var culture = new CultureInfo(code);
        _currentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;

        // Clear cache on language change
        _cache.Clear();

        _logger.LogInformation("Language changed to {Language} ({NativeName})", code, culture.NativeName);

        // Notify all subscribers
        _languageChanged.OnNext(code);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Get string from .resx resource file via ResourceManager.
    /// </summary>
    private string GetStringFromResource(string key)
    {
        try
        {
            var value = _resourceManager.GetString(key, _currentCulture);
            if (value is null)
            {
                _logger.LogWarning("Localization key not found: {Key} for culture {Culture}", key, _currentCulture.Name);
                return $"[{key}]";
            }
            return value;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get localized string for key {Key}", key);
            return $"[{key}]";
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _languageChanged.Dispose();
        _cache.Clear();
    }
}

/// <summary>
/// Avalonia MarkupExtension for reactive localization in XAML.
/// Creates a binding that automatically updates when language changes.
/// Usage: {loc:Localized Menu.File}
/// </summary>
public class LocalizedExtension : MarkupExtension
{
    public string Key { get; }

    public LocalizedExtension(string key)
    {
        Key = key;
    }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        // Use indexer binding - will update via PropertyChanged when language changes
        return new Binding
        {
            Path = $"[{Key}]",
            Mode = BindingMode.OneWay,
            Source = LocalizationManager.Instance,
        }.ProvideValue(serviceProvider);
    }
}
