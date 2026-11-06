namespace ZettaTrade.Infrastructure.Exchange;

using System.Collections.Concurrent;
using System.Reactive.Linq;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// Simulated (paper) trading adapter. Market, stop-market and market-crossing
/// limit orders fill immediately against prices taken from the wrapped
/// <see cref="IMarketDataAdapter"/>, a standalone limit resting away from the
/// market stays Pending until cancelled, and conditional orders — stop-limit,
/// trailing stop and OCO (one-cancels-the-other) legs — arm as working orders
/// and evaluate their triggers against the wrapped adapter's live price stream
/// (the ticker feed). OCO legs additionally share an either-or group: the
/// first leg that fills or is cancelled takes the remaining legs down with
/// it, so no half pair keeps working. A stop-side order that would trigger
/// immediately, and a leg joining an OCO group whose partner already ended,
/// are rejected with an explicit error instead of a degraded execution.
/// In-memory balances and netted positions are maintained for every fill.
/// Never touches real funds or exchange keys, which makes it the safe default
/// trading surface for MCP tools and demos on any exchange.
/// </summary>
public sealed class PaperTradingAdapter : ITradingAdapter
{
    #region Constants

    /// <summary>Adapter identifier exposed to Symbol exchange-format resolution.</summary>
    public const string ExchangeId = "paper";

    public const string ExchangeName = "Paper Trading";

    /// <summary>Fully simulated starting balance (USDT).</summary>
    public const decimal InitialQuoteAmount = 100_000m;

    /// <summary>Quote currency simulated for every fill in this demo implementation.</summary>
    public const string QuoteCurrency = "USDT";

    private const string OrderIdPrefix = "paper-";
    private const string PositionIdPrefix = "pos-";

    /// <summary>Timeframe of the bar providing the reference price (market fills and limit crossing checks).</summary>
    private const TimeFrame ReferenceTimeFrame = TimeFrame.H1;

    /// <summary>Lookback window guaranteeing at least one reference bar exists.</summary>
    private const int ReferenceBarLookbackHours = 24;

    /// <summary>
    /// Reference-window fetch limit: one H1 bar per lookback hour plus one for
    /// the partially open bar. Big enough to cover the whole window on every
    /// backend, so the request can no longer page back exactly one bar: the
    /// prior fixed <c>limit: 1</c> from <c>now - 24h</c> handed the adapter a
    /// ~24-hour-old close on ascending-order backends (BUG-006).
    /// </summary>
    private const int ReferenceBarFetchLimit = ReferenceBarLookbackHours + 1;

    #endregion

    #region Fields

    private readonly IMarketDataAdapter _marketData;
    private readonly ILogger<PaperTradingAdapter> _logger;

    /// <summary>Serializes order placement / cancellation; positions are mutated under it.</summary>
    private readonly SemaphoreSlim _tradingLock = new(1, 1);

    private readonly ConcurrentDictionary<string, Order> _orders = new();
    private readonly ConcurrentDictionary<string, NetPosition> _positions = new();
    private readonly ConcurrentDictionary<string, decimal> _balances = new();

    /// <summary>
    /// Simulation state per stream-evaluated working order (stop-limit,
    /// trailing stop, OCO legs): the trigger level the order currently arms
    /// at. Entries exist exactly while the order works; every terminal
    /// transition removes them.
    /// </summary>
    private readonly ConcurrentDictionary<string, ConditionalState> _conditionals = new();

    /// <summary>
    /// OCO group registry: group id → its leg order ids. All mutations run
    /// under the trading lock, so the either-or semantics can never observe a
    /// torn group.
    /// </summary>
    private readonly ConcurrentDictionary<string, List<string>> _ocoLegs = new();

    /// <summary>One live ticker subscription per symbol that holds working conditionals.</summary>
    private readonly ConcurrentDictionary<string, IDisposable> _priceStreams = new();

    #endregion

    #region Constructor

