namespace ZettaTrade.App.Services;

using System;
using System.Collections.Generic;
using ZettaTrade.Charting;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;

/// <summary>
/// Incremental aggregator for the trade-statistics dashboard: ingests
/// journal orders ONE chronological page at a time and keeps the running
/// aggregate plus the equity curve so far, so a scan publishes honest
/// partial statistics from its first page (no first-load wall).
/// <para>
/// The math consumes the journal's per-record whole-order fill semantics —
/// a Filled row is one fill of FilledQuantity at the row's Price (the paper
/// ledger guarantees exactly this; live adapters record whatever their last
/// placement refresh carried — the paper venue filter scopes the reads when
/// that distinction matters). Realized PnL books fill-pair-wise: an open
/// position accumulates at its weighted-average entry, a reducing fill
/// closes its quantity against that entry (a partial close never re-averages
/// the remainder), a fill beyond the open size closes the position fully and
/// flips the remainder into a fresh position at its own price.
/// </para>
/// <para>
/// Single-threaded by contract: one scan owns one accumulator and ingests
/// its pages in ascending (CreatedAt, ExchangeId, OrderId) order — the exact
/// shape <see cref="ZettaTrade.Infrastructure.Persistence.IOrderStatsQuery"/>
/// serves. Pages that never close a position cost nothing but the volume
/// they traded.
/// </para>
/// </summary>
public sealed class TradeStatsAccumulator
{
    #region Private Fields

    /// <summary>Running net position state per normalized symbol.</summary>
    private readonly Dictionary<string, SymbolPosition> _positions = new(StringComparer.Ordinal);

    /// <summary>The equity curve so far: the zero anchor at the first fill's time, then one point per closing fill.</summary>
    private readonly List<EquityCurvePoint> _equity = [];

    private int _trades;
    private int _wins;
    private int _losses;
    private decimal _grossProfit;
    private decimal _grossLoss;
    private decimal _maxDrawdown;
    private decimal _realizedTotal;
    private decimal _volume;

    /// <summary>The running equity peak the drawdown reads against; zero seeds the start of history.</summary>
    private decimal _equityPeak;

    private bool _hasFills;

    #endregion Private Fields

    #region Properties

    /// <summary>True once at least one Filled row was ingested — the honest empty-state signal of the scope.</summary>
    public bool HasAnyFills => _hasFills;

    #endregion Properties

    #region Public Methods

    /// <summary>Ingest one chronological page of journal orders, ascending (CreatedAt, ExchangeId, OrderId).</summary>
    public void Ingest(IReadOnlyList<Order> page)
    {
        ArgumentNullException.ThrowIfNull(page);

        foreach (var order in page)
        {
            IngestOne(order);
        }
    }

    /// <summary>
    /// The aggregate so far, including a fresh copy of the equity curve (the
    /// scan keeps appending — a snapshot must not see later points).
    /// </summary>
    public TradeStatsSummary Snapshot()
    {
        var wins = _wins;
        var losses = _losses;

        return new TradeStatsSummary(
            Trades: _trades,
            Wins: wins,
            Losses: losses,
            MaxDrawdown: _maxDrawdown,
            RealizedTotal: _realizedTotal,
            Volume: _volume,
            AverageWinner: wins > 0 ? _grossProfit / wins : null,
            AverageLoser: losses > 0 ? _grossLoss / losses : null,
            WinRate: wins + losses > 0 ? wins / (decimal)(wins + losses) : null,
            ProfitFactor: _grossLoss == 0 ? null : _grossProfit / -_grossLoss,
            Equity: [.. _equity]);
    }

    #endregion Public Methods

    #region Private Methods

