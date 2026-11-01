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
/// </summary>
public sealed class ExchangeAdapterBaseSubscriptionTests
{
    #region Test Adapter

    /// <summary>
    /// Minimal concrete adapter for the base contract tests; the stream start
    /// is a pending wait whose invocations and cancellations are recorded.
    /// </summary>
    private sealed class TestAdapter : ExchangeAdapterBase
    {
        private readonly List<Symbol> _calledStarts = new();
        private readonly List<Symbol> _cancelledStarts = new();
        private readonly bool _blockStart;
        private CancellationToken? _observedStartToken;

        public TestAdapter(bool blockStart)
            : base(NullLogger.Instance)
        {
            _blockStart = blockStart;
        }

        public IReadOnlyList<Symbol> CalledStarts => _calledStarts;

        public IReadOnlyList<Symbol> CancelledStarts => _cancelledStarts;

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

        protected override async Task StartSymbolStreamsAsync(Symbol symbol, CancellationToken ct)
        {
            _observedStartToken = ct;
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

    #endregion
}
