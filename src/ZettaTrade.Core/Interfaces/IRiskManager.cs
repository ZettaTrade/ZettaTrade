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
    /// Request human confirmation for an operation (pre-composed message).
    /// </summary>
    /// <param name="message">Confirmation message to display.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if confirmed, false if rejected or timeout.</returns>
    Task<bool> RequestConfirmationAsync(string message, CancellationToken ct = default);

    /// <summary>
    /// Request human confirmation displaying a localization template filled with arguments.
    /// The implementation resolves the template in the user's language, so callers
    /// never pass user-facing English text themselves.
    /// </summary>
    Task<bool> RequestConfirmationAsync(ConfirmationRequest request, CancellationToken ct = default);
}

/// <summary>
/// Structured confirmation message: a localization key plus template arguments.
/// Layered callers (e.g., MCP tools) only reference keys — UI layers remain the single
/// place that turns keys into human-readable text.
/// </summary>
public sealed record ConfirmationRequest
{
    /// <summary>Localization key of the confirmation template (e.g., "Trading.ConfirmPlaceOrderFormat").</summary>
    public required string MessageKey { get; init; }

    /// <summary>Arguments for the template, in string.Format order (pre-formatted with InvariantCulture).</summary>
    public object[] Arguments { get; init; } = [];
}