    private void IngestOne(Order order)
    {
        if (order.Status is not OrderStatus.Filled)
        {
            return;
        }

        var filled = order.FilledQuantity.Value;
        if (filled <= 0)
        {
            return;
        }

        var price = order.Price.Value;
        _volume += filled * price;

        if (!_hasFills)
        {
            // The curve's zero anchor sits at the scope's first fill, so the
            // chart starts at the true origin instead of the first close.
            _hasFills = true;
            _equity.Add(new EquityCurvePoint(order.CreatedAt, 0m));
        }

        var symbolKey = order.Symbol.Normalized;
        if (!_positions.TryGetValue(symbolKey, out var position))
        {
            position = new SymbolPosition();
            _positions[symbolKey] = position;
        }

        var fillSign = order.Side == OrderSide.Buy ? 1m : -1m;
        var signed = position.SignedQuantity;

        if (signed == 0)
        {
            position.Open(fillSign * filled, price);
            return;
        }

        if (decimal.Sign(fillSign) == decimal.Sign(signed))
        {
            position.Extend(fillSign * filled, price);
            return;
        }

        BookClose(position, fillSign * filled, price, order.CreatedAt);
    }

    /// <summary>
    /// A closing fill: book the closing share of the position against its
    /// average entry, then hand the fill's remainder to the position state —
    /// nothing left on an exact close, a flipped remainder otherwise. One
    /// fill = at most one trade outcome, per the fill-pair contract.
    /// </summary>
    private void BookClose(SymbolPosition position, decimal closingDelta, decimal price, DateTimeOffset time)
    {
        var closable = Math.Min(Math.Abs(closingDelta), Math.Abs(position.SignedQuantity));
        var direction = decimal.Sign(position.SignedQuantity);
        var realized = direction * (price - position.AverageEntry) * closable;

        _trades++;
        _realizedTotal += realized;

        if (realized > 0)
        {
            _wins++;
            _grossProfit += realized;
        }
        else if (realized < 0)
        {
            _losses++;
            _grossLoss += realized;
        }

        _equity.Add(new EquityCurvePoint(time, _realizedTotal));

        if (_realizedTotal > _equityPeak)
        {
            _equityPeak = _realizedTotal;
        }

        var drawdown = _equityPeak - _realizedTotal;
        if (drawdown > _maxDrawdown)
        {
            _maxDrawdown = drawdown;
        }

        position.ReduceOrFlip(closingDelta, price);
    }

    #endregion Private Methods

    #region Position State

    /// <summary>
    /// Mutable accumulator of one symbol's net exposure. The quantity and
    /// average entry rules mirror the paper ledger's bookkeeping (BUG-005:
    /// a partial close never re-averages the remainder; BUG-034: a flip
    /// stamps its fresh position at its own fill).
    /// </summary>
    private sealed class SymbolPosition
    {
        /// <summary>Signed net quantity (positive long, negative short, zero flat).</summary>
        public decimal SignedQuantity { get; private set; }

        /// <summary>Weighted-average entry of the open size; zero while flat.</summary>
        public decimal AverageEntry { get; private set; }

        /// <summary>Open from flat: the fresh position sits at the fill price.</summary>
        public void Open(decimal signedQuantity, decimal price)
        {
            SignedQuantity = signedQuantity;
            AverageEntry = price;
        }

        /// <summary>Extend the open side: weighted-average entry over the whole new size.</summary>
        public void Extend(decimal signedDelta, decimal price)
        {
            var previousAbs = Math.Abs(SignedQuantity);
            var deltaAbs = Math.Abs(signedDelta);
            AverageEntry = ((AverageEntry * previousAbs) + (price * deltaAbs))
                / (previousAbs + deltaAbs);
            SignedQuantity += signedDelta;
        }

        /// <summary>
        /// Reduce by the closing quantity (the average entry of the remaining
        /// size stays exactly as it was) or cross zero: the flip remainder
        /// opens its fresh position at its own fill price.
        /// </summary>
        public void ReduceOrFlip(decimal closingDelta, decimal price)
        {
            var newSigned = SignedQuantity + closingDelta;

            if (newSigned == 0)
            {
                SignedQuantity = 0;
                AverageEntry = 0;
                return;
            }

            if (decimal.Sign(newSigned) == decimal.Sign(SignedQuantity))
            {
                SignedQuantity = newSigned;
                return;
            }

            SignedQuantity = newSigned;
            AverageEntry = price;
        }
    }

    #endregion Position State
}
