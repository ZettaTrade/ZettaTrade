namespace ZettaTrade.Infrastructure.Secrets;

using System.Runtime.Versioning;
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
[SupportedOSPlatform("windows")]
public sealed class WindowsDpapiSecretStore : ISecretStore, IAsyncInitializable
{
    private readonly string _storePath;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private Dictionary<string, byte[]> _cache = new();

    /// <summary>
    /// Create a new WindowsDpapiSecretStore instance.
    /// </summary>
    public WindowsDpapiSecretStore(ILogger logger)
    {
        _logger = logger;
        _storePath = Path.Combine(ConfigPathResolver.DataDirectory, "secrets.dat");
    }

    /// <summary>
    /// Initialize the secret store by loading secrets from disk.
    /// Must be called after construction.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await LoadStoreAsync();
    }

    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (!_cache.TryGetValue(key, out var encrypted))
                return null;

            try
            {
                var decrypted = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(decrypted);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to decrypt secret for key {Key}", key);
                return null;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var plaintext = Encoding.UTF8.GetBytes(value);
            var encrypted = ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser);
            _cache[key] = encrypted;
            await SaveStoreAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task RemoveAsync(string key, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_cache.Remove(key))
                await SaveStoreAsync();
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
            var keys = _cache.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
            return Task.FromResult<IReadOnlyList<string>>(keys);
        }
        finally
        {
            _lock.Release();
        }
    }

    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
        => Task.FromResult(OperatingSystem.IsWindows());

    private async Task LoadStoreAsync()
    {
        if (!File.Exists(_storePath))
            return;

        try
        {
            var json = await File.ReadAllTextAsync(_storePath);
            var data = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (data != null)
            {
                _cache = data.ToDictionary(kv => kv.Key, kv => Convert.FromBase64String(kv.Value));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load secret store from {Path}", _storePath);
        }
    }

    private async Task SaveStoreAsync()
    {
        try
        {
            var directory = Path.GetDirectoryName(_storePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            // Atomic write: write to temp file, then rename
            var tempPath = _storePath + ".tmp";
            var data = _cache.ToDictionary(kv => kv.Key, kv => Convert.ToBase64String(kv.Value));
            var json = JsonSerializer.Serialize(data);
            await File.WriteAllTextAsync(tempPath, json);
            File.Move(tempPath, _storePath, overwrite: true);

            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                File.SetUnixFileMode(_storePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save secret store to {Path}", _storePath);
        }
    }
}
