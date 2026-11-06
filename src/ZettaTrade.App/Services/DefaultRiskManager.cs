namespace ZettaTrade.App.Services;

using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// Default risk manager implementation.
/// Provides basic risk checks for orders.
/// </summary>
/// <remarks>
/// Fail-closed by design: when the request itself carries no price the
/// value can be estimated from (the limit price, or the stop trigger for
/// stop orders), the order is REJECTED with an explicit "cannot evaluate
/// risk" reason — the hard cap is never bypassed by any caller, whatever
/// order type or side it claims (BUG-026).
/// </remarks>
public sealed class DefaultRiskManager : IRiskManager
{
    private readonly ILogger<DefaultRiskManager> _logger;

    /// <summary>Maximum order value in USD before requiring confirmation.</summary>
    private const decimal MaxOrderValueUsd = 10000m;

    /// <summary>
    /// Order value in USD that is outright rejected regardless of confirmation.
    /// Hard caps are the checks that must never be bypassable by any caller.
    /// </summary>
    private const decimal MaxOrderValueHardCapUsd = 100000m;

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
            // The value anchors on the price the ORDER COMMITS TO AT TRIGGER
            // time: every stop-family order (StopMarket, StopLimit,
            // TrailingStop) anchors at its stop trigger — the level whose
            // crossing converts the order into an executable commitment; an
            // ordinary order anchors at its limit price. Anchoring a stop at
            // its limit price instead would understate the notional the
            // trigger actually commits (a sell stop-limit's protective limit
            // sits below its trigger), so the hard cap is evaluated against
            // what the order can really execute for at the trigger (T-074).
            // An order with NEITHER (a market order) cannot have its value
            // estimated from the request — and this manager deliberately
            // has no market-data dependency to approximate with (a stale
            // last price would estimate nonsense and reopen the bypass it
            // exists to prevent). Such an order is rejected explicitly:
            // the pre-fix behavior approved it with a required confirmation
            // and never met the cap, which its own documentation declares
            // must never be bypassable by any caller (BUG-026).
            var referencePrice = request.Type is OrderType.StopMarket or OrderType.StopLimit or OrderType.TrailingStop
                ? request.StopPrice ?? request.Price
                : request.Price;
            if (referencePrice is null)
            {
                _logger.LogWarning(
                    "Order of {Quantity} {Symbol} carries no price to estimate its value; risk cannot be evaluated and the order is rejected",
                    request.Quantity.Value,
                    request.Symbol.Normalized);
                return Task.FromResult(RiskCheckResult.Rejected(
                    "cannot evaluate risk: the order carries no price to estimate its value against the hard cap; place a limit order, or a stop order with its trigger price"));
            }

            var estimatedValue = request.Quantity.Value * referencePrice.Value.Value;

            if (estimatedValue > MaxOrderValueHardCapUsd)
            {
                _logger.LogWarning(
                    "Order value {Value} exceeds the hard cap {Cap}; rejected",
                    estimatedValue,
                    MaxOrderValueHardCapUsd);
                return Task.FromResult(RiskCheckResult.Rejected(
                    $"Order value {estimatedValue.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)} USD exceeds the hard cap {MaxOrderValueHardCapUsd.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)} USD"));
            }

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
