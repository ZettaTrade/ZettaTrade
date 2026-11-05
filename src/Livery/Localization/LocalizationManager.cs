namespace Livery.Localization;

using System.Collections.Concurrent;
using System.Globalization;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Resources;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Livery.Extensions;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// Reactive localization service using ResourceManager and .resx resource files.
/// Provides IObservable-based language change notifications.
/// Includes thread-safe caching for fast repeated string lookups.
/// </summary>
public sealed class LocalizationManager : ILocalizationManager, IDisposable
{
    private static LocalizationManager? _instance;

    /// <summary>
    /// Get the singleton instance of LocalizationManager.
    /// </summary>
    public static LocalizationManager Instance => _instance ?? throw new InvalidOperationException("LocalizationManager not initialized");

    /// <summary>
    /// True once an <see cref="ILocalizationManager"/> instance exists; lets controls
    /// (e.g. the chart) subscribe safely even in design mode before the app wires the manager.
    /// </summary>
    public static bool IsInitialized => _instance is not null;

    private readonly ResourceManager _resourceManager;
    private readonly ILogger _logger;
    private readonly BehaviorSubject<string> _languageChanged;
    private readonly ConcurrentDictionary<string, string> _cache = new();
    private readonly SemaphoreSlim _lock = new(1, 1);
    private CultureInfo _currentCulture;
    private bool _disposed;

    /// <summary>
    /// Create a new LocalizationManager instance.
    /// </summary>
    public LocalizationManager(ILogger logger, ResourceManager resourceManager)
    {
        _logger = logger;
        _resourceManager = resourceManager;

        AvailableLanguages = new List<LanguageInfo>
        {
            new("en", "English", "English", true),
            new("ru", "Russian", "Русский", true),
            new("zh-CN", "Chinese", "简体中文", false),
            new("ja", "Japanese", "日本語", false),
            new("ko", "Korean", "한국어", false),
            new("es", "Spanish", "Español", false),
            new("de", "German", "Deutsch", false),
            new("fr", "French", "Français", false),
        };

        // First run keeps the OS language, but normalized to an available UI
        // language ("ru-RU" -> "ru"): the OS culture may be region-specific,
        // while selections and lookups work with plain language codes.
        _currentCulture = ResolveInitialCulture(CultureInfo.CurrentUICulture);
        _languageChanged = new BehaviorSubject<string>(_currentCulture.Name);
        _instance = this;

        _logger.LogInformation("LocalizationManager initialized with ResourceManager");
    }

    /// <summary>
    /// Map the OS UI culture onto one of the available languages by two-letter
    /// code; fall back to the OS culture itself when no listed language matches.
    /// </summary>
    private CultureInfo ResolveInitialCulture(CultureInfo osCulture)
    {
        var shortCode = osCulture.TwoLetterISOLanguageName;
        var match = AvailableLanguages
            .FirstOrDefault(l => l.Code.Equals(shortCode, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            return osCulture;
        }

        try
        {
            return new CultureInfo(match.Code);
        }
        catch (CultureNotFoundException)
        {
            return osCulture;
        }
    }

    public string CurrentLanguage => _currentCulture.Name;

    public IReadOnlyList<LanguageInfo> AvailableLanguages { get; }

    /// <summary>
    /// Observable stream of language changes, delivered on the UI thread.
    /// A language switch is a discrete user action, so there is no debounce.
    /// </summary>
    public IObservable<string> LanguageChanged => _languageChanged
        .DistinctUntilChanged()
        .ObserveOnUiThread();

    /// <summary>
    /// Get a localized string by key.
    /// Cached string lookup from .resx resources. Thread-safe and culture-aware.
    /// </summary>
    /// <remarks>
    /// The culture is snapshotted ONCE per call: the cache key and the
    /// resource lookup must carry THE SAME culture. The pre-fix read raced
    /// a concurrent language switch at two levels — the key read
    /// <c>_currentCulture</c> inline and the factory re-read the field for
    /// its lookup — so a switch landing between the reads stored a
    /// wrong-culture value under the caller's key, where it stuck until the
    /// next cache clear (BUG-051). A racing caller may still receive either
    /// the pre-switch or the post-switch string, but always the one
    /// consistent with its own snapshot.
    /// </remarks>
    public string GetString(string key)
    {
        var culture = _currentCulture;
        return _cache.GetOrAdd($"{culture.Name}:{key}", _ => GetStringFromResource(key, culture));
    }

    public string GetString(string key, params object[] args)
    {
        var template = GetString(key);
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

    // Observable.Defer computes start value at subscription time, not at creation
    public IObservable<string> GetObservable(string key)
    {
        return Observable.Defer(() => Observable.Return(GetString(key)))
            .Concat(LanguageChanged.Select(_ => GetString(key)))
            .DistinctUntilChanged();
    }

    public async Task SetLanguageAsync(string cultureCode, CancellationToken ct = default)
    {
        if (_currentCulture.Name == cultureCode) return;

        await _lock.WaitAsync(ct);
        try
        {
            var culture = new CultureInfo(cultureCode);
            _currentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
            CultureInfo.CurrentCulture = culture;

            _cache.Clear();

            _logger.LogInformation("Language changed to {Language} ({NativeName})", cultureCode, culture.NativeName);

            _languageChanged.OnNext(cultureCode);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Fetch one resource under the EXACT culture the caller snapshotted:
    /// the field is never re-read here, so a concurrent language switch
    /// cannot land between the key construction and the lookup (BUG-051).
    /// </summary>
    private string GetStringFromResource(string key, CultureInfo culture)
    {
        try
        {
            var value = _resourceManager.GetString(key, culture);
            if (value is null)
            {
                _logger.LogWarning("Localization key not found: {Key} for culture {Culture}", key, culture.Name);
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
        _lock.Dispose();
    }
}
