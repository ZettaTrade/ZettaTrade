namespace ZettaTrade.Infrastructure.Secrets;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Infrastructure.Configuration;

/// <summary>
/// Encrypted file-based secret store.
/// Uses AES-256-GCM with a master password derived via PBKDF2.
/// Fallback for environments without native secret storage (headless Linux, CI).
/// </summary>
public sealed class EncryptedFileSecretStore : ISecretStore
{
    private const int KeySize = 256;
    private const int Iterations = 600_000;
    private readonly string _filePath;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private Dictionary<string, string> _secrets = new();
    private byte[]? _encryptionKey;
    private bool _initialized;

    public EncryptedFileSecretStore(ILogger logger)
    {
        _logger = logger;
        _filePath = Path.Combine(ConfigPathResolver.DataDirectory, "secrets.enc");
    }

    /// <summary>
    /// Initialize the store with a master password.
    /// Must be called before any Get/Set operations.
    /// </summary>
    public async Task InitializeAsync(string masterPassword, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            _encryptionKey = await DeriveKeyAsync(masterPassword, ct);
            await LoadAsync(ct);
            _initialized = true;
            _logger.LogInformation("Encrypted secret store initialized");
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        if (!_initialized)
            throw new InvalidOperationException("Store not initialized. Call InitializeAsync first.");

        await _lock.WaitAsync(ct);
        try
        {
            return _secrets.TryGetValue(key, out var value) ? value : null;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        if (!_initialized)
            throw new InvalidOperationException("Store not initialized. Call InitializeAsync first.");

        await _lock.WaitAsync(ct);
        try
        {
            _secrets[key] = value;
            await SaveAsync(ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task RemoveAsync(string key, CancellationToken ct = default)
    {
        if (!_initialized)
            throw new InvalidOperationException("Store not initialized. Call InitializeAsync first.");

        await _lock.WaitAsync(ct);
        try
        {
            if (_secrets.Remove(key))
                await SaveAsync(ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<string>> ListKeysAsync(string prefix = "", CancellationToken ct = default)
    {
        if (!_initialized)
            throw new InvalidOperationException("Store not initialized. Call InitializeAsync first.");

        await _lock.WaitAsync(ct);
        try
        {
            var keys = _secrets.Keys
                .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
                .ToList();
            return keys;
        }
        finally
        {
            _lock.Release();
        }
    }

    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
        => Task.FromResult(true);

    private async Task<byte[]> DeriveKeyAsync(string password, CancellationToken ct)
    {
        var salt = await GetOrCreateSaltAsync(ct);

        using var pbkdf2 = new Rfc2898DeriveBytes(
            password,
            salt,
            Iterations,
            HashAlgorithmName.SHA256);

        return pbkdf2.GetBytes(KeySize / 8);
    }

    private async Task<byte[]> GetOrCreateSaltAsync(CancellationToken ct)
    {
        var saltPath = _filePath + ".salt";

        if (File.Exists(saltPath))
        {
            return await File.ReadAllBytesAsync(saltPath, ct);
        }

        var salt = RandomNumberGenerator.GetBytes(32);
        var directory = Path.GetDirectoryName(saltPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(saltPath, salt, ct);

        // Set restrictive permissions on Unix
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            File.SetUnixFileMode(saltPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        return salt;
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        if (!File.Exists(_filePath) || _encryptionKey is null)
            return;

        try
        {
            var encryptedData = await File.ReadAllBytesAsync(_filePath, ct);

            // Format: [12-byte nonce][encrypted payload with tag]
            if (encryptedData.Length < 12)
            {
                _logger.LogWarning("Secret store file is too small, starting fresh");
                return;
            }

            var nonce = encryptedData[..12];
            var ciphertextWithTag = encryptedData[12..];

            using var aes = new AesGcm(_encryptionKey, tagSize: 16);
            
            // Split ciphertext and tag
            if (ciphertextWithTag.Length < 16)
            {
                _logger.LogWarning("Secret store file is corrupted, starting fresh");
                return;
            }

            var ciphertext = ciphertextWithTag[..^16];
            var tag = ciphertextWithTag[^16..];
            var plaintext = new byte[ciphertext.Length];

            // Decrypt - will throw CryptographicException if password is wrong
            aes.Decrypt(nonce, ciphertext, tag, plaintext);

            var json = Encoding.UTF8.GetString(plaintext);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(json);

            if (loaded is not null)
            {
                _secrets = loaded;
                _logger.LogDebug("Loaded {Count} secrets from encrypted store", _secrets.Count);
            }
        }
        catch (CryptographicException ex)
        {
            _logger.LogError(ex, "Failed to decrypt secret store - wrong password or corrupted file");
            // CRITICAL: Do NOT clear _secrets or overwrite file on decryption failure
            // This prevents data loss when wrong password is provided
            throw new InvalidOperationException("Failed to decrypt secret store. Wrong password?", ex);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse secret store JSON - file corrupted");
            // CRITICAL: Do NOT clear _secrets or overwrite file on parse failure
            throw new InvalidOperationException("Failed to parse secret store. File corrupted?", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load encrypted secret store");
            throw;
        }
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        if (_encryptionKey is null)
            throw new InvalidOperationException("Store not initialized. Call InitializeAsync first.");

        try
        {
            // Serialize inside lock to prevent race conditions
            var json = JsonSerializer.Serialize(_secrets);
            var plaintext = Encoding.UTF8.GetBytes(json);

            using var aes = new AesGcm(_encryptionKey, tagSize: 16);
            var nonce = RandomNumberGenerator.GetBytes(12);
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[16];

            aes.Encrypt(nonce, plaintext, ciphertext, tag);

            // Format: [nonce][ciphertext][tag]
            var output = new byte[nonce.Length + ciphertext.Length + tag.Length];
            Buffer.BlockCopy(nonce, 0, output, 0, nonce.Length);
            Buffer.BlockCopy(ciphertext, 0, output, nonce.Length, ciphertext.Length);
            Buffer.BlockCopy(tag, 0, output, nonce.Length + ciphertext.Length, tag.Length);

            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            // Atomic write: write to temp file, then rename
            var tempPath = _filePath + ".tmp";
            await File.WriteAllBytesAsync(tempPath, output, ct);
            File.Move(tempPath, _filePath, overwrite: true);

            // Set restrictive permissions on Unix
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                File.SetUnixFileMode(_filePath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            _logger.LogDebug("Saved {Count} secrets to encrypted store", _secrets.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save encrypted secret store");
            throw;
        }
    }
}
