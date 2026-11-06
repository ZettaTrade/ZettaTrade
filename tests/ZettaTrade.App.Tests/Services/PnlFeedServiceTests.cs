namespace ZettaTrade.App.Tests.Services;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using FluentAssertions;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using ZettaTrade.App.Services;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.Infrastructure.PnL;

/// <summary>
/// Tests for the PnL feed wrapper: the unrealized side is priced through
/// the active-exchange facade's ticker subscriptions — an emitted ticker
/// must reach the engine's books (the paper ledger's symbol priced by the
/// demo feed), and the observable engine feed stays exposed verbatim.
/// </summary>
public sealed class PnlFeedServiceTests
{
    #region Test Constants

    private static readonly DateTimeOffset BaseTime = new(2026, 1, 10, 12, 0, 0, TimeSpan.Zero);

    #endregion Test Constants

    #region Tests

    [Fact]
    public async Task Feed_TickerPricesReachTheEngine()
    {
        await WithFeedHarnessAsync(async (createEngine, createAdapter) =>
        {
            var engine = createEngine();
            await engine.InitializeAsync();
            await engine.IngestFillAsync(Fill("f1", OrderSide.Buy, "BTC/USDT", quantity: 1m, price: 10m), commission: null);

            // The wrapper prices through the facade (NSubstitute here): one
            // ticker stream serves the paper ledger's held symbol.
            var tickerSource = new TickerStream();
            var adapter = createAdapter(tickerSource);

            var feed = new PnlFeedService(adapter, engine, NullLogger<PnlFeedService>.Instance);
            await feed.InitializeAsync();

            // The wrapper subscribed the engine's replayed snapshot, whose
            // one non-flat account re-arms the symbol's ticker.
            adapter.Received().SubscribeTicker(Symbol.Parse("BTC/USDT"));

            tickerSource.Push(new Ticker(
                Symbol.Parse("BTC/USDT"),
                12m,
                11.9m,
                12.1m,
                100m,
                0.1m,
                DateTimeOffset.UtcNow));

            // The price pump completes off the caller's thread: converge.
            var priced = await WaitForAsync(
                () => PositionOrNull(engine, "BTC/USDT") is { LastPrice: not null } reached ? reached : null,
                "the ticker price must reach the engine's unrealized side");

            priced.LastPrice.Should().Be(12m);
            priced.UnrealizedPnl.Amount.Should().Be(2m);
            priced.RealizedPnl.Amount.Should().Be(0m);

            await feed.DisposeAsync();
            await engine.DisposeAsync();
        });
    }

    [Fact]
    public async Task Feed_ExposesTheEngineStateFeed()
    {
        await WithFeedHarnessAsync(async (createEngine, createAdapter) =>
        {
            var engine = createEngine();
            await engine.InitializeAsync();
            var adapter = createAdapter(null);

            var feed = new PnlFeedService(adapter, engine, NullLogger<PnlFeedService>.Instance);
            feed.Snapshots.Should().BeSameAs(engine.StateChanges);
            feed.Snapshots.Subscribe(_ => { }).Dispose();
            await feed.DisposeAsync();
            await engine.DisposeAsync();
        });
    }

    #endregion Tests

    #region Helpers

    /// <summary>
    /// The test-controlled ticker stream (replays the last price to late
    /// subscribers, like the demo adapter's snapshot-at-subscribe cadence).
    /// </summary>
    private sealed class TickerStream
    {
        private readonly ReplaySubject<Ticker> _subject = new(1);

        public IObservable<Ticker> Stream => _subject;

        public void Push(Ticker ticker) => _subject.OnNext(ticker);
    }

    /// <summary>
    /// Spin up one journal + one engine on temp SQLite files (the
    /// Infrastructure persistence module's canon), plus the NSubstitute
    /// market data adapter the wrapper prices through.
    /// </summary>
    private static async Task WithFeedHarnessAsync(
        Func<Func<PnlAccountingService>, Func<TickerStream?, IMarketDataAdapter>, Task> testBody)
    {
        var journalFile = Path.Combine(
            Path.GetTempPath(),
            $"zettatrade-pnl-feed-journal-{Guid.NewGuid():N}.db");
        var pnlFile = Path.Combine(
            Path.GetTempPath(),
            $"zettatrade-pnl-feed-{Guid.NewGuid():N}.db");
        var journal = new SqliteOrderHistoryStore(
            new DbContextOptionsBuilder<OrderHistoryDbContext>()
                .UseSqlite($"Data Source={journalFile};Pooling=false")
                .Options,
            NullLogger<SqliteOrderHistoryStore>.Instance);

        try
        {
            await journal.InitializeAsync();
            await testBody(
                () => new PnlAccountingService(
                    journal,
                    new DbContextOptionsBuilder<PnlDbContext>()
                        .UseSqlite($"Data Source={pnlFile};Pooling=false")
                        .Options,
                    NullLogger<PnlAccountingService>.Instance),
                source =>
                {
                    var marketData = Substitute.For<IMarketDataAdapter>();
                    if (source is not null)
                    {
                        marketData.SubscribeTicker(Symbol.Parse("BTC/USDT")).Returns(source.Stream);
                    }

                    return marketData;
                });
        }
        finally
        {
            foreach (var file in new[] { journalFile, pnlFile })
            {
                File.Delete(file);
            }
        }
    }

    private static Order Fill(
        string id,
        OrderSide side,
        string symbol,
        decimal quantity,
        decimal price) => new()
        {
            Id = id,
            ExchangeId = "paper",
            Symbol = Symbol.Parse(symbol),
            Side = side,
            Type = OrderType.Market,
            Quantity = new Quantity(quantity),
            Price = new Price(price),
            Status = OrderStatus.Filled,
            TimeInForce = TimeInForce.GTC,
            CreatedAt = BaseTime,
            FilledQuantity = new Quantity(quantity),
        };

    private static PnlPositionSnapshot? PositionOrNull(PnlAccountingService engine, string symbol)
    {
        PnlSnapshot? current = null;
        using var collector = engine.StateChanges.Subscribe(snapshot => current = snapshot);
        return current?.Positions.FirstOrDefault(p => p.Symbol == symbol);
    }

    /// <summary>
    /// A bounded convergence wait for async observable plumbing: the
    /// wrapper's price pump completes off the caller's thread, so the
    /// arrival is polled at a fixed slack instead of assumed.
    /// </summary>
    private static async Task<TIn> WaitForAsync<TIn>(Func<TIn?> probe, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var found = probe();
            if (found is not null)
            {
                return found;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25));
        }

        throw new TimeoutException($"{what} did not converge in time");
    }

    #endregion Helpers
}