    /// <summary>
    /// Create a new paper trading adapter around any market data adapter.
    /// </summary>
    public PaperTradingAdapter(IMarketDataAdapter marketData, ILogger<PaperTradingAdapter> logger)
    {
        _marketData = marketData ?? throw new ArgumentNullException(nameof(marketData));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    #endregion

    #region Market data (delegated to the wrapped adapter)

    /// <inheritdoc/>
    public string Id => ExchangeId;

    /// <inheritdoc/>
    public string Name => ExchangeName;

    /// <inheritdoc/>
    public bool IsConnected => _marketData.IsConnected;

    /// <inheritdoc/>
    public ExchangeCapabilities Capabilities =>
        ExchangeCapabilities.Trading | ExchangeCapabilities.AccountInfo | ExchangeCapabilities.HistoricalData | ExchangeCapabilities.MarketData;

    /// <inheritdoc/>
    public Task ConnectAsync(CancellationToken ct = default) => _marketData.ConnectAsync(ct);

    /// <inheritdoc/>
    public Task DisconnectAsync(CancellationToken ct = default) => _marketData.DisconnectAsync(ct);

    /// <inheritdoc/>
    public Task<IReadOnlyList<Symbol>> GetSymbolsAsync(CancellationToken ct = default) =>
        _marketData.GetSymbolsAsync(ct);

    /// <inheritdoc/>
    public IObservable<Ticker> SubscribeTicker(Symbol symbol) => _marketData.SubscribeTicker(symbol);

    /// <inheritdoc/>
    public IObservable<OrderBook> SubscribeOrderBook(Symbol symbol, int depth = 20) =>
        _marketData.SubscribeOrderBook(symbol, depth);

    /// <inheritdoc/>
    public IObservable<MarketTrade> SubscribeTrades(Symbol symbol) => _marketData.SubscribeTrades(symbol);

    /// <inheritdoc/>
    public Task<IReadOnlyList<Bar>> GetBarsAsync(
        Symbol symbol,
        TimeFrame timeFrame,
        DateTimeOffset from,
        DateTimeOffset? to = null,
        int limit = 500,
        CancellationToken ct = default) =>
        _marketData.GetBarsAsync(symbol, timeFrame, from, to, limit, ct);

    #endregion

    #region Trading

    /// <inheritdoc/>
    public async Task<Order> PlaceOrderAsync(OrderRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();

        await _tradingLock.WaitAsync(ct);
        try
        {
            if (request.Type == OrderType.Limit && request.OcoGroupId is null)
            {
                return await PlaceLimitOrderCoreAsync(request, ct);
            }

            if (request.Type is OrderType.Limit or OrderType.StopLimit or OrderType.TrailingStop)
            {
                // Conditional orders arm as working orders and evaluate their
                // triggers on the live price stream (see the Conditional
                // Simulation region).
                return await PlaceConditionalOrderCoreAsync(request, ct);
            }

            // Market and stop-market orders keep the long-standing immediate-fill semantics.
            var fillPrice = await ResolveFillPriceAsync(request, ct);
            return RecordFillLocked(FillOrder(request, fillPrice), fillPrice);
        }
        finally
        {
            _tradingLock.Release();
        }
    }

    /// <summary>
    /// The paper book simulation for limit orders: a limit whose price already reaches
    /// the market (a buy at or above the latest reference close, a sell at or below)
    /// fills at its own limit price exactly like before; a limit placed away from the
    /// market rests as Pending — no balance or position is touched, there is no
    /// matching loop, and the order stays cancelable.
    /// </summary>
    private async Task<Order> PlaceLimitOrderCoreAsync(OrderRequest request, CancellationToken ct)
    {
        // The request is validated: a limit always carries its limit price.
        Price limitPrice = request.Price!.Value;
        var referenceClose = await ResolveReferenceCloseAsync(request, ct);

        var crosses = request.Side == OrderSide.Buy
            ? limitPrice.Value >= referenceClose.Value
            : limitPrice.Value <= referenceClose.Value;

        if (!crosses)
        {
            var resting = CreateRestingOrder(request);
            _orders[resting.Id] = resting;

            _logger.LogInformation(
                "Paper rest: {Side} {Quantity} {Symbol} limit {LimitPrice} does not cross the reference close {ReferenceClose}; the order stays pending until cancelled",
                resting.Side,
                resting.Quantity.Value,
                resting.Symbol.Normalized,
                limitPrice.Value,
                referenceClose.Value);

            return resting;
        }

        // A crossing limit takes liquidity at its own price (the limit price itself).
        return RecordFillLocked(FillOrder(request, limitPrice), limitPrice);
    }

    /// <summary>
    /// The paper simulation for conditional orders — stop-limit, trailing stop
    /// and OCO legs: the order arms as a working (Pending) entry whose trigger
    /// is evaluated on the wrapped adapter's ticker stream. A stop-side order
    /// that would trigger immediately is rejected with an explicit error
    /// instead of silently degrading into an instant fill; a leg joining an
    /// OCO group whose partner already reached a terminal state is rejected
    /// (the either-or contract could not hold for it); and an OCO limit leg
    /// whose price already crosses the market takes liquidity immediately,
    /// exactly like a standalone crossing limit. Runs under the trading lock.
    /// </summary>
    private async Task<Order> PlaceConditionalOrderCoreAsync(OrderRequest request, CancellationToken ct)
    {
        // Stops need the reference close for the arming check; an OCO limit
        // leg needs it for the same crossing rule a standalone limit uses.
        var needsReference = request.Type is OrderType.StopLimit or OrderType.TrailingStop
            || (request.Type == OrderType.Limit && request.OcoGroupId is not null);
        var referenceClose = needsReference
            ? await ResolveReferenceCloseAsync(request, ct)
            : (Price?)null;

        // Arming check: a stop-side order must sit beyond the market, exactly
        // like an exchange would refuse an immediately-triggering stop.
        if (referenceClose is { } close && request.StopPrice is { } trigger)
        {
            var armed = request.Side == OrderSide.Buy
                ? trigger.Value > close.Value
                : trigger.Value < close.Value;

            if (!armed)
            {
                throw new InvalidOperationException(
                    $"The {request.Side} {request.Type} trigger {trigger.Value} is not " +
                    (request.Side == OrderSide.Buy ? "above" : "below") +
                    $" the reference close {close.Value}; the order would trigger immediately " +
                    "and is rejected rather than degraded.");
            }
        }

        // The either-or open check runs BEFORE anything is recorded: a
        // rejected leg leaves no phantom working or membership entry behind.
        if (request.OcoGroupId is { } groupId)
        {
            AssertOcoGroupOpenLocked(groupId);
        }

        // A market-crossing limit leg fills at placement — THROWING on a
        // refused portfolio effect exactly like every other placement-time
        // immediate fill, with the pair registry in place so the filled leg
        // tears its siblings down inside the record step.
        if (referenceClose is { } crossingClose && request.Type == OrderType.Limit)
        {
            // The request is validated: a limit always carries its limit price.
            Price limitPrice = request.Price!.Value;
            var crosses = request.Side == OrderSide.Buy
                ? limitPrice.Value >= crossingClose.Value
                : limitPrice.Value <= crossingClose.Value;

            if (crosses)
            {
                var fillOrder = FillOrder(request, limitPrice);
                if (request.OcoGroupId is { } crossingGroupId)
                {
                    // The pair registry lands before the record: the leg's
                    // either-or teardown inside the record step must see its
                    // group.
                    _ocoLegs.GetOrAdd(crossingGroupId, _ => []).Add(fillOrder.Id);
                }

                try
                {
                    return RecordFillLocked(fillOrder, limitPrice);
                }
                catch (InvalidOperationException)
                {
                    // A refused placement-time fill erases the leg entirely
                    // (BUG-033), so the pair record must never keep a member
                    // whose order does not exist.
                    if (request.OcoGroupId is { } refusedGroupId)
                    {
                        RemoveOcoMembership(refusedGroupId, fillOrder.Id);
                    }

                    throw;
                }
            }
        }

        var working = CreateWorkingOrder(request);
        _orders[working.Id] = working;

        var state = new ConditionalState
        {
            Order = working,
            TriggerLevel = request.StopPrice?.Value ?? working.Price.Value,
        };
        _conditionals[working.Id] = state;

        // The group membership lands BEFORE any evaluation so the either-or
        // teardown can always see every leg of the pair it must manage.
        if (request.OcoGroupId is { } armGroupId)
        {
            _ocoLegs.GetOrAdd(armGroupId, _ => []).Add(working.Id);
        }

        EnsurePriceStreamLocked(working.Symbol);

        _logger.LogInformation(
            "Paper arm: {Type} {Side} {Quantity} {Symbol} arms at {Trigger} ({Status})",
            working.Type,
            working.Side,
            working.Quantity.Value,
            working.Symbol.Normalized,
            request.StopPrice?.Value,
            working.Status);

        return working;
    }

    /// <summary>
    /// An OCO leg may only join a group every leg of which still works: a
    /// partner that already filled or was cancelled cannot be resurrected by
    /// a new leg, so the placement is an explicit error (the either-or pairing
    /// would degrade into an orphan). Runs before any store mutation, so a
    /// rejected leg leaves nothing behind.
    /// </summary>
    private void AssertOcoGroupOpenLocked(string groupId)
    {
        if (!_ocoLegs.TryGetValue(groupId, out var legs))
        {
            return;
        }

        var terminal = legs.FirstOrDefault(legId =>
            _orders.TryGetValue(legId, out var leg) &&
            leg.Status is not (OrderStatus.Pending or OrderStatus.Submitted));

        if (terminal is { } terminalLegId)
        {
            var terminalLeg = _orders[terminalLegId];
            throw new InvalidOperationException(
                $"OCO group {groupId} already holds leg {terminalLegId} in status {terminalLeg.Status}; " +
                "the either-or contract cannot hold for a further leg and it is rejected " +
                "rather than degraded into an orphan.");
        }
    }

    /// <summary>
    /// Undo one leg's group membership: a placement-time fill erased for a
    /// refused portfolio effect (BUG-033) leaves the pair registry exactly as
    /// it was before the leg existed.
    /// </summary>
    private void RemoveOcoMembership(string groupId, string legId)
    {
        if (_ocoLegs.TryGetValue(groupId, out var legs))
        {
            legs.Remove(legId);
            if (legs.Count == 0)
            {
                _ocoLegs.TryRemove(groupId, out _);
            }
        }
    }

    /// <summary>
    /// Record a pre-built filled order and apply its portfolio effect, under
    /// the trading lock. A portfolio refusal (insufficient balance) throws —
    /// placement-time fills surface the refusal to the caller and leave no
    /// phantom record (BUG-033).
    /// </summary>
    private Order RecordFillLocked(Order filled, Price fillPrice)
    {
        // The portfolio applies BEFORE the order is recorded: a fill rejected
        // for insufficient balance throws out of here and leaves no phantom
        // Filled record behind — the caller receives the exception and every
        // other caller-visible list stays honest (BUG-033).
        ApplyFillToPortfolio(filled, fillPrice);
        _orders[filled.Id] = filled;

        _logger.LogInformation(
            "Paper fill: {Side} {Quantity} {Symbol} at {Price} ({Type})",
            filled.Side,
            filled.Quantity.Value,
            filled.Symbol.Normalized,
            fillPrice.Value,
            filled.Type);

        // A filled leg of an OCO pair takes every remaining leg down with it.
        CancelOcoSiblingsLocked(filled);

        return filled;
    }

    /// <inheritdoc/>
    public Task CancelOrderAsync(Symbol symbol, string orderId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orderId);
        ArgumentNullException.ThrowIfNull(symbol);

        return CancelOrderCoreAsync(symbol, orderId, ct);
    }

