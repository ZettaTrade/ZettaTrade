namespace ZettaTrade.App.Services;

using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;

/// <summary>
/// Estimates one order's notional against the hard cap's headroom line
/// (T-078): the quantity times the price anchor the order commits to — the
/// limit price, or the stop trigger for the stop family (the very anchor
/// the risk engine's value checks ride). The market reference close backs
/// the price-less types as a display convenience — the same basis the
/// header fee-estimate lines use. The difference to the risk engine is
/// deliberate: the risk engine stays fail-closed and rejects an order
/// whose value it cannot estimate, while an estimate is allowed the
/// reference close — and returns nothing when even that is absent, so the
/// summary never prints a fabricated number.
/// </summary>
public static class OrderNotional
{
    /// <summary>
    /// The order's estimated notional in the risk engine's USD-equivalent
    /// denominator, or null when the estimate has no anchor at all.
    /// </summary>
    /// <param name="request">The final request the submit pipeline built.</param>
    /// <param name="referenceClose">
    /// The latest close the ticket fetched for its estimates (the price-less
    /// types ride it); null when the read has not answered yet or failed.
    /// </param>
    public static decimal? EstimateUsd(
        OrderRequest request,
        decimal? referenceClose)
    {
        var anchor = request.Type is OrderType.StopMarket or OrderType.StopLimit or OrderType.TrailingStop
            ? request.StopPrice ?? request.Price
            : request.Price;
        var price = anchor?.Value ?? referenceClose;
        return price is { } priceValue
            ? request.Quantity.Value * priceValue
            : null;
    }
}
