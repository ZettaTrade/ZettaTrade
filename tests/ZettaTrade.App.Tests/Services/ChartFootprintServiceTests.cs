namespace ZettaTrade.App.Tests.Services;

using System;
using System.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using ZettaTrade.App.Services;
using ZettaTrade.Charting;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.Infrastructure.Exchange;
using ZettaTrade.Infrastructure.Persistence;
using ZettaTrade.Infrastructure.PnL;

/// <summary>
/// Tests for the chart footprint feed (T-122): the pure composition maps
/// the trading lane's own state to the keyed drawing payloads (direction
/// by the quantity's sign, price-bearing orders only, journal-of-record
/// fills ascending by time), rows of other lanes and honest absences stay
/// out; the drain loop publishes the standing snapshot and pulls the
/// adapter exactly when the accounts' shaped state moved.
/// </summary>
public sealed class ChartFootprintServiceTests
{
    private const string LaneId = PaperTradingAdapter.ExchangeId;

    private readonly ITradingAdapter _trading = Substitute.For<ITradingAdapter>();
    private readonly IOrderHistoryStore _journal = Substitute.For<IOrderHistoryStore>();

    public ChartFootprintServiceTests()
    {
        _trading.Id.Returns(LaneId);
        _trading.GetOpenOrdersAsync(Arg.Any<CancellationToken>()).Returns([]);
        _journal.GetRecentAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
    }

    #region Tests: the composition matrix

    /// <summary>
    /// The lane's non-flat accounts map to entry overlays with the
    /// direction the signed quantity carries; flat accounts and another
    /// exchange's rows stay out.
    /// </summary>
    [Fact]
    public void TheLanePositions_MapToEntryOverlays_OthersStayOut()
    {
        var snapshot = ChartFootprintService.Compose(
            LaneId,
            [
                Position(LaneId, "BTC/USDT", 1.5m, 100m, 110m, new Money(15m, "USDT")),
                Position(LaneId, "ETH/USDT", -2m, 50m, null, new Money(0m, string.Empty)),
                Position(LaneId, "SOL/USDT", 0m, 0m, null, new Money(0m, string.Empty)),
                Position("binance", "BNB/USDT", 3m, 600m, null, new Money(0m, string.Empty)),
            ],
            [],
            []);

        snapshot.Positions.Select(static p => p.Symbol).Should().Equal("BTC/USDT", "ETH/USDT");
        snapshot.Positions[0].Overlay.Direction.Should().Be(PositionDirection.Long, "the positive quantity is the long stance");
        snapshot.Positions[0].Overlay.EntryPrice.Should().Be(100m);
        snapshot.Positions[0].Overlay.LatestPrice.Should().Be(110m);
        snapshot.Positions[0].Overlay.UnrealizedPnl.Amount.Should().Be(15m);
        snapshot.Positions[1].Overlay.Direction.Should().Be(PositionDirection.Short, "the negative quantity is the short stance");
        snapshot.Orders.Should().BeEmpty();
        snapshot.Fills.Should().BeEmpty();
    }

    /// <summary>
    /// Only price-bearing orders (LIMIT legs) map to working lines: a stop
    /// or trailing leg carries no standing limit level and owns no line.
    /// </summary>
    [Fact]
    public void ThePriceBearingOrders_MapToDashedLines_StopsStayOut()
    {
        var snapshot = ChartFootprintService.Compose(
            LaneId,
            [],
            [
                Order(OrderType.Limit, OrderSide.Buy, "BTC/USDT", 99_000m, LaneId),
                Order(OrderType.Limit, OrderSide.Sell, "ETH/USDT", 3_500m, LaneId),
                Order(OrderType.StopMarket, OrderSide.Sell, "BTC/USDT", 88_000m, LaneId),
                Order(OrderType.Market, OrderSide.Buy, "SOL/USDT", 180m, LaneId),
            ],
            []);

        snapshot.Orders.Select(static o => o.Symbol).Should().Equal("BTC/USDT", "ETH/USDT");
        snapshot.Orders[0].Line.Side.Should().Be(OrderSide.Buy);
        snapshot.Orders[0].Line.Price.Should().Be(99_000m);
        snapshot.Orders[1].Line.Price.Should().Be(3_500m);
        snapshot.Positions.Should().BeEmpty();
        snapshot.Fills.Should().BeEmpty();
    }

