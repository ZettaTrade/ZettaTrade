namespace ZettaTrade.Infrastructure.Exchange;

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// Simulated (paper) trading adapter. Market, stop and market-crossing limit orders
/// fill immediately against prices taken from the wrapped <see cref="IMarketDataAdapter"/>,
/// while a limit order resting away from the market stays Pending (and cancelable) until
/// it is cancelled — there is no automatic matching loop. In-memory balances and netted
/// positions are maintained for every fill. Never touches real funds or exchange keys,
/// which makes it the safe default trading surface for MCP tools and demos on any exchange.
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

    #endregion

    #region Fields

    private readonly IMarketDataAdapter _marketData;
    private readonly ILogger<PaperTradingAdapter> _logger;

    /// <summary>Serializes order placement / cancellation; positions are mutated under it.</summary>
    private readonly SemaphoreSlim _tradingLock = new(1, 1);

    private readonly ConcurrentDictionary<string, Order> _orders = new();
    private readonly ConcurrentDictionary<string, NetPosition> _positions = new();
    private readonly ConcurrentDictionary<string, decimal> _balances = new();

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
            if (request.Type == OrderType.Limit)
            {
                return await PlaceLimitOrderCoreAsync(request, ct);
            }

            // Market and stop orders keep the long-standing immediate-fill semantics.
            var fillPrice = await ResolveFillPriceAsync(request, ct);
            return FillAndApply(request, fillPrice);
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
        return FillAndApply(request, limitPrice);
    }

    /// <summary>Record a filled order and apply its portfolio effect, under the trading lock.</summary>
    private Order FillAndApply(OrderRequest request, Price fillPrice)
    {
        var order = FillOrder(request, fillPrice);
        _orders[order.Id] = order;
        ApplyFillToPortfolio(order, fillPrice);

        _logger.LogInformation(
            "Paper fill: {Side} {Quantity} {Symbol} at {Price} ({Type})",
            order.Side,
            order.Quantity.Value,
            order.Symbol.Normalized,
            fillPrice.Value,
            order.Type);

        return order;
    }

    /// <inheritdoc/>
    public Task CancelOrderAsync(Symbol symbol, string orderId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orderId);
        ArgumentNullException.ThrowIfNull(symbol);

        return CancelOrderCoreAsync(symbol, orderId);
    }

    private async Task CancelOrderCoreAsync(Symbol symbol, string orderId)
    {
        await _tradingLock.WaitAsync();
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

            _logger.LogInformation("Paper cancel: {OrderId}", orderId);
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
    /// Resolve the execution price of an immediate fill without guessing: a stop-limit
    /// order fills at its own limit price, a stop-market at its stop price, and a market
    /// order takes the latest close from the wrapped adapter (failing loudly when no
    /// reference data is available). Limit orders never reach here: the crossing
    /// decision in <see cref="PlaceLimitOrderCoreAsync"/> routes them first.
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
    /// fill price of market orders and the crossing yardstick for limit orders. Fails
    /// loudly when no reference data is available: a paper fill without a market
    /// price would corrupt the simulation.
    /// </summary>
    private async Task<Price> ResolveReferenceCloseAsync(OrderRequest request, CancellationToken ct)
    {
        var bars = await _marketData.GetBarsAsync(
            request.Symbol,
            ReferenceTimeFrame,
            DateTimeOffset.UtcNow.AddHours(-ReferenceBarLookbackHours),
            limit: 1,
            ct: ct);

        if (bars.Count == 0)
            throw new InvalidOperationException(
                $"No reference bar available for {request.Symbol.Normalized}; cannot fill a {request.Type} order.");

        return new Price(bars[^1].Close);
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
            _positions.TryRemove(symbolKey, out _);
            return;
        }

        if (currentSigned == 0 || decimal.Sign(newSigned) == decimal.Sign(currentSigned))
        {
            // Extending in the same direction (or opening): weighted average entry.
            var previousAbs = Math.Abs(currentSigned);
            var deltaAbs = Math.Abs(signedDelta);
            var newAbs = Math.Abs(newSigned);

            if (currentSigned == 0)
            {
                net.OpenedAt = order.CreatedAt;
                net.EntryPrice = fillPrice.Value;
            }
            else
            {
                net.EntryPrice = ((net.EntryPrice * previousAbs) + (fillPrice.Value * deltaAbs)) / newAbs;
            }

            net.Side = newSigned > 0 ? OrderSide.Buy : OrderSide.Sell;
            net.Quantity = newAbs;
        }
        else
        {
            // Crossing zero: the closing part is absorbed (realized PnL is out of scope here),
            // the remainder opens a fresh position at the fill price.
            net.Side = newSigned > 0 ? OrderSide.Buy : OrderSide.Sell;
            net.Quantity = Math.Abs(newSigned);
            net.EntryPrice = fillPrice.Value;
        }

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

    #region IAsyncDisposable

    public ValueTask DisposeAsync()
    {
        _tradingLock.Dispose();
        _orders.Clear();
        _positions.Clear();
        _balances.Clear();

        // The wrapped market data adapter has its own lifetime in the DI container.
        return ValueTask.CompletedTask;
    }

    #endregion
}
