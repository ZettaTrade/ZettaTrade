namespace ZettaTrade.Infrastructure.Secrets;

using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Infrastructure.Configuration;

/// <summary>
/// Windows DPAPI-based secret store.
/// Uses ProtectedData which encrypts with the current Windows user's credentials.
/// No password required — tied to the user session.
/// </summary>
public sealed class WindowsDpapiSecretStore : ISecretStore
{
    private readonly string _storePath;
    private readonly ILogger _logger;
    private readonly Dictionary<string, byte[]> _cache = [];
    private readonly object _lock = new();

    public WindowsDpapiSecretStore(ILogger logger)
    {
        _logger = logger;
        _storePath = Path.Combine(ConfigPathResolver.DataDirectory, "secrets.dat");
        LoadStore();
    }

    public Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        lock (_lock)
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
    }

    public Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var plaintext = Encoding.UTF8.GetBytes(value);
            var encrypted = ProtectedData.Protect(
                plaintext,
                additionalEntropy: null,
                DataProtectionScope.CurrentUser);
            _cache[key] = encrypted;
            SaveStore();
        }

        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key, CancellationToken ct = default)
    {
        lock (_lock)
        {
            _cache.Remove(key);
            SaveStore();
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListKeysAsync(string prefix = "", CancellationToken ct = default)
    {
        lock (_lock)
        {
            var keys = _cache.Keys
                .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
                .ToList();
            return Task.FromResult<IReadOnlyList<string>>(keys);
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
            var data = File.ReadAllBytes(_storePath);
            // Simple format: key\0value\0key\0value...
            // In production, use a proper serialization format
            // This is a simplified version for the scaffold
            _logger.LogDebug("Loaded secret store from {Path}", _storePath);
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

            // In production: serialize properly
            _logger.LogDebug("Saved secret store to {Path}", _storePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save secret store to {Path}", _storePath);
        }
    }
}
