namespace ZettaTrade.Infrastructure.Secrets;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Infrastructure.Configuration;

/// <summary>
/// Windows DPAPI-based secret store.
/// Uses ProtectedData which encrypts with the current Windows user's credentials.
/// Secrets are persisted to disk in encrypted form.
/// </summary>
public sealed class WindowsDpapiSecretStore : ISecretStore
{
    private readonly string _storePath;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private Dictionary<string, byte[]> _cache = new();

    public WindowsDpapiSecretStore(ILogger logger)
    {
        _logger = logger;
        _storePath = Path.Combine(ConfigPathResolver.DataDirectory, "secrets.dat");
        LoadStore();
    }

    public Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        _lock.Wait(ct);
        try
        {
            if (!_cache.TryGetValue(key, out var encrypted))
                return Task.FromResult<string?>(null);

            try
            {
                var decrypted = ProtectedData.Unprotect(
                    encrypted,
                    additionalEntropy: null,
                    DataProtectionScope.CurrentUser);
                return Task.FromResult<string?>(Encoding.UTF8.GetString(decrypted));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to decrypt secret for key {Key}", key);
                return Task.FromResult<string?>(null);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        _lock.Wait(ct);
        try
        {
            var plaintext = Encoding.UTF8.GetBytes(value);
            var encrypted = ProtectedData.Protect(
                plaintext,
                additionalEntropy: null,
                DataProtectionScope.CurrentUser);
            _cache[key] = encrypted;
            
            // CRITICAL: Must save to disk immediately
            SaveStore();
            
            return Task.CompletedTask;
        }
        finally
        {
            _lock.Release();
        }
    }

    public Task RemoveAsync(string key, CancellationToken ct = default)
    {
        _lock.Wait(ct);
        try
        {
            if (_cache.Remove(key))
                SaveStore();
            return Task.CompletedTask;
        }
        finally
        {
            _lock.Release();
        }
    }

    public Task<IReadOnlyList<string>> ListKeysAsync(string prefix = "", CancellationToken ct = default)
    {
        _lock.Wait(ct);
        try
        {
            var keys = _cache.Keys
                .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
                .ToList();
            return Task.FromResult<IReadOnlyList<string>>(keys);
        }
        finally
        {
            _lock.Release();
        }
    }

    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
        => Task.FromResult(OperatingSystem.IsWindows());

    private void LoadStore()
    {
        if (!File.Exists(_storePath))
            return;

        try
        {
            var json = File.ReadAllText(_storePath);
            var data = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (data != null)
            {
                _cache = data.ToDictionary(
                    kv => kv.Key,
                    kv => Convert.FromBase64String(kv.Value));
            }
            _logger.LogDebug("Loaded {Count} secrets from {Path}", _cache.Count, _storePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load secret store from {Path}", _storePath);
        }
    }

    private void SaveStore()
    {
        try
        {
            var directory = Path.GetDirectoryName(_storePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            // Atomic write: write to temp file, then rename
            var tempPath = _storePath + ".tmp";
            var data = _cache.ToDictionary(
                kv => kv.Key,
                kv => Convert.ToBase64String(kv.Value));
            var json = JsonSerializer.Serialize(data);
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _storePath, overwrite: true);

            // Set restrictive permissions on Unix
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                File.SetUnixFileMode(_storePath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            _logger.LogDebug("Saved {Count} secrets to {Path}", _cache.Count, _storePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save secret store to {Path}", _storePath);
        }
    }
}
