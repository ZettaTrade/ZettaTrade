namespace ZettaTrade.App.Services;

using Microsoft.Extensions.Logging;
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
            var estimatedValue = request.Quantity.Value * (request.Price?.Value ?? 0m);
            
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