    /// <summary>
    /// The working stop legs map to the measure ruler's stop context
    /// (T-200): every stop-shaped leg's trigger price rides the snapshot
    /// keyed by its normalized symbol — the limit legs, the missing and
    /// the zero triggers stay out (a stop without a trigger prices no
    /// risk distance).
    /// </summary>
    [Fact]
    public void TheStopLegs_MapToStopContexts_OthersStayOut()
    {
        var snapshot = ChartFootprintService.Compose(
            LaneId,
            [],
            [
                Order(OrderType.StopMarket, OrderSide.Sell, "BTC/USDT", 88_000m, LaneId, stopPrice: 88_000m),
                Order(OrderType.StopLimit, OrderSide.Sell, "BTC/USDT", 88_100m, LaneId, stopPrice: 88_050m),
                Order(OrderType.TrailingStop, OrderSide.Sell, "BTC/USDT", 0m, LaneId, stopPrice: 91_500m),
                Order(OrderType.StopMarket, OrderSide.Sell, "ETH/USDT", 0m, LaneId, stopPrice: null),
                Order(OrderType.StopMarket, OrderSide.Sell, "ETH/USDT", 0m, LaneId, stopPrice: 0m),
                Order(OrderType.Limit, OrderSide.Buy, "SOL/USDT", 180m, LaneId, stopPrice: 170m),
            ],
            []);

        snapshot.Stops.Select(static s => (s.Symbol, s.Price)).Should().Equal(
            ("BTC/USDT", 88_000m),
            ("BTC/USDT", 88_050m),
            ("BTC/USDT", 91_500m));
        snapshot.Stops.Select(static s => s.Symbol).Should().NotContain("SOL/USDT",
            "a limit leg never prices a stop context, whatever its fields say");
        snapshot.Stops.Should().NotContain(s => s.Symbol == "ETH/USDT",
            "a stop without a trigger prices no risk distance");
    }

    /// <summary>
    /// The journal's filled rows map to fill markers ordered by their own
    /// times: another lane's rows, non-filled statuses and zero-quantity
    /// rows stay out.
    /// </summary>
    [Fact]
    public void TheJournalFills_MapToMarkers_LaneQualifiedAscending()
    {
        var snapshot = ChartFootprintService.Compose(
            LaneId,
            [],
            [],
            [
                Journal(OrderStatus.Filled, LaneId, "BTC/USDT", 101_000m, AtMinutes(5)),
                Journal(OrderStatus.Filled, "binance", "BNB/USDT", 600m, AtMinutes(4)),
                Journal(OrderStatus.Rejected, LaneId, "ETH/USDT", 3_500m, AtMinutes(3)),
                Journal(OrderStatus.Filled, LaneId, "SOL/USDT", 180m, AtMinutes(2)),
                Journal(OrderStatus.Filled, LaneId, "BTC/USDT", 99_500m, AtMinutes(1)),
            ]);

        snapshot.Fills.Select(static f => f.Symbol).Should().Equal("BTC/USDT", "SOL/USDT", "BTC/USDT");
        snapshot.Fills.Select(static f => f.Marker.Price).Should().Equal(99_500m, 180m, 101_000m);
        snapshot.Fills.Select(static f => f.Marker.Time).Should().BeInAscendingOrder();
        snapshot.Positions.Should().BeEmpty();
        snapshot.Orders.Should().BeEmpty();
    }

    /// <summary>
    /// Rows that carry an exchange id (the engine's accounts and the
    /// journal) qualify to the lane; the open-order read needs no
    /// qualification — it is the lane's OWN adapter read by construction.
    /// </summary>
    [Fact]
    public void EveryInput_LaneQualifiesTogether()
    {
        var snapshot = ChartFootprintService.Compose(
            "binance",
            [Position(LaneId, "BTC/USDT", 1m, 100m, null, new Money(0m, string.Empty))],
            [],
            [Journal(OrderStatus.Filled, LaneId, "BTC/USDT", 100_500m, AtMinutes(1))]);

        snapshot.Positions.Should().BeEmpty("a foreign lane's account never paints another lane's chart");
        snapshot.Fills.Should().BeEmpty("a foreign lane's ledger row never paints another lane's chart");
        snapshot.Orders.Should().BeEmpty();
    }

    #endregion Tests: the composition matrix

    #region Tests: the drain loop's behavior

