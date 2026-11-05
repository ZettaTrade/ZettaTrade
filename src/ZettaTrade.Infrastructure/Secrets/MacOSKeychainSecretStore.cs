namespace ZettaTrade.Infrastructure.Secrets;

using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Constants;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// macOS Keychain-based secret store.
/// Uses the <c>security</c> CLI; every secret value travels via stdin, never
/// as a command-line argument (which would expose it in the process list).
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacOSKeychainSecretStore : ISecretStore
{
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _processLock = new(1, 1);

    /// <summary>
    /// Create a new MacOSKeychainSecretStore instance.
    /// </summary>
    public MacOSKeychainSecretStore(ILogger logger)
    {
        _logger = logger;
    }

    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        try
        {
            var result = await RunSecurityCommandAsync(
                ["find-generic-password", "-s", SecretStoreConstants.ServiceName, "-a", key, "-w"],
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
            // "-U" updates an existing item in place inside the same CLI call:
            // the standalone destroy-then-create update was non-atomic (a
            // failure or crash between the two steps lost the old secret,
            // BUG-038). And with "-w" carrying no value, security reads the
            // secret from STDIN (the secret never appears as a process
            // argument, BUG-009).
            await RunSecurityCommandAsync(
                ["add-generic-password", "-s", SecretStoreConstants.ServiceName, "-a", key, "-U", "-w"],
                stdinData: value + "\n",
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
                ["delete-generic-password", "-s", SecretStoreConstants.ServiceName, "-a", key],
                stdinData: null,
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to remove secret for key {Key} (may not exist)", key);
        }
    }

    /// <summary>
    /// List the keys of this application's generic passwords by parsing the
    /// keychain's attribute dump: <c>dump-keychain</c> reports each item's
    /// metadata (service, account) without touching secret values, so no
    /// keychain prompt fires; the service attribute selects ZettaTrade's own
    /// items only (BUG-036).
    /// </summary>
    public async Task<IReadOnlyList<string>> ListKeysAsync(string prefix = "", CancellationToken ct = default)
    {
        var dump = await RunSecurityCommandAsync(
            ["dump-keychain"],
            stdinData: null,
            ct);

        return SecretListOutputParsers.ParseKeychainDumpAccounts(dump)
            .Where(account => account.StartsWith(prefix, StringComparison.Ordinal))
            .OrderBy(account => account, StringComparer.Ordinal)
            .ToList();
    }

    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
        => Task.FromResult(OperatingSystem.IsMacOS());

    private async Task<string> RunSecurityCommandAsync(
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
                FileName = "/usr/bin/security",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = stdinData != null,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            foreach (var arg in arguments)
            {
                psi.ArgumentList.Add(arg);
            }

            using var process = new Process { StartInfo = psi };
            process.Start();

            try
            {
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
            catch (Exception)
            {
                // The child must die with its invocation: a timeout (or any
                // other failure) leaves no orphan still holding an open
                // keychain prompt or lock (BUG-037).
                SecretProcessGuard.KillUnfinished(process);
                throw;
            }
        }
        finally
        {
            _processLock.Release();
        }
    }
}
