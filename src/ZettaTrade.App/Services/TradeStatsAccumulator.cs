namespace ZettaTrade.App.Services;

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using ZettaTrade.Charting;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Infrastructure.Export;
using ZettaTrade.Infrastructure.Persistence;

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
/// <para>
/// The tag lane (T-211): when the scan carries one, a closing fill whose
/// journal row does not carry the lane's tag token books its position state
/// (the pairing stays whole) but contributes NO trade outcome — no ledger
/// row, no win/loss, no realized PnL, no curve point. The lane's aggregates
/// therefore stay honest: a lane-trade's PnL never mixes a close whose open
/// the lane would have filtered away. The volume keeps counting every fill
/// in the scope (a fill-level fact, disclosed as scope-wide — it is not
/// attributable to the lane's trades alone).
/// </para>
/// <para>
/// The risk-relative reads (T-251) ride the same booked-trade series. Every
/// booked close also produces a <see cref="TradeStatsTradeFact"/> — the
/// trade's window (the position opened at its opening fill's journal time;
/// an extend keeps the window, a flip stamps the flip fill) and the signed
/// R-multiple against the journal's stop context (the closing row's own
/// trigger first — a filled protective stop IS the risk level — then the
/// most recent trigger among the position's opening rows; the measure
/// ruler's division canon: null on a missing stop or zero risk, never a
/// substituted default). The Sharpe and Sortino ratios accumulate
/// incrementally over the booked PnLs (Welford's running mean and variance,
/// the target-0 downside sum) so a snapshot stays O(1) — a six-figure scan
/// publishing per page never re-reads the whole series.
/// </para>
/// </summary>
public sealed class TradeStatsAccumulator
{
    #region Private Fields

    /// <summary>
    /// The scan's tag lane token (T-211): null means every closing fill
    /// books its outcome; a token means only the closing fills whose journal
    /// tags carry it do. The token match is exact-token and case-insensitive
    /// — the same matching the tags' canonical comma-joined storage speaks.
    /// </summary>
    private readonly string? _tagLane;

    /// <summary>Running net position state per normalized symbol.</summary>
    private readonly Dictionary<string, SymbolPosition> _positions = new(StringComparer.Ordinal);

    /// <summary>The equity curve so far: the zero anchor at the first fill's time, then one point per closing fill.</summary>
    private readonly List<EquityCurvePoint> _equity = [];

    /// <summary>
    /// The realized-trade ledger (T-210): one row per booked closing fill,
    /// the same outcomes the summary and the curve derive from — the
    /// statement's realized section reads exactly this.
    /// </summary>
    private readonly List<StatementRealizedTrade> _realizedTrades = [];

    /// <summary>
    /// The per-trade risk facts (T-251): one row per booked closing fill,
    /// riding the ledger 1:1 in the same booking order — the window and the
    /// R-multiple facts the excursion derivation and the table's risk
    /// columns join on.
    /// </summary>
    private readonly List<TradeStatsTradeFact> _tradeFacts = [];

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

    /// <summary>Welford's running mean of the booked PnLs (T-251); zero seeds the series.</summary>
    private decimal _pnlMean;

    /// <summary>Welford's running squared-deviation sum over the booked PnLs; the sample variance divides it by count − 1.</summary>
    private decimal _pnlM2;

    /// <summary>The running sum of squared negative PnLs; the Sortino denominator reads its mean.</summary>
    private decimal _downsideSumSq;

    /// <summary>The running sum of the R-multiples the booked trades carry.</summary>
    private decimal _rSum;

    /// <summary>How many booked trades carried a stop context (the R distribution's denominator).</summary>
    private int _rCount;

    /// <summary>The best R-multiple booked so far; null until one trade carries a stop context.</summary>
    private decimal? _rBest;

    /// <summary>The worst R-multiple booked so far; null until one trade carries a stop context.</summary>
    private decimal? _rWorst;

    private bool _hasFills;

    #endregion Private Fields

    #region Properties

    /// <summary>True once at least one Filled row was ingested — the honest empty-state signal of the scope.</summary>
    public bool HasAnyFills => _hasFills;

    #endregion Properties

    #region Public Methods

