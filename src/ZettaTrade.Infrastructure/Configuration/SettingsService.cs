namespace ZettaTrade.Infrastructure.Configuration;

using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Text.Json.Serialization;
using Livery.Contracts;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// JSON-file-backed settings service with reactive change notifications.
/// Supports hot-reload via file system watcher.
/// Thread-safe with SemaphoreSlim for async operations.
/// Does NOT handle secrets — use ISecretStore directly for sensitive data.
/// </summary>
public sealed class SettingsService : ISettingsService, IAsyncInitializable, IDisposable
{
    /// <summary>Debounce window for external settings file reloads, in milliseconds.</summary>
    internal const int ExternalReloadDebounceMs = 1000;

    private readonly string _settingsFilePath;
    private readonly ILogger<SettingsService> _logger;
    private readonly Subject<SettingsChange> _changes = new();
    private readonly Subject<bool> _externalFileChanges = new();
    private readonly ConcurrentDictionary<string, JsonElement> _settings = new();
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private IDisposable? _reloadSubscription;
    private FileSystemWatcher? _watcher;
    private bool _disposed;

    /// <summary>
    /// The exact content the last successful save wrote. A file event carrying
    /// byte-identical content is the save's own echo, not an external edit: it
    /// is skipped on load regardless of WHEN it lands — the pre-fix
    /// <c>_isSaving</c> flag reset before the file event was delivered, so the
    /// guard never covered the whole delivery window (BUG-045).
    /// </summary>
    private string? _lastSavedContent;

    /// <summary>
    /// Create a new SettingsService instance.
    /// </summary>
    public SettingsService(string settingsFilePath, ILogger<SettingsService> logger)
    {
        _settingsFilePath = settingsFilePath;
        _logger = logger;

        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        // Debounced, latest-wins reload pipeline; LoadFromFileAsync logs its own failures.
        _reloadSubscription = _externalFileChanges
            .Sample(TimeSpan.FromMilliseconds(ExternalReloadDebounceMs))
            .Select(_ => Observable.FromAsync(() => LoadFromFileAsync()))
            .Switch()
            .Subscribe(
                _ => { },
                ex => _logger.LogError(ex, "Failed to reload settings after file change"));

        StartWatching();
    }

