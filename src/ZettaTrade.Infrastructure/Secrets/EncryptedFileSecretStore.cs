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
    private const int NonceSizeBytes = 12;
    private const int TagSizeBytes = 16;
    private const int SaltSizeBytes = 32;

    /// <summary>
    /// The smallest file size a valid store can have (nonce + at least one
    /// byte of ciphertext + tag); anything shorter is truncated content, not
    /// an empty store — an empty store is a MISSING file.
    /// </summary>
    private const int MinimumFileSizeBytes = NonceSizeBytes + TagSizeBytes;

    private readonly string _filePath;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private Dictionary<string, string> _secrets = new();
    private byte[]? _encryptionKey;
    private bool _initialized;

    /// <summary>
    /// Create a new EncryptedFileSecretStore instance.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="filePath">
    /// Optional explicit store file path override (a test seam). Defaults to
    /// <c>secrets.enc</c> inside <see cref="ConfigPathResolver.DataDirectory"/>.
    /// </param>
    public EncryptedFileSecretStore(ILogger logger, string? filePath = null)
    {
        _logger = logger;
        _filePath = filePath ?? Path.Combine(ConfigPathResolver.DataDirectory, "secrets.enc");
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

        // Use static Pbkdf2 method (modern API, non-obsolete)
        return Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            Iterations,
            HashAlgorithmName.SHA256,
            KeySize / 8);
    }

    private async Task<byte[]> GetOrCreateSaltAsync(CancellationToken ct)
    {
        var saltPath = _filePath + ".salt";

        if (File.Exists(saltPath))
        {
            return await File.ReadAllBytesAsync(saltPath, ct);
        }

        var salt = RandomNumberGenerator.GetBytes(SaltSizeBytes);
        var directory = Path.GetDirectoryName(saltPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(saltPath, salt, ct);

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            File.SetUnixFileMode(saltPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        return salt;
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        if (!File.Exists(_filePath) || _encryptionKey is null)
            return;

        var encryptedData = await File.ReadAllBytesAsync(_filePath, ct);

        // A too-short file is TRUNCATED CONTENT, never an empty store: an
        // empty store is a missing file, which the missing-file branch above
        // already handles. Starting fresh here would mark the store healthy
        // on unreadable bytes, so the next SetAsync would overwrite the
        // undecryptable file — the standing never-overwrite rule forbids
        // exactly that (BUG-041), so the corruption surfaces loudly instead.
        if (encryptedData.Length < MinimumFileSizeBytes)
        {
            _logger.LogError(
                "The encrypted secret store file {Path} is truncated ({Length} bytes < {Minimum}): the store refuses to start fresh so the unreadable content is never overwritten",
                _filePath,
                encryptedData.Length,
                MinimumFileSizeBytes);
            throw new InvalidOperationException(
                $"The encrypted secret store file '{_filePath}' is truncated or corrupt "
                + $"({encryptedData.Length} bytes < {MinimumFileSizeBytes}). Fix or remove the store "
                + "file, then re-initialize; the store never overwrites the unreadable content.");
        }

        try
        {
            var nonce = encryptedData[..NonceSizeBytes];
            var ciphertextWithTag = encryptedData[NonceSizeBytes..];

            using var aes = new AesGcm(_encryptionKey, TagSizeBytes);

            var ciphertext = ciphertextWithTag[..^TagSizeBytes];
            var tag = ciphertextWithTag[^TagSizeBytes..];
            var plaintext = new byte[ciphertext.Length];

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
            // Do NOT clear _secrets or overwrite file on decryption failure
            _logger.LogError(ex, "Failed to decrypt secret store - wrong password or corrupted file");
            throw new InvalidOperationException("Failed to decrypt secret store. Wrong password?", ex);
        }
        catch (JsonException ex)
        {
            // Do NOT clear _secrets or overwrite file on parse failure
            _logger.LogError(ex, "Failed to parse secret store JSON - file corrupted");
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
            var json = JsonSerializer.Serialize(_secrets);
            var plaintext = Encoding.UTF8.GetBytes(json);

            using var aes = new AesGcm(_encryptionKey, TagSizeBytes);
            var nonce = RandomNumberGenerator.GetBytes(NonceSizeBytes);
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[TagSizeBytes];

            aes.Encrypt(nonce, plaintext, ciphertext, tag);

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

            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                File.SetUnixFileMode(_filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
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
