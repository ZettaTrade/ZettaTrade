namespace ZettaTrade.Backtest;

using System.Globalization;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.Infrastructure.Fees;

/// <summary>
/// The simulated fill book of one backtest run (T-113): the exact
/// T-051-corrected paper ledger semantics, replayed on candle times
/// instead of wall clocks. One fill moves the netted position by the paper
/// adapter's rules — weighted-average entry over the whole new size on an
/// extension, a partial close never re-averages the remainder, a flip
/// stamps its fresh position at its own fill (BUG-034) — and the fill is
/// recorded in the journal's <see cref="Order"/> shape with its commission
/// stamped from the fee registry (a missing schedule leaves the commission
/// null — the honest unknown, never a fabricated zero).
/// <para>
/// The portfolio check runs BEFORE any mutation, exactly like the paper
/// ledger's placement-time fill: a refusal writes a visible
/// <see cref="BacktestRefusal"/> into the report and leaves the book
/// untouched (the no-phantom-fill canon). Round trips book by the
/// average-price method (the PnL engine's determinism canon): a reducing
/// fill realizes <c>(price − averageEntry) × closedQty</c>, signed by the
/// held side; commissions ride outside the book.
/// </para>
/// <para>
/// Deterministic by construction: every number derives from (seeded
/// draws, candle prices); order ids are the run-local fill sequence and
/// timestamps come from the candle the stance was judged on. No wall
/// clock, no unseeded randomness.
/// </para>
/// </summary>
public sealed class BacktestLedger
{
    #region Constants

    /// <summary>The id prefix the run's simulated fills carry (journal-spelling parity with the paper adapter's ids).</summary>
    private const string FillIdPrefix = "bt-";

    /// <summary>Decimal places of the simulated quantities and prices: the same
    /// precision the journal's fee schedule rounds commissions to (T-080).</summary>
    private const int QuantityDecimals = 8;

    /// <summary>One plus the mantissa digits the quantity floor keeps before truncation.</summary>
    private const decimal QuantityFloorScale = 100_000_000m;

    /// <summary>The fill role of every transition fill: stance changes execute
    /// as market orders crossing the reference (taker — exactly the paper
    /// adapter's immediate-fill role).</summary>
    private const FeeRole TransitionFeeRole = FeeRole.Taker;

    #endregion Constants

    #region Private Fields

    private readonly string _exchangeId;
    private readonly ZettaTrade.Core.ValueObjects.Symbol _symbol;
    private readonly IFeeScheduleProvider _feeSchedules;
    private readonly decimal _initialQuoteAmount;

    /// <summary>The simulated fills in journal shape, replay order.</summary>
    private readonly List<Order> _fills = [];

    /// <summary>The refused transitions, replay order.</summary>
    private readonly List<BacktestRefusal> _refusals = [];

    /// <summary>The mark-to-market equity curve, one point per marked candle.</summary>
    private readonly List<BacktestEquityPoint> _equity = [];

    /// <summary>Simulated quote balance (paper parity: commissions stamp on the
    /// record, the paper adapter's balance check counts only the trade's quote delta).</summary>
    private decimal _quoteBalance;

    /// <summary>Signed net quantity of the open position (positive long, negative short, zero flat).</summary>
    private decimal _signedQuantity;

    /// <summary>Weighted-average entry of the open size; zero while flat.</summary>
    private decimal _averageEntry;

    /// <summary>When the current position first became non-zero (the fill's candle time).</summary>
    private DateTimeOffset _openedAt;

    /// <summary>Round-trip bookkeeping (the trade-statistics denominators).</summary>
    private int _trades;
    private int _wins;
    private int _losses;
    private decimal _grossProfit;
    private decimal _grossLoss;
    private decimal _realizedTotal;
    private decimal _commissionTotal;
    private decimal _volume;

    #endregion Private Fields

    #region Constructors

