namespace ZettaTrade.App.Services;

using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// App-owned interim seam for adapters that can replace a working order in
/// place. It is deliberately not a Core contract: the shipped
/// <c>ITradingAdapter</c> (supervisor-gated) carries no replace call, so the
/// amend flow probes this optional interface instead. Adapters that do not
/// implement it — the shipped paper adapter among them — run the flow's
/// explicit, honestly surfaced cancel+new sequence (never a fake replace).
/// When Core lands the replace contract (<c>ReplaceOrderAsync</c> on
/// <c>ITradingAdapter</c> plus an <see cref="ExchangeCapabilities"/> flag),
/// this seam retires in favor of it.
/// </summary>
public interface IOrderReplaceSupport
{
    /// <summary>
    /// Replace a working order in place, carrying the full amended request.
    /// The adapter owns the in-place semantics and the fill-race decision:
    /// an order that filled or reached a terminal state before the replace
    /// is an <see cref="InvalidOperationException"/> (the terminal state
    /// wins and the flow reports it), exactly like the shipped cancel
    /// contract refuses non-pending orders.
    /// </summary>
    Task<Order> ReplaceOrderAsync(
        Symbol symbol,
        string orderId,
        OrderRequest amended,
        CancellationToken ct = default);
}
