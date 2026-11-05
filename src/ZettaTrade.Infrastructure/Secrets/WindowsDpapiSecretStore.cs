namespace ZettaTrade.Infrastructure.Secrets;

using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Livery.Contracts;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Infrastructure.Configuration;

/// <summary>
/// Windows DPAPI-based secret store.
/// Uses ProtectedData which encrypts with the current Windows user's credentials.
/// Secrets are persisted to disk in encrypted form.
/// When the store file exists but cannot be read or parsed, initialization fails
/// loudly and the store refuses every operation afterwards: it never overwrites
/// its file from an empty or partial cache, so the on-disk secrets stay preserved
/// (unusable-not-lost) until the store file is fixed or removed.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsDpapiSecretStore : ISecretStore, IAsyncInitializable
{
    private readonly string _storePath;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private Dictionary<string, byte[]> _cache = new();
    private bool _loadFailed;

    /// <summary>
    /// Create a new WindowsDpapiSecretStore instance.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="storePath">
    /// Optional explicit store file path override (a test seam). Defaults to
    /// <c>secrets.dat</c> inside <see cref="ConfigPathResolver.DataDirectory"/>.
    /// </param>
    public WindowsDpapiSecretStore(ILogger logger, string? storePath = null)
    {
        _logger = logger;
        _storePath = storePath ?? Path.Combine(ConfigPathResolver.DataDirectory, "secrets.dat");
    }

    /// <summary>
    /// Initialize the secret store by loading secrets from disk.
    /// Must be called after construction.
    /// Fails loudly when the store file exists but cannot be read or parsed:
    /// from then on every operation refuses with an explicit error and the
    /// store file is never overwritten (the on-disk secrets stay preserved).
    /// The load runs under the same lock the read/write paths use: an early
    /// concurrent <see cref="SetAsync"/> must never be clobbered by the load's
    /// cache reassignment (and then erased from disk by the next save, BUG-039) —
    /// it queues and applies itself on top of the loaded state instead.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            await LoadStoreAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        EnsureUsable();
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
                // A decryption failure must never masquerade as a missing key:
                // the interface promises null ONLY for keys that are absent,
                // so corrupt unreadable bytes surface loudly instead (BUG-040).
                throw new InvalidOperationException(
                    $"Failed to decrypt the secret for key '{key}': the stored value is corrupt or "
                    + "unreadable (it is NOT a missing key, so nothing is silently substituted). The on-disk "
                    + "value stays preserved.",
                    ex);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        EnsureUsable();
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
        EnsureUsable();
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

    /// <summary>
    /// List the stored keys matching the prefix. Awaits the shared lock like
    /// every other contract method: a synchronous <c>Wait</c> inside an async
    /// contract method freezes (or deadlocks) the calling thread while a
    /// concurrent write holds the semaphore across file I/O (BUG-011).
    /// </summary>
    public async Task<IReadOnlyList<string>> ListKeysAsync(string prefix = "", CancellationToken ct = default)
    {
        EnsureUsable();
        await _lock.WaitAsync(ct);
        try
        {
            return _cache.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
        => Task.FromResult(OperatingSystem.IsWindows());

    /// <summary>
    /// Refuse every operation once the store failed to load: the whole point is
    /// that a corrupt or unreadable store file must never be overwritten with an
    /// empty or partial memory cache (the standing trading-safety rule), and the
    /// secrets must fail loudly instead of being silently reported as missing.
    /// </summary>
    private void EnsureUsable()
    {
        if (_loadFailed)
        {
            throw new InvalidOperationException(
                $"The Windows DPAPI secret store at '{_storePath}' failed to load (see the initialization log). "
                + "Stored secrets stay preserved on disk but are unusable; the store refuses operations and "
                + "never overwrites the store file. Fix or remove the store file, then restart the application.");
        }
    }

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
            _loadFailed = true;
            _logger.LogError(ex, "Failed to load secret store from {Path}; secrets on disk are preserved and the store refuses operations", _storePath);
            throw new InvalidOperationException(
                $"The Windows DPAPI secret store failed to load from '{_storePath}' ({ex.GetType().Name}). "
                + "Stored secrets stay preserved on disk but are unusable; the store refuses operations and "
                + "never overwrites the store file. Fix or remove the store file, then restart the application.",
                ex);
        }
    }

    /// <summary>
    /// Persist the cache atomically (write to a temp file, then rename). Any
    /// failure propagates to the caller: a store whose disk write failed must
    /// never claim success while keeping the secret memory-only (a reported
    /// success that vanishes after restart), and a removal whose save failed
    /// must not be silently undone (BUG-010).
    /// </summary>
    private async Task SaveStoreAsync()
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
}