    /// <summary>
    /// Initialize the settings service by loading settings from disk.
    /// Must be called after construction.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await LoadFromFileAsync();
    }

    public IObservable<SettingsChange> Changes => _changes.Synchronize();

    public Task<T> GetAsync<T>(string section, string key, T defaultValue = default!)
    {
        // Use null char separator to avoid conflicts with keys containing ':'
        var fullKey = $"{section}\0{key}";

        if (_settings.TryGetValue(fullKey, out var element))
        {
            try
            {
                return Task.FromResult(element.Deserialize<T>(_jsonOptions) ?? defaultValue);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to deserialize setting {Section}:{Key}", section, key);
            }
        }

        return Task.FromResult(defaultValue);
    }

    public async Task SetAsync<T>(string section, string key, T value)
    {
        var fullKey = $"{section}\0{key}";
        var jsonElement = JsonSerializer.SerializeToElement(value, _jsonOptions);

        // The old-value payload shares the NEW value's type: the pre-fix event
        // boxed the raw file-model element while the new value was the typed T,
        // so subscribers comparing both always mis-handled one side (BUG-045).
        // A prior value that fails to deserialize to T degrades to null (no
        // substituted defaults).
        object? oldValue = null;
        if (_settings.TryGetValue(fullKey, out var oldElement))
        {
            try
            {
                oldValue = oldElement.Deserialize<T>(_jsonOptions);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "The previous value of {Section}:{Key} does not deserialize to {Type}; the change reports it as absent",
                    section,
                    key,
                    typeof(T).Name);
            }
        }

        // The memory mutation lives UNDER the save lock together with its
        // save: the pre-fix unlocked AddOrUpdate left a window where the
        // debounced reload ran between the set and its save, reloaded the
        // STALE file over the fresh memory and made the queued save persist
        // the stale snapshot — a lost write even with the reload serialized.
        await _saveLock.WaitAsync();
        try
        {
            _settings.AddOrUpdate(fullKey, jsonElement, (_, _) => jsonElement);

            _changes.OnNext(new SettingsChange(section, key, oldValue, value));

            await SaveCoreAsync();
        }
        finally
        {
            _saveLock.Release();
        }
    }

    /// <summary>Persist all pending changes to the settings file, serialized against reloads and other saves.</summary>
    public async Task SaveAsync(CancellationToken ct = default)
    {
        await _saveLock.WaitAsync(ct);
        try
        {
            await SaveCoreAsync(ct);
        }
        finally
        {
            _saveLock.Release();
        }
    }

    /// <summary>
    /// The locked file write itself; every caller already holds
    /// <see cref="_saveLock"/>, so the memory read, the byte-exact echo
    /// fingerprint and the file swap happen as one unit. A failed write
    /// propagates to the caller (it rethrows after logging).
    /// </summary>
    private async Task SaveCoreAsync(CancellationToken ct = default)
    {
        try
        {
            var directory = Path.GetDirectoryName(_settingsFilePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var grouped = new Dictionary<string, Dictionary<string, JsonElement>>();

            foreach (var (fullKey, element) in _settings)
            {
                var parts = fullKey.Split('\0');
                if (parts.Length != 2) continue;

                var section = parts[0];
                var key = parts[1];

                if (!grouped.ContainsKey(section))
                    grouped[section] = new Dictionary<string, JsonElement>();

                grouped[section][key] = element;
            }

            var json = JsonSerializer.Serialize(grouped, _jsonOptions);

            // Atomic write: write to temp file, then rename
            var tempPath = _settingsFilePath + ".tmp";
            await File.WriteAllTextAsync(tempPath, json, ct);
            File.Move(tempPath, _settingsFilePath, overwrite: true);

            // The fingerprint arms the echo guard only after a successful
            // write: a failed save must not let an equal-content external
            // write slip past unreported through a stale skip.
            _lastSavedContent = json;

            _logger.LogDebug("Settings saved to {Path}", _settingsFilePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save settings to {Path}", _settingsFilePath);
            throw;
        }
    }

    public async Task ReloadAsync(CancellationToken ct = default)
    {
        await LoadFromFileAsync(ct);
    }

    /// <summary>
    /// Load the file into memory. Every mutation of the shared state runs UNDER
    /// the save lock: the pre-fix unlock mutation (<c>Clear()</c> + repopulate)
    /// interleaved with <see cref="SetAsync{T}"/>/<see cref="SaveAsync"/> and
    /// erased just-written values, and the next save persisted the stale
    /// snapshot — the classic lost-write race (BUG-012). The same lock also
    /// serializes two concurrent reloads against each other.
    /// </summary>
    private async Task<int> LoadFromFileAsync(CancellationToken ct = default)
    {
        await _saveLock.WaitAsync(ct);
        try
        {
            if (!File.Exists(_settingsFilePath))
            {
                _logger.LogInformation("Settings file not found at {Path}, using defaults", _settingsFilePath);
                return 0;
            }

            try
            {
                var json = await File.ReadAllTextAsync(_settingsFilePath, ct);
                using var doc = JsonDocument.Parse(json);

                var newSettings = new Dictionary<string, JsonElement>();

                foreach (var section in doc.RootElement.EnumerateObject())
                {
                    if (section.Value.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var prop in section.Value.EnumerateObject())
                        {
                            var fullKey = $"{section.Name}\0{prop.Name}";
                            newSettings[fullKey] = prop.Value.Clone();
                        }
                    }
                }

                // The save's own echo: the file content is byte-identical to
                // what the last successful save wrote, so nothing changed —
                // applying it again (and publishing everything anew) would be
                // a pure self-trigger (BUG-045). The equality check covers the
                // whole delivery window no matter when the event lands.
                if (_lastSavedContent is not null && json == _lastSavedContent)
                {
                    return _settings.Count;
                }

                var changes = ApplyAndCollect(newSettings);

                _logger.LogInformation("Settings loaded from {Path} ({Count} keys)", _settingsFilePath, _settings.Count);

                // The publication happens AFTER the lock is released: a
                // subscriber re-entering the service must never find the save
                // lock still held (a deadlock on the very reload that made it
                // react).
                foreach (var change in changes)
                {
                    _changes.OnNext(change);
                }

                return _settings.Count;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load settings from {Path}", _settingsFilePath);
                return 0;
            }
        }
        finally
        {
            _saveLock.Release();
        }
    }

    /// <summary>
    /// Apply the freshly parsed snapshot and collect one change per difference
    /// between the old and the new state (BUG-013: the pre-fix reload replaced
    /// values but never published, so hot-reload consumers kept operating on
    /// stale data). Both payloads of a collected change are the file model's
    /// <see cref="JsonElement"/> — a consumer comparing old and new always
    /// sees one type; an absent side is null. The caller publishes the
    /// returned list once the save lock is released.
    /// </summary>
    private List<SettingsChange> ApplyAndCollect(IReadOnlyDictionary<string, JsonElement> newSettings)
    {
        var changes = new List<SettingsChange>();

        foreach (var fullKey in _settings.Keys.Union(newSettings.Keys))
        {
            var separator = fullKey.IndexOf('\0');
            var section = fullKey[..separator];
            var key = fullKey[(separator + 1)..];

            var oldExists = _settings.TryGetValue(fullKey, out var oldElement);
            var newExists = newSettings.TryGetValue(fullKey, out var newElement);

            if (oldExists && newExists && JsonElement.DeepEquals(oldElement, newElement))
                continue;

            changes.Add(new SettingsChange(
                section,
                key,
                oldExists ? (object)oldElement : null,
                newExists ? (object)newElement : null));
        }

        _settings.Clear();
        foreach (var (fullKey, element) in newSettings)
        {
            _settings[fullKey] = element;
        }

        return changes;
    }

    private void StartWatching()
    {
        var directory = Path.GetDirectoryName(_settingsFilePath);
        var fileName = Path.GetFileName(_settingsFilePath);

        if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(fileName))
            return;

        _watcher = new FileSystemWatcher(directory, fileName)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime,
            EnableRaisingEvents = true,
        };

        _watcher.Changed += OnFileChanged;
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        if (_disposed)
            return;

        _logger.LogInformation("Settings file changed externally, reloading");
        _externalFileChanges.OnNext(true);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _watcher?.Dispose();
        _reloadSubscription?.Dispose();
        _externalFileChanges.Dispose();
        _changes.Dispose();
        _saveLock.Dispose();
    }
}

