namespace ZettaTrade.Infrastructure.Secrets;

using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// Factory for creating platform-appropriate secret stores.
/// Auto-detects the current OS and returns the best available implementation.
/// </summary>
public static class SecretStoreFactory
{
    /// <summary>
    /// Create the best available secret store for the current platform.
    /// </summary>
    /// <remarks>
    /// For EncryptedFileSecretStore (Linux fallback), InitializeAsync must be called separately with master password.
    /// </remarks>
    public static ISecretStore Create(ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("SecretStore");

        if (OperatingSystem.IsWindows())
        {
            logger.LogInformation("Using Windows DPAPI secret store");
            return new WindowsDpapiSecretStore(loggerFactory.CreateLogger<WindowsDpapiSecretStore>());
        }

        if (OperatingSystem.IsMacOS())
        {
            logger.LogInformation("Using macOS Keychain secret store");
            return new MacOSKeychainSecretStore(loggerFactory.CreateLogger<MacOSKeychainSecretStore>());
        }

        if (OperatingSystem.IsLinux())
        {
            if (LinuxSecretServiceStore.IsAvailable())
            {
                logger.LogInformation("Using Linux Secret Service (libsecret/D-Bus)");
                return new LinuxSecretServiceStore(loggerFactory.CreateLogger<LinuxSecretServiceStore>());
            }

            logger.LogWarning(
                "Secret Service not available on Linux. " +
                "Falling back to encrypted file store. " +
                "Install libsecret-1-0 and a keyring daemon for better security.");
            return new EncryptedFileSecretStore(loggerFactory.CreateLogger<EncryptedFileSecretStore>());
        }

        throw new PlatformNotSupportedException(
            $"No secret store available for platform: {Environment.OSVersion.Platform}");
    }

    /// <summary>
    /// Check if the created secret store requires initialization (EncryptedFileSecretStore).
    /// </summary>
    public static bool RequiresInitialization(ISecretStore store)
    {
        return store is EncryptedFileSecretStore;
    }
}