    private async Task CancelOrderCoreAsync(Symbol symbol, string orderId, CancellationToken ct)
    {
        // The caller's cancellation token reaches the lock acquisition too: a
        // wedged lock holder can no longer keep cancellation pending forever
        // (BUG-035). The pre-acquisition await is outside the try, so a
        // cancelled wait never releases an un-acquired semaphore.
        await _tradingLock.WaitAsync(ct);
        try
        {
            if (!_orders.TryGetValue(orderId, out var order))
                throw new InvalidOperationException($"Order {orderId} is unknown to the paper adapter.");

            if (order.Symbol != symbol)
                throw new InvalidOperationException(
                    $"Order {orderId} belongs to {order.Symbol.Normalized}, not to {symbol.Normalized}.");

            if (order.Status is not (OrderStatus.Pending or OrderStatus.Submitted))
                throw new InvalidOperationException(
                    $"Order {orderId} is already {order.Status}; only pending orders can be cancelled.");

            _orders[orderId] = order with
            {
                Status = OrderStatus.Cancelled,
                UpdatedAt = DateTimeOffset.UtcNow,
            };

            _conditionals.TryRemove(orderId, out _);
            MaybeTearDownPriceStreamLocked(order.Symbol);
            _logger.LogInformation("Paper cancel: {OrderId}", orderId);

            // Cancelling one leg of an OCO pair ends the pair: the sibling
            // legs cannot outlive their partner without degrading into
            // orphans.
            CancelOcoSiblingsLocked(order with { Status = OrderStatus.Cancelled });
        }
        finally
        {
            _tradingLock.Release();
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<Order>> GetOpenOrdersAsync(CancellationToken ct = default)
    {
        var open = _orders.Values
            .Where(o => o.Status is OrderStatus.Pending or OrderStatus.Submitted or OrderStatus.PartiallyFilled)
            .ToArray();

        return Task.FromResult<IReadOnlyList<Order>>(open);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<Position>> GetPositionsAsync(CancellationToken ct = default)
    {
        var positions = _positions.Values.Select(ToEntity).ToArray();
        return Task.FromResult<IReadOnlyList<Position>>(positions);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyDictionary<string, Money>> GetBalancesAsync(CancellationToken ct = default)
    {
        IReadOnlyDictionary<string, Money> balances = _balances.ToDictionary(
            kv => kv.Key,
            kv => new Money(kv.Value, kv.Key));

        return Task.FromResult(balances);
    }

    #endregion

    #region Fill Simulation

    /// <summary>
    /// Resolve the execution price of an immediate fill without guessing: a
    /// stop-market fills at its stop price, a market order takes the latest
    /// close from the wrapped adapter (failing loudly when no reference data
    /// is available). Limit orders never reach here: the crossing decision in
    /// <see cref="PlaceLimitOrderCoreAsync"/> routes them first, conditional
    /// orders (stop-limit, trailing stop, OCO legs) go through the
    /// <see cref="PlaceConditionalOrderCoreAsync"/> arming path.
    /// </summary>
    private async Task<Price> ResolveFillPriceAsync(OrderRequest request, CancellationToken ct)
    {
        if (request.Price is { } limitPrice)
            return limitPrice;

        if (request.StopPrice is { } stopPrice)
            return stopPrice;

        return await ResolveReferenceCloseAsync(request, ct);
    }

    /// <summary>
    /// Latest H1 close from the wrapped adapter — the market's reference price: the
    /// fill price of market orders and the crossing yardstick for limit orders. The
    /// whole lookback window is fetched and the newest bar is selected BY ITS TIME,
    /// never by list position: backends page bars differently (Binance klines are
    /// oldest-first, Bybit/Demo serve the latest first), so the prior
    /// position-based <c>bars[^1]</c> on a fixed <c>limit: 1</c> from
    /// <c>now - 24h</c> silently took a ~24-hour-old close on ascending backends
    /// (BUG-006). Fails loudly when no reference data is available: a paper fill
    /// without a market price would corrupt the simulation.
    /// </summary>
    private async Task<Price> ResolveReferenceCloseAsync(OrderRequest request, CancellationToken ct)
    {
        var bars = await _marketData.GetBarsAsync(
            request.Symbol,
            ReferenceTimeFrame,
            DateTimeOffset.UtcNow.AddHours(-ReferenceBarLookbackHours),
            limit: ReferenceBarFetchLimit,
            ct: ct);

        if (bars.Count == 0)
            throw new InvalidOperationException(
                $"No reference bar available for {request.Symbol.Normalized}; cannot fill a {request.Type} order.");

        var newest = bars.Aggregate((latest, candidate) =>
            candidate.Time > latest.Time ? candidate : latest);
        return new Price(newest.Close);
    }

    /// <summary>The immediate-fill factory: the order records its full quantity.</summary>
    private Order FillOrder(OrderRequest request, Price fillPrice) =>
        CreateOrder(request, OrderStatus.Filled, fillPrice, request.Quantity);

    /// <summary>The resting factory: a non-crossing limit keeps its own limit price and fills nothing.</summary>
    private Order CreateRestingOrder(OrderRequest request)
    {
        // The request is validated: a limit always carries its limit price.
        return CreateOrder(request, OrderStatus.Pending, request.Price!.Value, Quantity.Zero);
    }

    /// <summary>The conditional factory: the order arms as Pending. The working
    /// record's display price is the stop-limit's own limit price, or the
    /// trailing stop's trigger seed (a trailing order never carries a limit
    /// price; the fill later records the executed level).</summary>
    private Order CreateWorkingOrder(OrderRequest request)
    {
        var displayPrice = request.Type == OrderType.TrailingStop
            ? request.StopPrice!.Value
            : request.Price!.Value;

        return CreateOrder(request, OrderStatus.Pending, displayPrice, Quantity.Zero);
    }

    /// <summary>Shared order field copy — the paper adapter's single order spelling.</summary>
    private Order CreateOrder(
        OrderRequest request,
        OrderStatus status,
        Price fillPrice,
        Quantity filledQuantity)
    {
        var id = $"{OrderIdPrefix}{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;

        return new Order
        {
            Id = id,
            ExchangeId = Id,
            ExchangeOrderId = id,
            Symbol = request.Symbol,
            Side = request.Side,
            Type = request.Type,
            Quantity = request.Quantity,
            Price = fillPrice,
            StopPrice = request.StopPrice,
            TrailDistance = request.TrailDistance,
            OcoGroupId = request.OcoGroupId,
            TimeInForce = request.TimeInForce,
            Expiry = request.Expiry,
            ClientOrderId = request.ClientOrderId,
            Status = status,
            FilledQuantity = filledQuantity,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    private void ApplyFillToPortfolio(Order order, Price fillPrice)
    {
        // Buying base asset, spending quote; selling does the reverse (negative base = short).
        var baseDelta = order.Side == OrderSide.Buy
            ? order.Quantity.Value
            : -order.Quantity.Value;
        var notional = fillPrice.Value * order.Quantity.Value;
        var quoteDelta = order.Side == OrderSide.Buy ? -notional : notional;

        // Balance is validated before any state mutation, so a rejected order
        // leaves the portfolio untouched (nothing to roll back).
        var quote = _balances.GetValueOrDefault(QuoteCurrency, InitialQuoteAmount);
        if (quote + quoteDelta < 0)
            throw new InvalidOperationException(
                $"Insufficient simulated {QuoteCurrency} balance for the order.");

        _balances[QuoteCurrency] = quote + quoteDelta;
        _balances.AddOrUpdate(order.Symbol.BaseAsset, baseDelta, (_, current) => current + baseDelta);

        NetPositionFromFill(order, fillPrice, baseDelta);
    }

    private void NetPositionFromFill(Order order, Price fillPrice, decimal signedDelta)
    {
        var symbolKey = order.Symbol.Normalized;
        var net = _positions.GetOrAdd(
            symbolKey,
            _ => new NetPosition { SymbolKey = symbolKey, OpenedAt = order.CreatedAt });

        var currentSigned = net.Side == OrderSide.Buy ? net.Quantity : -net.Quantity;
        var newSigned = currentSigned + signedDelta;

        if (newSigned == 0)
        {
            // Flat: the position is fully closed and removed.
            _positions.TryRemove(symbolKey, out _);
            return;
        }

        if (currentSigned == 0)
        {
            // Opening from flat: the fresh position is stamped at its own fill.
            net.OpenedAt = order.CreatedAt;
            net.EntryPrice = fillPrice.Value;
            net.Quantity = Math.Abs(newSigned);
        }
        else if (decimal.Sign(newSigned) == decimal.Sign(currentSigned))
        {
            if (Math.Abs(newSigned) > Math.Abs(currentSigned))
            {
                // Extending in the same direction: weighted average entry over
                // the whole new size.
                var previousAbs = Math.Abs(currentSigned);
                var deltaAbs = Math.Abs(signedDelta);

                net.EntryPrice = ((net.EntryPrice * previousAbs) + (fillPrice.Value * deltaAbs))
                    / Math.Abs(newSigned);
                net.Quantity = Math.Abs(newSigned);
            }
            else
            {
                // Reducing (BUG-005): the fill closes part of the position at
                // the fill price; the average entry of the REMAINING size stays
                // exactly as it was (a partial close never re-averages it).
                net.Quantity = Math.Abs(newSigned);
            }
        }
        else
        {
            // Crossing zero: the closing part is absorbed (realized PnL is out
            // of scope here), the remainder opens a fresh position at the fill
            // price — stamped at its OWN fill time, never inheriting the closed
            // position's timestamp (BUG-034).
            net.OpenedAt = order.CreatedAt;
            net.EntryPrice = fillPrice.Value;
            net.Quantity = Math.Abs(newSigned);
        }

        net.Side = newSigned > 0 ? OrderSide.Buy : OrderSide.Sell;
        net.LastPrice = fillPrice.Value;
    }

    private Position ToEntity(NetPosition net)
    {
        var symbol = Symbol.Parse(net.SymbolKey);
        return new Position
        {
            Id = $"{PositionIdPrefix}{net.SymbolKey}",
            ExchangeId = Id,
            Symbol = symbol,
            Side = net.Side,
            Quantity = new Quantity(net.Quantity),
            EntryPrice = new Price(net.EntryPrice),
            CurrentPrice = new Price(net.LastPrice),
            OpenedAt = net.OpenedAt,
        };
    }

    /// <summary>Mutable accumulator for net exposure on one symbol (paper adapter internal).</summary>
    private sealed class NetPosition
    {
        /// <summary>Normalized symbol key ("BTC/USDT").</summary>
        public string SymbolKey { get; init; } = string.Empty;

        /// <summary>Direction of the net exposure.</summary>
        public OrderSide Side { get; set; }

        /// <summary>Absolute net quantity.</summary>
        public decimal Quantity { get; set; }

        /// <summary>Weighted average entry price.</summary>
        public decimal EntryPrice { get; set; }

        /// <summary>Last fill price, used as the current marker price.</summary>
        public decimal LastPrice { get; set; }

        /// <summary>When the position first became non-zero.</summary>
        public DateTimeOffset OpenedAt { get; set; }
    }

    #endregion

    #region Conditional Simulation

    /// <summary>
    /// One ticker update, evaluated under the trading lock: every working
    /// conditional order of the pushed symbol gets its trigger check (a
    /// trailing stop ratchets its trigger along the favorable direction
    /// first). A triggered leg fills; a trigger whose fill is refused
    /// (insufficient balance) lands the leg as Rejected — either way the
    /// leg's OCO siblings are cancelled best-effort, so the pair never leaves
    /// a working orphan.
    /// </summary>
    private async Task EvaluateTickAsync(Symbol symbol, decimal lastPrice)
    {
        await _tradingLock.WaitAsync();
        try
        {
            var key = symbol.Normalized;
            var working = _conditionals.Values
                .Where(state => state.Order.Symbol.Normalized == key)
                .ToArray();

            foreach (var state in working)
            {
                // Re-check membership: a sibling torn down earlier in this very
                // tick (a refused trigger cancelling the rest of its pair) must
                // not resurrect from the loop's stale snapshot and re-execute
                // its terminal transition.
                if (!_conditionals.ContainsKey(state.Order.Id))
                {
                    continue;
                }

                if (EvaluateTriggerLocked(state, lastPrice) is not { } fillPrice)
                {
                    continue;
                }

                FillConditionalLocked(state, fillPrice);
            }
        }
        finally
        {
            _tradingLock.Release();
        }
    }

    /// <summary>
    /// The terminal-transition fill of a triggered conditional: the fill rides
    /// the shared record path, the working state disappears, and the OCO
    /// siblings go down with the leg. A portfolio refusal (insufficient
    /// balance) rejects the trigger instead of throwing out of the evaluation
    /// lane — the caller is long gone there, so the refusal becomes an order
    /// status, not an exception.
    /// </summary>
    private void FillConditionalLocked(ConditionalState state, Price fillPrice)
    {
        var order = state.Order with
        {
            Status = OrderStatus.Filled,
            FilledQuantity = state.Order.Quantity,
            Price = fillPrice,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        try
        {
            RecordFillLocked(order, fillPrice);
        }
        catch (InvalidOperationException ex)
        {
            RejectConditionalLocked(state, ex.Message);
            return;
        }

        _conditionals.TryRemove(order.Id, out _);
        MaybeTearDownPriceStreamLocked(order.Symbol);
    }

    /// <summary>
    /// The terminal transition of a refused conditional trigger: the order
    /// records Rejected with the refusal reason, the working state disappears,
    /// and the OCO siblings of the dead leg are cancelled best-effort — the
    /// either-or contract holds whatever ended the leg.
    /// </summary>
    private void RejectConditionalLocked(ConditionalState state, string reason)
    {
        var rejected = state.Order with
        {
            Status = OrderStatus.Rejected,
            RejectReason = reason,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        _orders[rejected.Id] = rejected;
        _conditionals.TryRemove(rejected.Id, out _);
        MaybeTearDownPriceStreamLocked(rejected.Symbol);

        _logger.LogError(
            "Conditional rejected: {OrderId} ({Type} {Symbol}): {Reason}",
            rejected.Id,
            rejected.Type,
            rejected.Symbol.Normalized,
            reason);

        CancelOcoSiblingsLocked(rejected);
    }

    /// <summary>
    /// The trigger decision for one working conditional at the pushed price:
    /// the fill price when the trigger fires, null while it does not.
    /// </summary>
    private Price? EvaluateTriggerLocked(ConditionalState state, decimal lastPrice)
    {
        var order = state.Order;
        switch (order.Type)
        {
            // The arming matrix (the limit sits at or above the trigger for
            // buys and at or below it for sells) guarantees the limit leg
            // crosses the market at trigger time, so the paper fill executes
            // at the limit price itself — the same rule a crossing standalone
            // limit uses.
            case OrderType.StopLimit when order.StopPrice is { } trigger:
            {
                var stopped = order.Side == OrderSide.Buy
                    ? lastPrice >= trigger.Value
                    : lastPrice <= trigger.Value;

                return stopped ? order.Price : (Price?)null;
            }

            // The trailing trigger only ever moves in the favorable direction:
            // a Sell order's trigger ratchets up, a Buy order's down. The stop
            // fills at its trigger level, like every other paper stop fill.
            case OrderType.TrailingStop
                when order.TrailDistance is { } trail
                && order.Side == OrderSide.Sell:
            {
                state.TriggerLevel = Math.Max(state.TriggerLevel, lastPrice - trail.Value);
                return lastPrice <= state.TriggerLevel ? new Price(state.TriggerLevel) : null;
            }

            case OrderType.TrailingStop
                when order.TrailDistance is { } trail:
            {
                state.TriggerLevel = Math.Min(state.TriggerLevel, lastPrice + trail.Value);
                return lastPrice >= state.TriggerLevel ? new Price(state.TriggerLevel) : null;
            }

            // A working limit as the take-profit leg of an OCO pair owes the
            // leg's fill despite never having a stop side: the resting limit
            // is matched against the stream with the same crossing rule as
            // placement. Standalone resting limits keep the documented
            // rest-until-cancel behavior (no automatic matching loop).
            case OrderType.Limit when order.Price is { } limitPrice:
            {
                var crosses = order.Side == OrderSide.Buy
                    ? limitPrice.Value >= lastPrice
                    : limitPrice.Value <= lastPrice;

                return crosses ? limitPrice : null;
            }

            default:
                return null;
        }
    }

    /// <summary>
    /// Either-or teardown: the leg that just reached a terminal state cancels
    /// its group siblings (those still working). Runs under the trading lock
    /// and is best-effort: a sibling unknown to the store, or already
    /// terminal, is skipped. The group membership itself always stays
    /// recorded — a later leg cannot resurrect a half pair.
    /// </summary>
    private void CancelOcoSiblingsLocked(Order terminalLeg)
    {
        if (terminalLeg.OcoGroupId is not { } groupId ||
            !_ocoLegs.TryGetValue(groupId, out var legs))
        {
            return;
        }

        foreach (var legId in legs)
        {
            if (legId == terminalLeg.Id || !_orders.TryGetValue(legId, out var leg))
            {
                continue;
            }

            if (leg.Status is not (OrderStatus.Pending or OrderStatus.Submitted or OrderStatus.PartiallyFilled))
            {
                continue;
            }

            _orders[legId] = leg with
            {
                Status = OrderStatus.Cancelled,
                UpdatedAt = DateTimeOffset.UtcNow,
            };

            _conditionals.TryRemove(legId, out _);
            MaybeTearDownPriceStreamLocked(leg.Symbol);

            _logger.LogInformation(
                "OCO group {GroupId}: leg {LegId} cancels with its {PartnerId} partner",
                groupId,
                legId,
                terminalLeg.Id);
        }
    }

    /// <summary>
    /// Attach the symbol's live price stream once — the first working
    /// conditional subscribes and the lane lives until the last one leaves
    /// (or the adapter is disposed). The subscription serializes trigger
    /// evaluations per symbol through <see cref="Concat"/>: never overlapping,
    /// never an async-void callback. A stream that fails to start or dies
    /// later leaves the conditionals working without evaluation — never a
    /// crash, and cancellation always stays available.
    /// </summary>
    private void EnsurePriceStreamLocked(Symbol symbol)
    {
        var key = symbol.Normalized;
        if (_priceStreams.ContainsKey(key))
        {
            return;
        }

        try
        {
            var subscription = _marketData
                .SubscribeTicker(symbol)
                .Select(ticker => Observable.FromAsync(
                    () => EvaluateTickAsync(symbol, ticker.LastPrice)))
                .Concat()
                .Subscribe(
                    onNext: _ => { },
                    onError: error => OnPriceStreamFaulted(key, error));

            _priceStreams[key] = subscription;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Paper simulation: the price stream for {Symbol} failed to start; the conditional orders stay working but no trigger evaluates on the stream",
                key);
        }
    }

    /// <summary>Stream teardown when the symbol no longer holds working conditionals.</summary>
    private void MaybeTearDownPriceStreamLocked(Symbol symbol)
    {
        var key = symbol.Normalized;
        if (_conditionals.Values.Any(state => state.Order.Symbol.Normalized == key))
        {
            return;
        }

        if (_priceStreams.TryRemove(key, out var subscription))
        {
            subscription.Dispose();
            _logger.LogInformation(
                "Paper simulation: the price stream for {Symbol} ends with its last conditional order",
                key);
        }
    }

    /// <summary>
    /// A faulted stream lane cannot be restarted from the callback itself:
    /// forget the subscription so the next placement re-subscribes, and log
    /// the teardown. The working orders stay working; cancellation always
    /// stays available.
    /// </summary>
    private void OnPriceStreamFaulted(string symbolKey, Exception error)
    {
        if (_priceStreams.TryRemove(symbolKey, out var subscription))
        {
            subscription.Dispose();
        }

        _logger.LogWarning(
            error,
            "Paper simulation: the price stream for {Symbol} faulted; trigger evaluation stops until the next placement",
            symbolKey);
    }

    /// <summary>
    /// Mutable simulation state of one stream-evaluated working order.
    /// </summary>
    private sealed class ConditionalState
    {
        /// <summary>Latest snapshot of the working order.</summary>
        public required Order Order { get; init; }

        /// <summary>
        /// The trigger level the order currently arms at: the trailing stop's
        /// ratcheting state; every other conditional keeps its placement
        /// trigger.
        /// </summary>
        public decimal TriggerLevel { get; set; }
    }

    #endregion

    #region IAsyncDisposable

    public ValueTask DisposeAsync()
    {
        foreach (var subscription in _priceStreams.Values)
        {
            subscription.Dispose();
        }

        _priceStreams.Clear();
        _conditionals.Clear();
        _ocoLegs.Clear();
        _tradingLock.Dispose();
        _orders.Clear();
        _positions.Clear();
        _balances.Clear();

        // The wrapped market data adapter has its own lifetime in the DI container.
        return ValueTask.CompletedTask;
    }

    #endregion
}
