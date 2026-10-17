namespace ZettaTrade.Infrastructure.Configuration;

using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// JSON-file-backed settings service with reactive change notifications.
/// Supports hot-reload via file system watcher.
/// Thread-safe with SemaphoreSlim for async operations.
/// </summary>
public sealed class SettingsService : ISettingsService, IDisposable
{
    private readonly string _settingsFilePath;
    private readonly ILogger<SettingsService> _logger;
    private readonly Subject<SettingsChange> _changes = new();
    private readonly ConcurrentDictionary<string, JsonElement> _settings = new();
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private FileSystemWatcher? _watcher;
    private bool _disposed;
    private bool _isSaving; // Flag to prevent self-triggered reloads

    public SettingsService(
        string settingsFilePath,
        ILogger<SettingsService> logger)
    {
        _settingsFilePath = settingsFilePath;
        _logger = logger;

        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        LoadFromFile();
        StartWatching();
    }

    public IObservable<SettingsChange> Changes => _changes.Synchronize();

    public Task<T> GetAsync<T>(string section, string key, T defaultValue = default!)
    {
        // Use composite key with null char separator to avoid conflicts with keys containing ':'
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

        // Get old value for change notification
        JsonElement? oldValue = _settings.TryGetValue(fullKey, out var old) ? old : null;

        _settings.AddOrUpdate(fullKey, jsonElement, (_, _) => jsonElement);

        _changes.OnNext(new SettingsChange(section, key, oldValue, value));

        await SaveAsync();
    }

    public async Task SaveAsync(CancellationToken ct = default)
    {
        await _saveLock.WaitAsync(ct);
        try
        {
            _isSaving = true;

            var directory = Path.GetDirectoryName(_settingsFilePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            // Group by section for clean JSON output
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

            _logger.LogDebug("Settings saved to {Path}", _settingsFilePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save settings to {Path}", _settingsFilePath);
            throw;
        }
        finally
        {
            _isSaving = false;
            _saveLock.Release();
        }
    }

    public Task ReloadAsync(CancellationToken ct = default)
    {
        LoadFromFile();
        return Task.CompletedTask;
    }

    private void LoadFromFile()
    {
        if (!File.Exists(_settingsFilePath))
        {
            _logger.LogInformation("Settings file not found at {Path}, using defaults", _settingsFilePath);
            return;
        }

        try
        {
            var json = File.ReadAllText(_settingsFilePath);
            using var doc = JsonDocument.Parse(json);

            // Create new dictionary to avoid race conditions
            var newSettings = new ConcurrentDictionary<string, JsonElement>();

            foreach (var section in doc.RootElement.EnumerateObject())
            {
                if (section.Value.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in section.Value.EnumerateObject())
                    {
                        // Use null char separator to avoid conflicts
                        var fullKey = $"{section.Name}\0{prop.Name}";
                        newSettings[fullKey] = prop.Value.Clone();
                    }
                }
            }

            // Atomic swap
            _settings.Clear();
            foreach (var (key, value) in newSettings)
            {
                _settings[key] = value;
            }

            _logger.LogInformation("Settings loaded from {Path} ({Count} keys)", _settingsFilePath, _settings.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load settings from {Path}", _settingsFilePath);
        }
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
        // Ignore changes triggered by our own saves
        if (_isSaving)
            return;

        _logger.LogInformation("Settings file changed externally, reloading");
        LoadFromFile();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _watcher?.Dispose();
        _changes.Dispose();
        _saveLock.Dispose();
    }
}
