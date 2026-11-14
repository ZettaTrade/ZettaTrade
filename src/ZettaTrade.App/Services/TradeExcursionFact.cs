namespace ZettaTrade.App.Services;

/// <summary>
/// One trade's derived MAE/MFE (T-251): the excursions the cached candles
/// yielded for the trade's window, keyed for joining with the accumulator's
/// fact ledger by (exchange id, closing order id). A trade whose window the
/// cache does not cover simply produces no row — the honest absence, never
/// a fabricated excursion.
/// </summary>
/// <param name="ExchangeId">The closing journal row's exchange; the join's key half.</param>
/// <param name="ClosingOrderId">The closing journal row's order id; the join's key half.</param>
/// <param name="Mae">The maximum adverse excursion in price units (never negative).</param>
/// <param name="Mfe">The maximum favorable excursion in price units (never negative).</param>
public sealed record TradeExcursionFact(
    string ExchangeId,
    string ClosingOrderId,
    decimal Mae,
    decimal Mfe);