    /// <summary>Create one run's ledger over the request's execution identity.</summary>
    /// <param name="exchangeId">The venue identity the fills execute on (and the fee schedule's lookup key).</param>
    /// <param name="symbol">The symbol the fills carry.</param>
    /// <param name="feeSchedules">The fee registry commissions stamp from (the paper registry).</param>
    /// <param name="initialQuoteAmount">The simulated starting quote balance.</param>
    public BacktestLedger(
        string exchangeId,
        ZettaTrade.Core.ValueObjects.Symbol symbol,
        IFeeScheduleProvider feeSchedules,
        decimal initialQuoteAmount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exchangeId);
        _exchangeId = exchangeId;
        _symbol = symbol ?? throw new ArgumentNullException(nameof(symbol));
        _feeSchedules = feeSchedules ?? throw new ArgumentNullException(nameof(feeSchedules));
        _initialQuoteAmount = initialQuoteAmount;
        _quoteBalance = initialQuoteAmount;
    }

    #endregion Constructors

    #region Public Surface

    /// <summary>The fills recorded so far, in replay order.</summary>
    public IReadOnlyList<Order> Fills => _fills;

    /// <summary>The refused transitions so far, in replay order.</summary>
    public IReadOnlyList<BacktestRefusal> Refusals => _refusals;

    /// <summary>True once at least one fill landed (the honest baseline signal).</summary>
    public bool HasFills => _fills.Count > 0;

    /// <summary>The signed net quantity of the open position.</summary>
    public decimal SignedQuantity => _signedQuantity;

    /// <summary>The mark-to-market equity at the given price: quote cash plus the open position marked.</summary>
    public decimal MarkEquity(decimal price) => _quoteBalance + (_signedQuantity * price);

    /// <summary>Record one mark-to-market point for the closed candle.</summary>
    public void Mark(DateTimeOffset time, decimal close) => _equity.Add(new BacktestEquityPoint(time, MarkEquity(close)));

    /// <summary>The equity curve recorded so far.</summary>
    public IReadOnlyList<BacktestEquityPoint> EquityCurve => _equity;

    /// <summary>
    /// The aggregate statistics of the run so far: the fill/trip bookkeeping
    /// plus the equity-curve fold (peak, final, drawdown) and the open
    /// position's unrealized PnL at the final mark's own price.
    /// </summary>
    /// <param name="lastClose">The close price of the last marked candle (the final mark's denominator).</param>
    public BacktestStatistics ComputeStatistics(decimal lastClose)
    {
        // The equity fold: peak over the whole curve, the final value, and
        // the largest peak-to-trough fall. An empty curve (no candles) pins
        // peak/final at the starting quote honestly.
        var peak = _initialQuoteAmount;
        var final = _initialQuoteAmount;
        var maxDrawdown = 0m;
        foreach (var point in _equity)
        {
            if (point.Value > peak)
            {
                peak = point.Value;
            }

            var drawdown = peak - point.Value;
            if (drawdown > maxDrawdown)
            {
                maxDrawdown = drawdown;
            }

            final = point.Value;
        }

        var unrealized = _signedQuantity != 0m ? _signedQuantity * (lastClose - _averageEntry) : 0m;

        return new BacktestStatistics(
            Fills: _fills.Count,
            Trades: _trades,
            Wins: _wins,
            Losses: _losses,
            RealizedTotal: _realizedTotal,
            CommissionTotal: _commissionTotal,
            Volume: _volume,
            MaxDrawdown: maxDrawdown,
            PeakEquity: peak,
            FinalEquity: final,
            Unrealized: unrealized,
            AverageWinner: _wins > 0 ? _grossProfit / _wins : null,
            AverageLoser: _losses > 0 ? _grossLoss / _losses : null,
            WinRate: _wins + _losses > 0 ? _wins / (decimal)(_wins + _losses) : null,
            ProfitFactor: _grossLoss == 0m ? null : _grossProfit / -_grossLoss);
    }

    /// <summary>
    /// Fill one stance transition: an immediate fill with the paper ledger's
    /// semantics. Returns true when the fill landed; the false case is a
    /// portfolio refusal (recorded, the book untouched).
    /// </summary>
    /// <param name="side">The executed side (Buy opens/extends, Sell closes).</param>
    /// <param name="quantity">The executed quantity, strictly positive.</param>
    /// <param name="fillPrice">The execution price, strictly positive.</param>
    /// <param name="fillTime">The fill's candle time (the replay's only clock).</param>
    /// <returns>True when the fill landed, false when the portfolio refused it.</returns>
    public bool FillSide(
        OrderSide side,
        decimal quantity,
        decimal fillPrice,
        DateTimeOffset fillTime)
    {
        if (side is not (OrderSide.Buy or OrderSide.Sell))
        {
            throw new ArgumentOutOfRangeException(nameof(side), side, "The fill side must be Buy or Sell.");
        }

        if (quantity <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), quantity, "The fill quantity must be positive.");
        }

        if (fillPrice <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(fillPrice), fillPrice, "The fill price must be positive.");
        }

        // The paper bookkeeping: base asset moves by the signed delta,
        // quote moves by the notional. The balance check runs BEFORE any
        // mutation — exactly the paper adapter's ApplyFillToPortfolio
        // order — and a refusal leaves the book untouched (BUG-033, no
        // phantom fill).
        var signedDelta = side == OrderSide.Buy ? quantity : -quantity;
        var notional = fillPrice * quantity;
        var quoteDelta = side == OrderSide.Buy ? -notional : notional;

        if (_quoteBalance + quoteDelta < 0m)
        {
            var reason = $"Insufficient simulated quote balance for the {side} {quantity} {fillPrice} fill (quote {_quoteBalance.ToString(CultureInfo.InvariantCulture)}, required {quoteDelta.ToString(CultureInfo.InvariantCulture)}).";
            _refusals.Add(new BacktestRefusal(fillTime, reason));
            return false;
        }

        // The commission stamps BEFORE anything is recorded (T-080/T-051
        // paper order): computed from the venue's schedule in the fill's
        // executed role, null when the venue carries no schedule — the
        // honest unknown. The paper parity nuance: the balance accounting
        // counts only the trade's quote delta; the fee rides the record
        // and the totals, never the simulated cash.
        var commission = _feeSchedules.GetSchedule(_exchangeId) is { } schedule
            ? schedule.Commission(TransitionFeeRole, notional)
            : (decimal?)null;
        if (commission is { } stamped)
        {
            _commissionTotal += stamped;
        }

        // The fill roots into the round trips before the position moves:
        // a reducing share of one fill books its realized share against
        // the current average entry.
        BookPosition(side, signedDelta, fillPrice, fillTime);

        _quoteBalance += quoteDelta;
        _volume += notional;

        _fills.Add(new Order
        {
            // The id is the run-local fill sequence: deterministic, unlike
            // the paper adapter's random ids and wall-clock stamps — the
            // replay pins both to the candle sequence instead.
            Id = ComposeFillId(_fills.Count),
            ExchangeId = _exchangeId,
            ExchangeOrderId = ComposeFillId(_fills.Count),
            Symbol = _symbol,
            Side = side,
            Type = OrderType.Market,
            Quantity = new ZettaTrade.Core.ValueObjects.Quantity(quantity),
            Price = new ZettaTrade.Core.ValueObjects.Price(fillPrice),
            StopPrice = null,
            TrailDistance = null,
            OcoGroupId = null,
            TimeInForce = TimeInForce.GTC,
            Expiry = null,
            ClientOrderId = null,
            Status = OrderStatus.Filled,
            FilledQuantity = new ZettaTrade.Core.ValueObjects.Quantity(quantity),
            CreatedAt = fillTime,
            UpdatedAt = fillTime,
            Commission = commission,
        });

        return true;
    }

    #endregion Public Surface

    #region Target Sizing

    /// <summary>
    /// The quantity the long stance converts into: the fraction of the
    /// current equity marked at the given price, floored at the journal's
    /// decimal places (buying marginally less, never more, than the budget).
    /// </summary>
    /// <param name="markPrice">The price the equity marks at (the same candle's close).</param>
    /// <param name="longNotionalFraction">The request's long notional fraction.</param>
    public decimal LongTargetQuantity(decimal markPrice, decimal longNotionalFraction)
    {
        if (markPrice <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(markPrice), markPrice, "The sizing mark must be positive.");
        }

        var notional = MarkEquity(markPrice) * longNotionalFraction;
        return FloorQuantity(notional / markPrice);
    }

    /// <summary>The run-local deterministic id of the fill at the given index.</summary>
    private string ComposeFillId(int fillIndex) =>
        $"{FillIdPrefix}{fillIndex.ToString("D6", CultureInfo.InvariantCulture)}";

    /// <summary>Floor an absolute quantity at the journal's decimal places.</summary>
    public static decimal FloorQuantity(decimal quantity)
    {
        var floored = Math.Floor(quantity * QuantityFloorScale) / QuantityFloorScale;
        return floored;
    }

    #endregion Target Sizing

    #region Position Book

    /// <summary>
    /// Move the netted position by one fill using exactly the paper ledger's
    /// rules, booking the fill's realized share on the way (the PnL engine's
    /// average-price canon).
    /// </summary>
    private void BookPosition(OrderSide side, decimal signedDelta, decimal fillPrice, DateTimeOffset fillTime)
    {
        var previousSigned = _signedQuantity;

        if (previousSigned == 0m)
        {
            OpenFresh(signedDelta, fillPrice, fillTime);
            return;
        }

        if (decimal.Sign(signedDelta) == decimal.Sign(previousSigned))
        {
            Extend(previousSigned, signedDelta, fillPrice);
            return;
        }

        BookClosed(previousSigned, signedDelta, fillPrice, fillTime);
    }

    /// <summary>Open from flat: the fresh position stamps at its own fill.</summary>
    private void OpenFresh(decimal signedDelta, decimal fillPrice, DateTimeOffset fillTime)
    {
        _signedQuantity = signedDelta;
        _averageEntry = fillPrice;
        _openedAt = fillTime;
    }

    /// <summary>Extend the open side: weighted-average entry over the whole new size.</summary>
    private void Extend(decimal previousSigned, decimal signedDelta, decimal fillPrice)
    {
        var previousAbs = Math.Abs(previousSigned);
        var deltaAbs = Math.Abs(signedDelta);
        _averageEntry = ((_averageEntry * previousAbs) + (fillPrice * deltaAbs)) / (previousAbs + deltaAbs);
        _signedQuantity = previousSigned + signedDelta;
    }

    /// <summary>
    /// Close into (or beyond) the position: the closing share realizes by
    /// the average-price method (one round trip booked), a partial close
    /// never re-averages the remainder, and a flip's remainder opens its
    /// fresh position at the fill price, stamped at the fill's own candle
    /// time (the BUG-034 canon).
    /// </summary>
    private void BookClosed(decimal previousSigned, decimal signedDelta, decimal fillPrice, DateTimeOffset fillTime)
    {
        var heldSign = decimal.Sign(previousSigned);
        var held = Math.Abs(previousSigned);
        var closed = Math.Min(Math.Abs(signedDelta), held);
        var gross = (fillPrice - _averageEntry) * closed * heldSign;

        _trades++;
        _realizedTotal += gross;

        if (gross > 0m)
        {
            _wins++;
            _grossProfit += gross;
        }
        else if (gross < 0m)
        {
            _losses++;
            _grossLoss += gross;
        }

        var newSigned = previousSigned + signedDelta;
        if (newSigned == 0m)
        {
            _signedQuantity = 0m;
            _averageEntry = 0m;
            return;
        }

        if (decimal.Sign(newSigned) == decimal.Sign(previousSigned))
        {
            // Partial close: the remainder keeps the average entry exactly.
            _signedQuantity = newSigned;
            return;
        }

        // Flip: a fresh position on the other side at the fill price.
        _signedQuantity = newSigned;
        _averageEntry = fillPrice;
        _openedAt = fillTime;
    }

    #endregion Position Book
}
