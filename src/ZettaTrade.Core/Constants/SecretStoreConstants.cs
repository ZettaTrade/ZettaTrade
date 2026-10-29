namespace ZettaTrade.Core.Constants;

/// <summary>
/// Constants for secret storage implementations.
/// </summary>
public static class SecretStoreConstants
{
    /// <summary>
    /// Service name used in platform-specific secret stores (Keychain, Secret Service, etc.).
    /// </summary>
    public const string ServiceName = ApplicationConstants.ApplicationName;

    /// <summary>
    /// Default attribute key for secret storage.
    /// </summary>
    public const string DefaultAttributeKey = "key";

    /// <summary>
    /// Timeout in seconds for secret store operations.
    /// </summary>
    public const int OperationTimeoutSeconds = 10;
}
