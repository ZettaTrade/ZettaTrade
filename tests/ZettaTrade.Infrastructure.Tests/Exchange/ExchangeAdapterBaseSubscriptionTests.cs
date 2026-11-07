namespace ZettaTrade.Infrastructure.Tests.Exchange;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Core.Entities;
using Core.Enums;
using Core.ValueObjects;
using FluentAssertions;
using Infrastructure.Exchange;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Contract tests for the lazy stream start in <see cref="ExchangeAdapterBase"/>:
/// the first feed subscriber starts the adapter-side streams, an in-flight
/// start is cancelled together with the subscription, and every new outer
/// subscription re-runs the idempotent start while the per-symbol subject
/// stays shared.
///
/// Wait budget (T-116, 2026-11-07): <c>EventWait</c> is the 60 s house
/// completion budget shared by the WS E2E family, and the starvation
/// signature reproduced right here: during the T-075/T-077 landing wave a
/// full-solution parallel test run failed on a poll deadline (5 s inline at
/// the time) that a scheduler stall across sibling test hosts chewed past,
/// while every focused run and the gate's flaky retry stayed green — the
/// adapter pipeline was healthy all along. Load patience, never a product
/// timing claim: solo runs always passed and still must, and deliberate
/// latency ceilings stay separate from this budget.
/// </summary>
public sealed class ExchangeAdapterBaseSubscriptionTests
{
    #region Constants

    /// <summary>
    /// The 60 s house load budget (T-116, see the class header) for polls on
    /// cross-thread settlement — completion patience, never a latency ceiling.
    /// </summary>
    private static readonly TimeSpan EventWait = TimeSpan.FromSeconds(60);

    #endregion

    #region Test Adapter

    /// <summary>
    /// Minimal concrete adapter for the base contract tests; the stream start
    /// is a pending wait whose invocations, cancellations and requested book
    /// depths are recorded.
    /// </summary>
    private sealed class TestAdapter : ExchangeAdapterBase
    {
        private readonly List<Symbol> _calledStarts = new();
        private readonly List<Symbol> _cancelledStarts = new();
        private readonly List<int?> _requestedDepths = new();
        private readonly bool _blockStart;
        private CancellationToken? _observedStartToken;

        public TestAdapter(bool blockStart, IReadOnlySet<int>? bookDepths = null)
            : base(NullLogger.Instance)
        {
            _blockStart = blockStart;
            supportedBookDepths = bookDepths ?? new HashSet<int> { 20 };
        }

        /// <inheritdoc/>
        protected override IReadOnlySet<int> SupportedBookDepths => supportedBookDepths;

        private readonly IReadOnlySet<int> supportedBookDepths;

        public IReadOnlyList<Symbol> CalledStarts => _calledStarts;

        public IReadOnlyList<Symbol> CancelledStarts => _cancelledStarts;

        /// <summary>The depth each start was given, in call order (null for ticker/trade starts).</summary>
        public IReadOnlyList<int?> RequestedDepths => _requestedDepths;

        /// <summary>The cancellation token the last start received.</summary>
        public CancellationToken? ObservedStartToken => _observedStartToken;

        public override string Id => "test";

        public override string Name => "Test";

        public override ExchangeCapabilities Capabilities => ExchangeCapabilities.MarketData;

        public override Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public override Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public override Task<IReadOnlyList<Symbol>> GetSymbolsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Symbol>>(Array.Empty<Symbol>());

        public override Task<IReadOnlyList<Bar>> GetBarsAsync(
            Symbol symbol,
            TimeFrame timeFrame,
            DateTimeOffset from,
            DateTimeOffset? to = null,
            int limit = 500,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Bar>>(Array.Empty<Bar>());

        protected override async Task StartSymbolStreamsAsync(
            Symbol symbol,
            int? bookDepth,
            CancellationToken ct)
        {
            _observedStartToken = ct;
            _requestedDepths.Add(bookDepth);
            _calledStarts.Add(symbol);

            if (!_blockStart)
            {
                // Immediate start: the wrapper switches onto the shared feed,
                // so test publishes are delivered synchronously.
                return;
            }

            try
            {
                // A blocking start that only the wrapper's cancellation can
                // end: proves an in-flight start is dropped when the
                // subscription leaves.
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                _cancelledStarts.Add(symbol);
            }
        }

        /// <summary>Publish into the book feed (the protected base surface).</summary>
        public void Publish(OrderBook book) => PublishOrderBook(book);
    }

    private static OrderBook Book(Symbol symbol, decimal bestPrice) =>
        new()
        {
            Symbol = symbol,
            Bids = [new PriceLevel(bestPrice, 1)],
            Asks = [new PriceLevel(bestPrice + 1, 1)],
            Timestamp = DateTimeOffset.UtcNow,
        };

    #endregion

    #region Tests

    [Fact]
    public void FirstSubscription_StartsTheAdapterStreams()
    {
        var adapter = new TestAdapter(blockStart: false);
        var symbol = Symbol.Parse("BTC/USDT");

        using var subscription = adapter.SubscribeOrderBook(symbol).Subscribe(_ => { });

        adapter.CalledStarts.Should().ContainSingle().Which.Should().Be(symbol);
    }