    /// <summary>
    /// Create an accumulator for one scan run. The tag lane (T-211) is the
    /// run's scope: null (the default) books every closing fill's outcome;
    /// a token books only the outcomes whose closing journal row carries it.
    /// </summary>
    public TradeStatsAccumulator(string? tagLane = null)
    {
        _tagLane = string.IsNullOrWhiteSpace(tagLane) ? null : tagLane.Trim();
    }

    /// <summary>Ingest one chronological page of journal orders, ascending (CreatedAt, ExchangeId, OrderId). Each row rides its user trade annotations (T-211) as the journal keeps them.</summary>
    public void Ingest(IReadOnlyList<OrderPageRow> page)
    {
        ArgumentNullException.ThrowIfNull(page);

        foreach (var row in page)
        {
            IngestOne(row);
        }
    }

    /// <summary>
    /// The aggregate so far, including the equity curve as it was at this
    /// moment: the scan keeps appending after a publish, and a snapshot
    /// must not see later points — the pinned view guarantees exactly that
    /// without copying the whole curve per publish (a six-figure scan
    /// re-copied its whole growing curve every page before).
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
            Equity: new EquityView(_equity, _equity.Count),
            Sharpe: SharpeRatio(),
            Sortino: SortinoRatio(),
            AverageRMultiple: _rCount > 0 ? _rSum / _rCount : null,
            BestRMultiple: _rBest,
            WorstRMultiple: _rWorst);
    }

    /// <summary>
    /// The realized-trade ledger so far (T-210): a fresh copy, like the
    /// summary's equity curve — the scan keeps appending, a snapshot must
    /// not see later rows.
    /// </summary>
    public IReadOnlyList<StatementRealizedTrade> GetRealizedTrades() => [.. _realizedTrades];

    /// <summary>
    /// The per-trade risk facts so far (T-251): a fresh copy, like the
    /// ledger it rides — the scan keeps appending, a snapshot must not see
    /// later facts.
    /// </summary>
    public IReadOnlyList<TradeStatsTradeFact> GetTradeFacts() => [.. _tradeFacts];

    /// <summary>
    /// The per-trade Sharpe ratio (T-251): the Welford mean of the booked
    /// PnLs over their sample standard deviation. Null while fewer than two
    /// returns exist or the series carries no spread at all (an unbounded
    /// division is refused, never defaulted — the view renders it by the
    /// mean's sign). Unannualized: the curve carries no annual clock.
    /// </summary>
    private decimal? SharpeRatio()
    {
        if (_trades < 2 || _pnlM2 <= 0m)
        {
            return null;
        }

        return _pnlMean / Sqrt(_pnlM2 / (_trades - 1));
    }

    /// <summary>
    /// The per-trade Sortino ratio (T-251): the Welford mean over the
    /// target-0 downside deviation (the square root of the mean squared
    /// negative return, divided by ALL the booked trades — the standard
    /// downside-deviation denominator). Null while no negative return
    /// exists (the division is unbounded or meaningless), never a zero.
    /// </summary>
    private decimal? SortinoRatio()
    {
        if (_trades == 0 || _downsideSumSq <= 0m)
        {
            return null;
        }

        return _pnlMean / Sqrt(_downsideSumSq / _trades);
    }

    /// <summary>The decimal square root through the double lane (the VwapOverlay pattern).</summary>
    private static decimal Sqrt(decimal value) => (decimal)Math.Sqrt((double)value);

    #endregion Public Methods

    #region Private Methods

    private void IngestOne(OrderPageRow row)
    {
        var order = row.Order;
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
            // chart starts at the true origin instead of the first close
            // (the scope's origin even under a tag lane — the lane filters
            // outcomes, never the scope itself).
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
        var rowStop = order.StopPrice?.Value;

        if (signed == 0)
        {
            position.Open(fillSign * filled, price, order.CreatedAt, rowStop);
            return;
        }

        if (decimal.Sign(fillSign) == decimal.Sign(signed))
        {
            position.Extend(fillSign * filled, price, rowStop);
            return;
        }

        BookClose(position, row, price);
    }

    /// <summary>
    /// A closing fill: book the closing share of the position against its
    /// average entry, then hand the fill's remainder to the position state —
    /// nothing left on an exact close, a flipped remainder otherwise. One
    /// fill = at most one trade outcome, per the fill-pair contract; the
    /// outcome lands in the realized-trade ledger exactly as the summary
    /// and the curve see it (T-210).
    /// <para>
    /// The tag lane (T-211) gates the OUTCOME only: a closing fill outside
    /// the lane still hands its remainder to the position state (the pairing
    /// stays whole), but books no trade, no PnL, no curve point, no risk
    /// fact. Lane trades carry the closing journal row's identity and
    /// annotations in the ledger row.
    /// </para>
    /// <para>
    /// The risk facts (T-251) read the position BEFORE the close mutates it:
    /// the window opens at the position's <see cref="SymbolPosition.OpenedAt"/>
    /// (the opening fill's journal time), the side is the held quantity's
    /// sign, and the stop context is the closing row's own trigger when it
    /// carries one (a filled protective stop IS the risk level), else the
    /// most recent trigger among the position's opening rows. The R-multiple
    /// divides the signed per-unit PnL by the |entry − stop| distance — null
    /// on a missing stop or a zero risk distance, never a substituted
    /// default (the measure ruler's division canon, T-200).
    /// </para>
    /// </summary>
    private void BookClose(SymbolPosition position, OrderPageRow row, decimal price)
    {
        var order = row.Order;
        var closingDelta = (order.Side == OrderSide.Buy ? 1m : -1m) * order.FilledQuantity.Value;
        var time = order.CreatedAt;

        // The lane gate BEFORE any outcome booking: the position state
        // reduces no matter what, the aggregates only see lane trades.
        if (_tagLane is { } lane && !CarriesTag(row.Tags, lane))
        {
            position.ReduceOrFlip(closingDelta, price, time, order.StopPrice?.Value);
            return;
        }

        var closable = Math.Min(Math.Abs(closingDelta), Math.Abs(position.SignedQuantity));
        var direction = decimal.Sign(position.SignedQuantity);
        var perUnitPnl = direction * (price - position.AverageEntry);
        var realized = perUnitPnl * closable;
        var openedAt = position.OpenedAt;
        var isLong = direction > 0;

        var stopPrice = order.StopPrice?.Value ?? position.StopPrice;
        decimal? rMultiple = null;
        if (stopPrice is { } stop)
        {
            var riskDistance = Math.Abs(position.AverageEntry - stop);
            if (riskDistance > 0m)
            {
                rMultiple = perUnitPnl / riskDistance;
            }
        }

        _trades++;
        _realizedTotal += realized;
        _realizedTrades.Add(new StatementRealizedTrade(
            ClosedAt: time,
            ExchangeId: order.ExchangeId,
            Symbol: order.Symbol.Normalized,
            ClosingSide: order.Side.ToString(),
            ClosingOrderId: order.Id,
            Quantity: closable,
            EntryPrice: position.AverageEntry,
            ExitPrice: price,
            RealizedPnl: realized,
            QuoteCurrency: order.Symbol.QuoteAsset,
            Note: row.Note,
            Tags: row.Tags));
        _tradeFacts.Add(new TradeStatsTradeFact(
            ClosedAt: time,
            OpenedAt: openedAt,
            ExchangeId: order.ExchangeId,
            Symbol: order.Symbol.Normalized,
            ClosingOrderId: order.Id,
            IsLong: isLong,
            EntryPrice: position.AverageEntry,
            RMultiple: rMultiple));

        // The running ratio series (T-251): Welford's mean and squared-
        // deviation sum over the booked PnLs, the downside sum beside them
        // — a snapshot never re-reads the series, the publish stays O(1).
        var delta = realized - _pnlMean;
        _pnlMean += delta / _trades;
        _pnlM2 += delta * (realized - _pnlMean);
        if (realized < 0m)
        {
            _downsideSumSq += realized * realized;
        }

        if (rMultiple is { } multiple)
        {
            _rCount++;
            _rSum += multiple;
            if (_rBest is null || multiple > _rBest)
            {
                _rBest = multiple;
            }

            if (_rWorst is null || multiple < _rWorst)
            {
                _rWorst = multiple;
            }
        }

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

        position.ReduceOrFlip(closingDelta, price, time, order.StopPrice?.Value);
    }

    /// <summary>
    /// Whether a journal row's canonical comma-joined tag list carries the
    /// lane's token: exact-token, case-insensitive, tolerant of stray
    /// whitespace (the storage is canonical, the matching is forgiving).
    /// </summary>
    private static bool CarriesTag(string? tags, string lane) =>
        tags?
            .Split(',')
            .Any(tag => string.Equals(tag.Trim(), lane, StringComparison.OrdinalIgnoreCase)) == true;

    #endregion Private Methods

    #region Position State

    /// <summary>
    /// Mutable accumulator of one symbol's net exposure. The quantity and
    /// average entry rules mirror the paper ledger's bookkeeping (BUG-005:
    /// a partial close never re-averages the remainder; BUG-034: a flip
    /// stamps its fresh position at its own fill).
    /// <para>
    /// The risk facts (T-251) ride the same state: the trade window opens
    /// when the position does (an extend keeps it — the trade is still the
    /// same position), a flip stamps the window at the flip fill, and the
    /// stop context remembers the most recent trigger an opening row
    /// carried (the ticket's sizer stop riding the entry leg) until the
    /// position itself turns over.
    /// </para>
    /// </summary>
    private sealed class SymbolPosition
    {
        /// <summary>Signed net quantity (positive long, negative short, zero flat).</summary>
        public decimal SignedQuantity { get; private set; }

        /// <summary>Weighted-average entry of the open size; zero while flat.</summary>
        public decimal AverageEntry { get; private set; }

        /// <summary>When the current position opened (the opening or flip fill's journal time); the trade window's start.</summary>
        public DateTimeOffset OpenedAt { get; private set; }

        /// <summary>The most recent trigger an opening row carried (the ticket's sizer stop); null while none does.</summary>
        public decimal? StopPrice { get; private set; }

        /// <summary>Open from flat: the fresh position sits at the fill price, its window and stop stamped from the row.</summary>
        public void Open(decimal signedQuantity, decimal price, DateTimeOffset openedAt, decimal? stopPrice)
        {
            SignedQuantity = signedQuantity;
            AverageEntry = price;
            OpenedAt = openedAt;
            StopPrice = stopPrice;
        }

        /// <summary>
        /// Extend the open side: weighted-average entry over the whole new
        /// size, the window unchanged (the same position, still the same
        /// trade), the stop context advanced when the row carries one.
        /// </summary>
        public void Extend(decimal signedDelta, decimal price, decimal? stopPrice)
        {
            var previousAbs = Math.Abs(SignedQuantity);
            var deltaAbs = Math.Abs(signedDelta);
            AverageEntry = ((AverageEntry * previousAbs) + (price * deltaAbs))
                / (previousAbs + deltaAbs);
            SignedQuantity += signedDelta;
            if (stopPrice is { } stop)
            {
                StopPrice = stop;
            }
        }

        /// <summary>
        /// Reduce by the closing quantity (the average entry of the remaining
        /// size stays exactly as it was) or cross zero: the flip remainder
        /// opens its fresh position at its own fill price, its window and
        /// stop stamped from the same row.
        /// </summary>
        public void ReduceOrFlip(decimal closingDelta, decimal price, DateTimeOffset rowTime, decimal? stopPrice)
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
            OpenedAt = rowTime;
            StopPrice = stopPrice;
        }
    }

    #endregion Position State

    #region Snapshot View

    /// <summary>
    /// The read-only window over the accumulator's append-only equity list,
    /// pinned to the length at snapshot time: the scan keeps appending after
    /// a snapshot published, and a published summary must never grow with
    /// it — the pinned count guarantees exactly that without copying the
    /// whole curve per publish. Reads stay single-threaded by the
    /// accumulator's own contract: one scan ingests and publishes on its
    /// continuation chain, and the chart copies the points it renders onto
    /// its snapshot before the render thread sees any of them.
    /// </summary>
    private sealed class EquityView(List<EquityCurvePoint> points, int count) : IReadOnlyList<EquityCurvePoint>
    {
        public int Count => count;

        public EquityCurvePoint this[int index] =>
            index >= 0 && index < count ? points[index] : throw new ArgumentOutOfRangeException(nameof(index));

        public IEnumerator<EquityCurvePoint> GetEnumerator() =>
            points.Take(count).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    #endregion Snapshot View
}