    /// <summary>
    /// The standing snapshot arrives on subscribe (the subject's replay),
    /// and the lane's inventory publishes once the boot read and the first
    /// accounts' emission have been handled (either one batch or two — the
    /// fact gates on the publication, never on the batch borders).
    /// </summary>
    [Fact]
    public async Task TheDrainLoop_PublishesTheStandingInventory_WithTheBootRead()
    {
        var pnl = new BehaviorSubject<PnlSnapshot>(PnlSnapshot.Empty);
        // The substitute's read is configured BEFORE InitializeAsync: the
        // boot drain may pull before the fact drives the first emission.
        var order = Order(OrderType.Limit, OrderSide.Buy, "BTC/USDT", 89_000m, LaneId);
        _trading.GetOpenOrdersAsync(Arg.Any<CancellationToken>()).Returns([order]);
        var service = CreateService(pnl);
        await service.InitializeAsync();

        TakePublished(service.Updates);

        using var published = new FirstDelivery(
            service.Updates,
            static s => s.Positions.Count == 1 && s.Orders.Count == 1);
        pnl.OnNext(new PnlSnapshot { Positions = [Position(LaneId, "BTC/USDT", 1m, 90_000m, null, new Money(0m, string.Empty))] });

        var drawn = await published.Task.WaitAsync(CompletionBudget);
        drawn.Positions.Should().ContainSingle("the lane's own position rides the stream").Which
            .Overlay.EntryPrice.Should().Be(90_000m);
        drawn.Positions[0].Overlay.LatestPrice.Should().BeNull("no price has been observed yet");
        drawn.Orders.Should().ContainSingle().Which.Line.Price.Should().Be(89_000m);
    }

    /// <summary>
    /// The read economy: the boot read and the first shaped emission may
    /// fold into one pull (which ever way, the count is measured AFTER the
    /// first publication), a price-only refresh recomposes from the caches
    /// (no pull), and the next shaped change pulls once more.
    /// </summary>
    [Fact]
    public async Task TheReadEconomy_RefreshesOnlyOnShapedChanges()
    {
        var pnl = new BehaviorSubject<PnlSnapshot>(PnlSnapshot.Empty);
        var service = CreateService(pnl);
        await service.InitializeAsync();
        TakePublished(service.Updates);

        // The boot read and the shaped boot position: either folds or does
        // not, the delta discipline below absorbs both orderings.
        using var boot = new FirstDelivery(service.Updates, static s => s.Positions.Count == 1);
        pnl.OnNext(new PnlSnapshot { Positions = [AtEntry] });
        await boot.Task.WaitAsync(CompletionBudget);
        var pullsSoFar = Pulls();

        // Price-only: the same shape ticks its last price; no adapter pull.
        using var priced = new FirstDelivery(
            service.Updates,
            static s => s.Positions.Count == 1 && s.Positions[0].Overlay.LatestPrice == 100_000m);
        pnl.OnNext(new PnlSnapshot { Positions = [AtEntry] });
        pnl.OnNext(new PnlSnapshot { Positions = [AtEntry with { LastPrice = 100_000m, UnrealizedPnl = new Money(10_000m, "USDT") }] });

        await priced.Task.WaitAsync(CompletionBudget);
        Pulls().Should().Be(pullsSoFar, "a price-only refresh recomposes from the caches, never the adapter");

        // Shaped: the quantity doubled — the drain pulls exactly once more.
        using var doubled = new FirstDelivery(
            service.Updates,
            static s => s.Positions.Count == 1 && s.Positions[0].Overlay.UnrealizedPnl.Amount == 20_000m);
        pnl.OnNext(new PnlSnapshot
        {
            Positions = [AtEntry with { SignedQuantity = 2m, LastPrice = 100_000m, UnrealizedPnl = new Money(20_000m, "USDT") }],
        });

        await doubled.Task.WaitAsync(CompletionBudget);
        Pulls().Should().Be(pullsSoFar + 1, "the shaped change is the next drain's fresh pull");
    }

    #endregion Tests: the drain loop's behavior

    #region Helpers

    /// <summary>The completion budget: the drain's publication releases the wait; the budget never gates an assertion's timing.</summary>
    private static readonly TimeSpan CompletionBudget = TimeSpan.FromSeconds(30);