    [Fact]
    public void EveryNewSubscription_RerunsTheIdempotentStart()
    {
        var adapter = new TestAdapter(blockStart: true);
        var symbol = Symbol.Parse("BTC/USDT");

        using (adapter.SubscribeOrderBook(symbol).Subscribe(_ => { }))
        {
        }

        using var second = adapter.SubscribeOrderBook(symbol).Subscribe(_ => { });

        adapter.CalledStarts.Should().HaveCount(2, "per-subscription starts document the contract");
        adapter.CancelledStarts.Should().Contain(symbol, "the in-flight start of the first subscription was cancelled with its dispose");
    }

    [Fact]
    public void DataFlowsThroughTheSharedSubject_RegardlessOfTheStart()
    {
        var adapter = new TestAdapter(blockStart: false);
        var symbol = Symbol.Parse("BTC/USDT");
        var first = new List<OrderBook>();
        var second = new List<OrderBook>();

        using var outerOne = adapter.SubscribeOrderBook(symbol).Subscribe(first.Add);
        adapter.Publish(Book(symbol, 100));

        using var outerTwo = adapter.SubscribeOrderBook(symbol).Subscribe(second.Add);
        adapter.Publish(Book(symbol, 200));

        first.Should().ContainSingle(b => b.Bids[0].Price == 100m)
            .And.ContainSingle(b => b.Bids[0].Price == 200m);
        second.Should().ContainSingle(b => b.Bids[0].Price == 200m, "the subject stays shared across start reruns");
    }

    [Fact]
    public void SymbolKey_IsolatesTheFeeds()
    {
        var adapter = new TestAdapter(blockStart: false);
        var bitcoin = Symbol.Parse("BTC/USDT");
        var ethereum = Symbol.Parse("ETH/BTC");
        var bitcoinBooks = new List<OrderBook>();

        using var subscription = adapter.SubscribeOrderBook(bitcoin).Subscribe(bitcoinBooks.Add);
        adapter.Publish(Book(ethereum, 3000));

        bitcoinBooks.Should().BeEmpty("a publish for another symbol never reaches this feed");
    }

    [Fact]
    public void StartToken_IsLiveWhileTheStartIsInFlight()
    {
        var adapter = new TestAdapter(blockStart: true);
        var symbol = Symbol.Parse("BTC/USDT");

        using var subscription = adapter.SubscribeOrderBook(symbol).Subscribe(_ => { });

        adapter.ObservedStartToken.Should().NotBeNull();
        adapter.ObservedStartToken!.Value.IsCancellationRequested.Should().BeFalse(
            "the start token is live for the whole in-flight start");

        subscription.Dispose();
    }

    [Fact]
    public void SupportedDepth_RidesTheStreamStart()
    {
        var adapter = new TestAdapter(
            blockStart: false,
            bookDepths: new HashSet<int> { 5, 10, 20 });
        var symbol = Symbol.Parse("BTC/USDT");

        using var subscription = adapter.SubscribeOrderBook(symbol, 5).Subscribe(_ => { });

        adapter.RequestedDepths.Should().Equal(new int?[] { 5 });
    }

    [Fact]
    public void TickerAndTradeStarts_CarryNoBookDepth()
    {
        var adapter = new TestAdapter(
            blockStart: false,
            bookDepths: new HashSet<int> { 5, 10, 20 });
        var symbol = Symbol.Parse("BTC/USDT");

        using (adapter.SubscribeTicker(symbol).Subscribe(_ => { }))
        using (adapter.SubscribeTrades(symbol).Subscribe(_ => { }))
        {
        }

        adapter.RequestedDepths.Should().Equal(new int?[] { null, null },
            "a ticker or trade start carries no depth request at all");
    }

    [Fact]
    public async Task RejectedDepth_ErrorsOnEverySubscribeWithoutAStart()
    {
        var adapter = new TestAdapter(
            blockStart: false,
            bookDepths: new HashSet<int> { 5, 10, 20 });
        var symbol = Symbol.Parse("BTC/USDT");
        var source = adapter.SubscribeOrderBook(symbol, 7);

        var failures = new List<Exception>();
        using (source.Subscribe(_ => { }, failures.Add))
        {
            await TestPoll.PollUntilAsync(() => failures.Count >= 1, EventWait);
        }

        // The rejection is handed out per subscription, not a one-shot state.
        using (source.Subscribe(_ => { }, failures.Add))
        {
            await TestPoll.PollUntilAsync(() => failures.Count >= 2, EventWait);
        }

        failures.Should().HaveCount(2);
        failures.Should().OnlyContain(failure => failure is ArgumentOutOfRangeException);
        failures[0].Message.Should().Contain("5, 10, 20").And.Contain("requested 7");
        adapter.CalledStarts.Should()
            .BeEmpty("a rejected depth request never starts any stream");
    }

    #endregion
}
