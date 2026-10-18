namespace ZettaTrade.Infrastructure.Secrets;

using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Constants;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// Linux Secret Service (D-Bus/libsecret) based secret store.
/// Works with GNOME Keyring, KWallet, KeePassXC, and other compatible daemons.
/// Uses secret-tool CLI with proper stdin handling for secret values.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxSecretServiceStore : ISecretStore
{
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _processLock = new(1, 1);

    /// <summary>
    /// Create a new LinuxSecretServiceStore instance.
    /// </summary>
    public LinuxSecretServiceStore(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Check if a Secret Service daemon is available on D-Bus.
    /// </summary>
    public static bool IsAvailable()
    {
        try
        {
            var dbusAddress = Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS");
            if (string.IsNullOrEmpty(dbusAddress))
                return false;

            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            return path.Split(Path.PathSeparator).Any(dir =>
                File.Exists(Path.Combine(dir, "secret-tool")));
        }
        catch
        {
            return false;
        }
    }

    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        try
        {
            var result = await RunSecretToolAsync(
                "lookup",
                ["service", SecretStoreConstants.ServiceName, SecretStoreConstants.DefaultAttributeKey, key],
                stdinData: null,
                ct);
            return string.IsNullOrWhiteSpace(result) ? null : result.Trim();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Secret not found for key {Key}", key);
            return null;
        }
    }

    public async Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        try
        {
            // secret-tool store reads the secret value from stdin
            await RunSecretToolAsync(
                "store",
                ["--label", $"ZettaTrade: {key}", "service", SecretStoreConstants.ServiceName, SecretStoreConstants.DefaultAttributeKey, key],
                stdinData: value,
                ct);

            _logger.LogDebug("Secret stored for key {Key}", key);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to store secret for key {Key}", key);
            throw;
        }
    }

    public async Task RemoveAsync(string key, CancellationToken ct = default)
    {
        try
        {
            await RunSecretToolAsync(
                "clear",
                ["service", SecretStoreConstants.ServiceName, SecretStoreConstants.DefaultAttributeKey, key],
                stdinData: null,
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to remove secret for key {Key} (may not exist)", key);
        }
    }

    public Task<IReadOnlyList<string>> ListKeysAsync(string prefix = "", CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }

    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
        => Task.FromResult(IsAvailable());

    private async Task<string> RunSecretToolAsync(
        string command,
        string[] arguments,
        string? stdinData,
        CancellationToken ct)
    {
        await _processLock.WaitAsync(ct);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(SecretStoreConstants.OperationTimeoutSeconds));

            var psi = new ProcessStartInfo
            {
                FileName = "secret-tool",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = stdinData != null,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            psi.ArgumentList.Add(command);
            foreach (var arg in arguments)
            {
                psi.ArgumentList.Add(arg);
            }

            using var process = new Process { StartInfo = psi };
            process.Start();

            if (stdinData != null)
            {
                await process.StandardInput.WriteAsync(stdinData.AsMemory(), cts.Token);
                process.StandardInput.Close();
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);

            await process.WaitForExitAsync(cts.Token);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"secret-tool {command} failed (exit {process.ExitCode}): {stderr}");
            }

            return stdout;
        }
        finally
        {
            _processLock.Release();
        }
    }
}
