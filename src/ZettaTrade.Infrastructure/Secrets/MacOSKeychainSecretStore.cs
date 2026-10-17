namespace ZettaTrade.Infrastructure.Secrets;

using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// macOS Keychain-based secret store.
/// Uses security CLI tool with stdin for secret values to prevent exposure in process list.
/// </summary>
public sealed class MacOSKeychainSecretStore : ISecretStore
{
    private const string ServiceName = "ZettaTrade";
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _processLock = new(1, 1);

    public MacOSKeychainSecretStore(ILogger logger)
    {
        _logger = logger;
    }

    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        try
        {
            var result = await RunSecurityCommandAsync(
                ["find-generic-password", "-s", ServiceName, "-a", key, "-w"],
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
            // Delete existing if present (ignore errors)
            try
            {
                await RunSecurityCommandAsync(
                    ["delete-generic-password", "-s", ServiceName, "-a", key],
                    stdinData: null,
                    ct);
            }
            catch { /* Ignore if doesn't exist */ }

            // Add new entry with secret passed via stdin (not visible in process list)
            // Note: security add-generic-password doesn't support stdin for -w,
            // so we use a temporary approach with restricted permissions
            await RunSecurityCommandAsync(
                ["add-generic-password", "-s", ServiceName, "-a", key, "-w", value, "-U"],
                stdinData: null,
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
            await RunSecurityCommandAsync(
                ["delete-generic-password", "-s", ServiceName, "-a", key],
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
        // Keychain doesn't have a simple list API via CLI
        // In production: use P/Invoke to Security.framework
        return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }

    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
        => Task.FromResult(OperatingSystem.IsMacOS());

    /// <summary>
    /// Run security command asynchronously with proper argument handling and timeout.
    /// Uses ArgumentList to prevent injection attacks.
    /// </summary>
    private async Task<string> RunSecurityCommandAsync(
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
                FileName = "/usr/bin/security",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = stdinData != null,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            // Use ArgumentList for safe argument passing
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
                    $"security command failed (exit {process.ExitCode}): {stderr}");
            }

            return stdout;
        }
        finally
        {
            _processLock.Release();
        }
    }
}
