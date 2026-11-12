namespace ZettaTrade.App.Services;

using System.Globalization;
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
/// order type or side it claims (BUG-026). The daily loss governor (T-189)
/// rides the same discipline: when it is enabled and its ledger read
/// faults, the exception lands in this method's own catch and the order is
/// rejected with the honest fault text — never approved on an unknown
/// state.
/// </remarks>
public sealed class DefaultRiskManager : IRiskManager
{
    private readonly ILogger<DefaultRiskManager> _logger;

    /// <summary>
    /// The daily loss governor's state seam (T-189): optional by
    /// construction — a manager built without it (the legacy unit battery)
    /// has no governor and stays bit-identical to the ungoverned past; DI
    /// always wires it.
    /// </summary>
    private readonly IDailyLossLimit? _dailyLoss;

    /// <summary>Maximum order value in USD before requiring confirmation.</summary>
    private const decimal MaxOrderValueUsd = 10000m;

    /// <summary>
    /// Order value in USD that is outright rejected regardless of confirmation.
    /// Hard caps are the checks that must never be bypassable by any caller.
    /// Exposed (T-078) so the ticket's pre-submit summary names its headroom
    /// from the same authority the risk check enforces — the cap is never
    /// a locally duplicated number.
    /// </summary>
    public const decimal MaxOrderValueHardCapUsd = 100000m;

    /// <summary>
    /// Create a new DefaultRiskManager instance.
    /// </summary>
    /// <param name="logger">The manager's own log.</param>
    /// <param name="dailyLoss">The daily loss governor's state seam (T-189), or null for a manager without the governor (the legacy shape; DI always wires the service).</param>
    public DefaultRiskManager(ILogger<DefaultRiskManager> logger, IDailyLossLimit? dailyLoss = null)
    {
        _logger = logger;
        _dailyLoss = dailyLoss;
    }

    public async Task<RiskCheckResult> CheckOrderAsync(OrderRequest request, CancellationToken ct = default)
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
                return RiskCheckResult.Rejected(
                    "cannot evaluate risk: the order carries no price to estimate its value against the hard cap; place a limit order, or a stop order with its trigger price");
            }

            var estimatedValue = request.Quantity.Value * referencePrice.Value.Value;

            if (estimatedValue > MaxOrderValueHardCapUsd)
            {
                _logger.LogWarning(
                    "Order value {Value} exceeds the hard cap {Cap}; rejected",
                    estimatedValue,
                    MaxOrderValueHardCapUsd);
                return RiskCheckResult.Rejected(
                    $"Order value {estimatedValue.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)} USD exceeds the hard cap {MaxOrderValueHardCapUsd.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)} USD");
            }

            // The daily loss governor (T-189): a protective block, never a
            // trap. It rides AFTER the intrinsic checks (the feature's off
            // state keeps every decision here bit-identical) and BEFORE the
            // threshold confirmation (a blocked order must never reach a
            // confirmation dialog), and a reduce-only request is exempt
            // before any read at all — closing positions and the kill
            // switch's flatten always stay allowed. The engine fault path
            // lands in the method's own catch below: fail-closed, never
            // approved on an unknown state.
            if (!request.ReduceOnly && _dailyLoss is { } dailyLoss)
            {
                var loss = await dailyLoss.ReadStateAsync(request.Symbol, ct);
                if (loss.Enabled && loss.IsBlocked)
                {
                    _logger.LogWarning(
                        "Daily loss limit reached for {Symbol}: {Loss} {Quote} realized today against the {Limit} {Quote} limit; new orders are blocked",
                        request.Symbol.Normalized,
                        loss.DayLoss,
                        loss.Quote,
                        loss.Limit,
                        loss.Quote);
                    return RiskCheckResult.Rejected(
                        $"Daily loss limit reached: {loss.DayLoss.ToString("F2", CultureInfo.InvariantCulture)} {loss.Quote} realized today against the {loss.Limit.ToString("F2", CultureInfo.InvariantCulture)} {loss.Quote} limit; new orders are blocked, closing orders stay allowed");
                }
            }

            if (estimatedValue > MaxOrderValueUsd)
            {
                _logger.LogWarning("Order value {Value} exceeds threshold {Threshold}",
                    estimatedValue, MaxOrderValueUsd);
                return RiskCheckResult.Approved(requiresConfirmation: true);
            }

            return RiskCheckResult.Approved(requiresConfirmation: false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Risk check failed");
            return RiskCheckResult.Rejected($"Risk check error: {ex.Message}");
        }
    }
}
