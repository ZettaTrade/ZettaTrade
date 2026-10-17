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
    private const int Iterations = 600_000; // PBKDF2 iterations
    private readonly string _filePath;
    private readonly ILogger _logger;
    private readonly Dictionary<string, string> _secrets = [];
    private byte[]? _encryptionKey;
    private readonly object _lock = new();

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
        _encryptionKey = await DeriveKeyAsync(masterPassword, ct);
        await LoadAsync(ct);
    }

    public Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        lock (_lock)
        {
            _secrets.TryGetValue(key, out var value);
            return Task.FromResult(value);
        }
    }

    public async Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        lock (_lock)
        {
            _secrets[key] = value;
        }

        await SaveAsync(ct);
    }

    public async Task RemoveAsync(string key, CancellationToken ct = default)
    {
        lock (_lock)
        {
            _secrets.Remove(key);
        }

        await SaveAsync(ct);
    }

    public Task<IReadOnlyList<string>> ListKeysAsync(string prefix = "", CancellationToken ct = default)
    {
        lock (_lock)
        {
            var keys = _secrets.Keys
                .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
                .ToList();
            return Task.FromResult<IReadOnlyList<string>>(keys);
        }
    }

    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
        => Task.FromResult(true); // Always available (file-based)

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
        return salt;
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        if (!File.Exists(_filePath) || _encryptionKey is null)
            return;

        try
        {
            var encryptedData = await File.ReadAllBytesAsync(_filePath, ct);

            // Format: [12-byte nonce][encrypted payload]
            if (encryptedData.Length < 12)
                return;

            var nonce = encryptedData[..12];
            var ciphertext = encryptedData[12..];

            using var aes = new AesGcm(_encryptionKey, tagSize: 16);
            var plaintext = new byte[ciphertext.Length - 16];
            var tag = ciphertext[^16..];
            var actualCiphertext = ciphertext[..^16];

            aes.Decrypt(nonce, actualCiphertext, tag, plaintext);

            var json = Encoding.UTF8.GetString(plaintext);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(json);

            if (loaded is not null)
            {
                lock (_lock)
                {
                    foreach (var kv in loaded)
                        _secrets[kv.Key] = kv.Value;
                }
            }

            _logger.LogDebug("Loaded {Count} secrets from encrypted store", _secrets.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load encrypted secret store");
        }
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        if (_encryptionKey is null)
            throw new InvalidOperationException("Store not initialized. Call InitializeAsync first.");

        try
        {
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

            await File.WriteAllBytesAsync(_filePath, output, ct);

            _logger.LogDebug("Saved {Count} secrets to encrypted store", _secrets.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save encrypted secret store");
            throw;
        }
    }
}
