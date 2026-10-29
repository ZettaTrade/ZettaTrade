namespace ZettaTrade.App.Services;

using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// Default risk manager implementation.
/// Provides basic risk checks for orders.
/// </summary>
public sealed class DefaultRiskManager : IRiskManager
{
    private readonly ILogger<DefaultRiskManager> _logger;

    /// <summary>Maximum order value in USD before requiring confirmation.</summary>
    private const decimal MaxOrderValueUsd = 10000m;

    /// <summary>
    /// Create a new DefaultRiskManager instance.
    /// </summary>
    public DefaultRiskManager(ILogger<DefaultRiskManager> logger)
    {
        _logger = logger;
    }

    public Task<RiskCheckResult> CheckOrderAsync(OrderRequest request, CancellationToken ct = default)
    {
        try
        {
            // Market orders carry no explicit price: rather than estimating zero value
            // (which would silently bypass confirmation), require a human decision.
            var referencePrice = request.Price?.Value;
            if (referencePrice is null)
            {
                _logger.LogWarning(
                    "Order of {Quantity} {Symbol} has no price to estimate value; requiring confirmation",
                    request.Quantity.Value,
                    request.Symbol.Normalized);
                return Task.FromResult(RiskCheckResult.Approved(requiresConfirmation: true));
            }

            var estimatedValue = request.Quantity.Value * referencePrice.Value;

            if (estimatedValue > MaxOrderValueUsd)
            {
                _logger.LogWarning("Order value {Value} exceeds threshold {Threshold}",
                    estimatedValue, MaxOrderValueUsd);
                return Task.FromResult(RiskCheckResult.Approved(requiresConfirmation: true));
            }

            return Task.FromResult(RiskCheckResult.Approved(requiresConfirmation: false));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Risk check failed");
            return Task.FromResult(RiskCheckResult.Rejected($"Risk check error: {ex.Message}"));
        }
    }
}