    /// <summary>The boot-shaped account (1 BTC long at 90k) the behavior facts push through the feed.</summary>
    private static PnlPositionSnapshot AtEntry { get; } =
        Position("paper", "BTC/USDT", 1m, 90_000m, null, new Money(0m, "USDT"));

    /// <summary>The adapter's open-order pulls so far (the lane's only read the feed performs).</summary>
    private int Pulls() => _trading
        .ReceivedCalls()
        .Count(static c => string.Equals(
            c.GetMethodInfo().Name,
            nameof(ITradingAdapter.GetOpenOrdersAsync),
            StringComparison.Ordinal));

    /// <summary>
    /// Subscribe to one later publication whose predicate holds: the
    /// publication IS the gate, no clock — the wait rides the drain's own
    /// arrival and the standing replay is filtered out by the predicate.
    /// Leak per test is acceptable (a parked state machine reads one
    /// behavior subject).
    /// </summary>
    private sealed class FirstDelivery : IDisposable
    {
        private readonly IDisposable _subscription;
        private readonly TaskCompletionSource<ChartFootprintSnapshot> _waited =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The stream to ride and the predicate the release waits for.</summary>
        public FirstDelivery(
            IObservable<ChartFootprintSnapshot> updates,
            Func<ChartFootprintSnapshot, bool> wanted) =>
            _subscription = updates.Subscribe(
                snapshot =>
                {
                    if (wanted(snapshot))
                    {
                        _waited.TrySetResult(snapshot);
                    }
                },
                _waited.SetException);

        /// <summary>The release task: completes with the matched snapshot.</summary>
        public Task<ChartFootprintSnapshot> Task => _waited.Task;

        public void Dispose() => _subscription.Dispose();
    }

    /// <summary>Take the standing replay synchronously (the subject carries the before-boot state).</summary>
    private static void TakePublished(IObservable<ChartFootprintSnapshot> updates)
    {
        ChartFootprintSnapshot? taken = null;
        using var subscription = updates.Subscribe(snapshot => taken ??= snapshot);
        taken.Should().NotBeNull("the standing snapshot replays on subscribe");
    }

    private ChartFootprintService CreateService(BehaviorSubject<PnlSnapshot> positions) =>
        new(
            _trading,
            _journal,
            new OrderJournal(),
            positions,
            new NotificationCenter(
                Substitute.For<ISettingsService>(),
                NullLogger<NotificationCenter>.Instance),
            NullLogger<ChartFootprintService>.Instance);

    private static PnlPositionSnapshot Position(
        string exchange,
        string symbol,
        decimal quantity,
        decimal entry,
        decimal? lastPrice,
        Money unrealized) => new()
        {
            ExchangeId = exchange,
            Symbol = symbol,
            QuoteCurrency = "USDT",
            SignedQuantity = quantity,
            AverageEntryPrice = entry,
            RealizedPnl = new Money(0m, "USDT"),
            LastPrice = lastPrice,
            UnrealizedPnl = unrealized,
        };

    private static Order Order(
        OrderType type,
        OrderSide side,
        string symbol,
        decimal price,
        string exchange,
        decimal? stopPrice = null) => new()
        {
            Id = $"o-{Guid.NewGuid():N}",
            ExchangeId = exchange,
            Symbol = Symbol.Parse(symbol),
            Side = side,
            Type = type,
            Quantity = new Quantity(1m),
            Price = new Price(price),
            StopPrice = stopPrice is { } stop ? new Price(stop) : null,
            Status = OrderStatus.Submitted,
            TimeInForce = TimeInForce.GTC,
            CreatedAt = AtMinutes(0),
        };

    private static Order Journal(
        OrderStatus status,
        string exchange,
        string symbol,
        decimal price,
        DateTimeOffset at) => new()
        {
            Id = $"j-{Guid.NewGuid():N}",
            ExchangeId = exchange,
            Symbol = Symbol.Parse(symbol),
            Side = OrderSide.Buy,
            Type = OrderType.Limit,
            Quantity = new Quantity(1m),
            FilledQuantity = new Quantity(1m),
            Price = new Price(price),
            Status = status,
            TimeInForce = TimeInForce.GTC,
            CreatedAt = at.AddSeconds(-1),
            UpdatedAt = at,
        };

    private static DateTimeOffset AtMinutes(int minutes) =>
        new DateTimeOffset(2026, 11, 9, 10, 0, 0, TimeSpan.Zero).AddMinutes(minutes);

    #endregion Helpers
}
