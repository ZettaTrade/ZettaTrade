namespace Livery.Contracts;

/// <summary>
/// Interface for services that require asynchronous initialization.
/// Implement this interface when a service needs to load data from disk,
/// connect to external resources, or perform other async operations during startup.
/// </summary>
public interface IAsyncInitializable
{
    /// <summary>
    /// Initialize the service asynchronously.
    /// Must be called after construction and before using the service.
    /// </summary>
    Task InitializeAsync(CancellationToken ct = default);
}
