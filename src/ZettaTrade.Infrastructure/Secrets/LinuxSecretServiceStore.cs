namespace ZettaTrade.Infrastructure.Secrets;

using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// Linux Secret Service (D-Bus/libsecret) based secret store.
/// Works with GNOME Keyring, KWallet, KeePassXC, and other compatible daemons.
/// Uses secret-tool CLI with proper stdin handling for secret values.
/// </summary>
public sealed class LinuxSecretServiceStore : ISecretStore
{
    private const string ServiceName = "ZettaTrade";
    private const string AttributeKey = "key";
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _processLock = new(1, 1);

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
                ["service", ServiceName, AttributeKey, key],
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
            // CRITICAL: Must write value to stdin and close stream
            await RunSecretToolAsync(
                "store",
                ["--label", $"ZettaTrade: {key}", "service", ServiceName, AttributeKey, key],
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
                ["service", ServiceName, AttributeKey, key],
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
        // Secret Service doesn't have a simple list API via CLI
        // In production: use D-Bus directly via Tmds.DBus
        return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }

    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
        => Task.FromResult(IsAvailable());

    /// <summary>
    /// Run secret-tool command asynchronously with proper stdin handling and timeout.
    /// Uses ArgumentList to prevent injection attacks.
    /// </summary>
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
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            var psi = new ProcessStartInfo
            {
                FileName = "secret-tool",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = stdinData != null,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            // Use ArgumentList for safe argument passing (no injection)
            psi.ArgumentList.Add(command);
            foreach (var arg in arguments)
            {
                psi.ArgumentList.Add(arg);
            }

            using var process = new Process { StartInfo = psi };
            process.Start();

            // Write to stdin asynchronously if needed
            if (stdinData != null)
            {
                await process.StandardInput.WriteAsync(stdinData.AsMemory(), cts.Token);
                process.StandardInput.Close();
            }

            // Read stdout and stderr concurrently
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
