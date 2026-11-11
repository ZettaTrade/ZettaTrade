namespace ZettaTrade.App.Tests.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Subjects;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using ZettaTrade.App.Services;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.Infrastructure.Demo;

/// <summary>
/// The watchlist feed facts (T-120, over the pulse seam): the quotes hold
/// until the first pulse beats (which always ships the starter row set,
/// unpriced rows carrying null fields), a tick followed by a pulse lands
/// the row's values, the row's price ring caps at its capacity keeping the
/// newest prices, and a selection join retires the previous selection-only
/// row once it leaves.
/// </summary>
public sealed class WatchlistFeedServiceTests
{
    /// <summary>The starter row order the panel claims (mirrored, not read).</summary>
    private static readonly string[] StarterTexts =
    [
        "BTC/USDT",
        "ETH/USDT",
        "SOL/USDT",
        "XRP/USDT",
        "ADA/USDT",
        "DOGE/USDT",
        "BNB/USDT",
        "LINK/USDT",
        "AVAX/USDT",
        "LTC/USDT",
    ];

    private const decimal RingCapacity = 32;

    #region Fixtures

    /// <summary>One feed over a substituted adapter, subject streams and the controllable pulse.</summary>
    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Feeds = new Dictionary<string, Subject<Ticker>>(StringComparer.Ordinal);
            Adapter = Substitute.For<IMarketDataAdapter>();
            Adapter
                .SubscribeTicker(Arg.Any<Symbol>())
                .Returns(callInfo => LaneFor(Feeds, callInfo.Arg<Symbol>().Normalized));
            Selections = new Subject<Symbol>();
            Pulse = new Subject<long>();
            Feed = new WatchlistFeedService(
                Adapter,
                Selections,
                NullLogger<WatchlistFeedService>.Instance,
                Pulse);
        }

        public IMarketDataAdapter Adapter { get; }

        public Dictionary<string, Subject<Ticker>> Feeds { get; }

        public Subject<Symbol> Selections { get; }

        public Subject<long> Pulse { get; }

        public WatchlistFeedService Feed { get; }

        /// <summary>Push one ticker into the row's lane.</summary>
        public void Tick(string symbolText, decimal price, decimal changePercent, decimal volume24h, decimal bid, decimal ask)
        {
            LaneFor(Feeds, symbolText).OnNext(new Ticker(
                Symbol.Parse(symbolText),
                price,
                bid,
                ask,
                volume24h,
                changePercent,
                DateTimeOffset.UtcNow));
        }

        /// <summary>Take one pulse-coalesced snapshot (subscriptions are synchronous).</summary>
        public IReadOnlyList<WatchlistQuote> SnapshotAfterPulse()
        {
            IReadOnlyList<WatchlistQuote> latest = Array.Empty<WatchlistQuote>();
            using (Feed.Quotes.Subscribe(snapshot => latest = snapshot))
            {
                Pulse.OnNext(0L);
            }

            return latest;
        }

        public void Dispose()
        {
            Feed.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        /// <summary>The lane cache behind the substituted SubscribeTicker.</summary>
        private static Subject<Ticker> LaneFor(Dictionary<string, Subject<Ticker>> feeds, string normalized)
        {
            if (!feeds.TryGetValue(normalized, out var lane))
            {
                lane = new Subject<Ticker>();
                feeds.Add(normalized, lane);
            }

            return lane;
        }
    }

    #endregion

    #region Feed Facts

    [Fact]
    public void TheFeed_HoldsUntilTheFirstPulse_ThenShipsTheStarterRowSetUnpriced()
    {
        using var fixture = new Fixture();
        var seen = new List<IReadOnlyList<WatchlistQuote>>();

        using (fixture.Feed.Quotes.Subscribe(seen.Add))
        {
            seen.Should().BeEmpty("nothing ships before the first pulse beat");
            fixture.Pulse.OnNext(0L);
            // A pulse without fresh data tick re-ships nothing (Sample clears
            // its buffer too): one snapshot per set of actually-new rows.
            fixture.Pulse.OnNext(1L);
        }

        seen.Should().ContainSingle();
        var snapshot = seen[0];
        snapshot.Should().HaveCount(StarterTexts.Length);
        snapshot.Select(row => row.SymbolText).Should().Equal(StarterTexts);
        foreach (var row in snapshot)
        {
            row.LastPrice.Should().BeNull("a row that has not ticked yet carries null fields");
            row.Change24hPercent.Should().BeNull();
            row.Volume24h.Should().BeNull();
            row.Sparkline.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task TheDemoCatalog_CoversEveryStarterRow()
    {
        // The default first-run exchange is the demo adapter (T-166): every
        // starter row must price there, or the row throws inside the ticker
        // projection at every startup and stays unpriced forever.
        var adapter = new DemoMarketDataAdapter(NullLogger<DemoMarketDataAdapter>.Instance);
        var symbols = await adapter.GetSymbolsAsync();

        StarterTexts.Should().OnlyContain(
            starter => symbols.Any(symbol => symbol.Normalized == starter),
            "the demo catalog is the default first-run exchange's tradable universe");
    }

    [Fact]
    public void ATickFollowedByAPulse_LandsTheRowsNewValues()
    {
        using var fixture = new Fixture();

        fixture.Tick("BTC/USDT", 65432.10m, 1.25m, 1_520_000m, 65400m, 65450m);

        var snapshot = fixture.SnapshotAfterPulse();
        var btc = snapshot.Should().Contain(row => row.SymbolText == "BTC/USDT").Which;
        btc.LastPrice.Should().Be(65432.10m);
        btc.Change24hPercent.Should().Be(1.25m);
        btc.Volume24h.Should().Be(1_520_000m);
        btc.Sparkline.Should().Equal(new decimal[] { 65432.10m });
    }

    [Fact]
    public void ThePriceRing_CapsAtItsCapacity_KeepingTheNewestPrices()
    {
        using var fixture = new Fixture();

        for (var index = 1; index <= 34; index++)
        {
            fixture.Tick("ETH/USDT", index, 0m, 1m, 1m, 1m);
        }

        var eth = fixture.SnapshotAfterPulse()
            .Should().Contain(row => row.SymbolText == "ETH/USDT").Which;

        eth.Sparkline.Should().HaveCount((int)RingCapacity);
        eth.Sparkline[0].Should().Be(3m, "34 ticks over a 32-slot ring evict the two oldest prices");
        eth.Sparkline[^1].Should().Be(34m);
    }

    [Fact]
    public void ASelectionJoinsItsRow_AndTheNextSelectionRetiresIt()
    {
        using var fixture = new Fixture();

        fixture.Selections.OnNext(Symbol.Parse("TRX/USDT"));

        var joined = fixture.SnapshotAfterPulse();
        joined.Should().HaveCount(StarterTexts.Length + 1);
        joined.Select(row => row.SymbolText).Should().Contain("TRX/USDT").And.HaveCount(StarterTexts.Length + 1);

        fixture.Selections.OnNext(Symbol.Parse("ETH/USDT"));

        var after = fixture.SnapshotAfterPulse();
        after.Select(row => row.SymbolText).Should().Equal(StarterTexts);
    }

    #endregion
}
