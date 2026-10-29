namespace ZettaTrade.Core.Interfaces;

using ZettaTrade.Core.Entities;

/// <summary>
/// Risk management service for validating orders before execution.
/// </summary>
public interface IRiskManager
{
    /// <summary>
    /// Check if an order passes risk controls.
    /// </summary>
    Task<RiskCheckResult> CheckOrderAsync(OrderRequest request, CancellationToken ct = default);
}

/// <summary>
/// Result of a risk check.
/// </summary>
public sealed record RiskCheckResult
{
    /// <summary>Whether the order is approved.</summary>
    public required bool IsApproved { get; init; }

    /// <summary>Whether human confirmation is required.</summary>
    public required bool RequiresConfirmation { get; init; }

    /// <summary>Rejection reason (if not approved).</summary>
    public string? Reason { get; init; }

    /// <summary>
    /// Create an approved result.
    /// </summary>
    public static RiskCheckResult Approved(bool requiresConfirmation = false) =>
        new() { IsApproved = true, RequiresConfirmation = requiresConfirmation };

    /// <summary>
    /// Create a rejected result.
    /// </summary>
    public static RiskCheckResult Rejected(string reason) =>
        new() { IsApproved = false, RequiresConfirmation = false, Reason = reason };
}

/// <summary>
/// Service for requesting human confirmation for high-risk operations.
/// </summary>
public interface IConfirmationService
{
    /// <summary>
    /// Request human confirmation for an operation.
    /// </summary>
    /// <param name="message">Confirmation message to display.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if confirmed, false if rejected or timeout.</returns>
    Task<bool> RequestConfirmationAsync(string message, CancellationToken ct = default);
}
