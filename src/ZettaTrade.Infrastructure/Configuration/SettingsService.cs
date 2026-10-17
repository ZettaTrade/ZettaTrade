namespace ZettaTrade.Infrastructure.Configuration;

using System.Collections.Concurrent;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// JSON-file-backed settings service with reactive change notifications.
/// Supports hot-reload via file system watcher.
/// </summary>
public sealed class SettingsService : ISettingsService, IDisposable
{
    private readonly string _settingsFilePath;
    private readonly ISecretStore _secretStore;
    private readonly ILogger<SettingsService> _logger;
    private readonly Subject<SettingsChange> _changes = new();
    private readonly ConcurrentDictionary<string, JsonElement> _settings = new();
    private readonly JsonSerializerOptions _jsonOptions;
    private FileSystemWatcher? _watcher;
    private bool _disposed;

    public SettingsService(
        string settingsFilePath,
        ISecretStore secretStore,
        ILogger<SettingsService> logger)
    {
        _settingsFilePath = settingsFilePath;
        _secretStore = secretStore;
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

    public IObservable<SettingsChange> Changes => _changes;

    public Task<T> GetAsync<T>(string section, string key, T defaultValue = default!)
    {
        var fullKey = $"{section}:{key}";

        if (_settings.TryGetValue(fullKey, out var element))
        {
            try
            {
                return Task.FromResult(element.Deserialize<T>(_jsonOptions) ?? defaultValue);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to deserialize setting {Key}", fullKey);
            }
        }

        return Task.FromResult(defaultValue);
    }

    public async Task SetAsync<T>(string section, string key, T value)
    {
        var fullKey = $"{section}:{key}";
        var jsonElement = JsonSerializer.SerializeToElement(value, _jsonOptions);

        _settings.AddOrUpdate(fullKey, jsonElement, (_, _) => jsonElement);

        _changes.OnNext(new SettingsChange(section, key, OldValue: null, NewValue: value));

        await SaveAsync();
    }

    public Task<string?> GetSecretAsync(string key, CancellationToken ct = default)
        => _secretStore.GetAsync($"settings:{key}", ct);

    public Task SetSecretAsync(string key, string value, CancellationToken ct = default)
        => _secretStore.SetAsync($"settings:{key}", value, ct);

    public Task RemoveSecretAsync(string key, CancellationToken ct = default)
        => _secretStore.RemoveAsync($"settings:{key}", ct);

    public async Task SaveAsync(CancellationToken ct = default)
    {
        try
        {
            var directory = Path.GetDirectoryName(_settingsFilePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            // Group by section for clean JSON output
            var grouped = _settings
                .GroupBy(kv => kv.Key.Split(':')[0])
                .ToDictionary(
                    g => g.Key,
                    g => g.ToDictionary(
                        kv => kv.Key.Split(':')[1],
                        kv => kv.Value));

            var json = JsonSerializer.Serialize(grouped, _jsonOptions);
            await File.WriteAllTextAsync(_settingsFilePath, json, ct);

            _logger.LogDebug("Settings saved to {Path}", _settingsFilePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save settings to {Path}", _settingsFilePath);
            throw;
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

            _settings.Clear();

            foreach (var section in doc.RootElement.EnumerateObject())
            {
                if (section.Value.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in section.Value.EnumerateObject())
                    {
                        var fullKey = $"{section.Name}:{prop.Name}";
                        _settings[fullKey] = prop.Value.Clone();
                    }
                }
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

        _watcher.Changed += (_, _) =>
        {
            _logger.LogInformation("Settings file changed externally, reloading");
            LoadFromFile();
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _watcher?.Dispose();
        _changes.Dispose();
    }
}
