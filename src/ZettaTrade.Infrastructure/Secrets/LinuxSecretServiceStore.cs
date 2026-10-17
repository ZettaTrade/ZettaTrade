namespace ZettaTrade.Infrastructure.Secrets;

using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// Linux Secret Service (D-Bus/libsecret) based secret store.
/// Works with GNOME Keyring, KWallet, KeePassXC, and other compatible daemons.
/// </summary>
public sealed class LinuxSecretServiceStore : ISecretStore
{
    private const string CollectionAlias = "default";
    private const string ServiceName = "ZettaTrade";
    private readonly ILogger _logger;

    // In production, use Tmds.DBus or a libsecret binding
    // This scaffold shows the interface — actual D-Bus calls are complex

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
            // Check if D-Bus session bus is available
            var dbusAddress = Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS");
            if (string.IsNullOrEmpty(dbusAddress))
                return false;

            // In production: try to connect to org.freedesktop.secrets on D-Bus
            // For scaffold, we check if secret-tool CLI is available
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            return path.Split(':').Any(dir =>
                File.Exists(Path.Combine(dir, "secret-tool")));
        }
        catch
        {
            return false;
        }
    }

    public Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        try
        {
            // Use secret-tool CLI as a portable approach
            // In production: use D-Bus directly via Tmds.DBus
            var result = RunCommand("secret-tool",
                $"lookup service {ServiceName} key {key}");
            return Task.FromResult<string?>(result?.Trim());
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Secret not found for key {Key}", key);
            return Task.FromResult<string?>(null);
        }
    }

    public Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        try
        {
            RunCommand("secret-tool",
                $"store --label=\"ZettaTrade: {key}\" service {ServiceName} key {key}");

            // secret-tool reads value from stdin
            _logger.LogDebug("Secret stored for key {Key}", key);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to store secret for key {Key}", key);
            throw;
        }

        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key, CancellationToken ct = default)
    {
        try
        {
            RunCommand("secret-tool",
                $"clear service {ServiceName} key {key}");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to remove secret for key {Key}", key);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListKeysAsync(string prefix = "", CancellationToken ct = default)
    {
        // Secret Service doesn't have a simple list API via CLI
        // In production: use D-Bus to enumerate attributes
        return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }

    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
        => Task.FromResult(IsAvailable());

    private static string? RunCommand(string command, string arguments)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = command,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start {command}");

        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            var error = process.StandardError.ReadToEnd();
            throw new InvalidOperationException($"{command} failed: {error}");
        }

        return output;
    }
}
