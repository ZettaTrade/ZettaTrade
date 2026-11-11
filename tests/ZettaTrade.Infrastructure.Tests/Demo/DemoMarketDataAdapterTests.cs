namespace ZettaTrade.Infrastructure.Tests.Demo;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.Infrastructure.Demo;

/// <summary>
/// Tests for the canned-data demo adapter: a closed symbol universe, a
/// deterministic bar walk for the same request, strict rejection of unknown
/// symbols (never silent defaults) and cadenced stream snapshots. No network
/// is involved anywhere.
/// </summary>
public sealed class DemoMarketDataAdapterTests
{
    private readonly DemoMarketDataAdapter _adapter = new(NullLogger<DemoMarketDataAdapter>.Instance);

    private static readonly DateTimeOffset M1Anchor =
        DateTimeOffset.FromUnixTimeSeconds(1730000040); // already on the M1 boundary

    /// <summary>The anchor floored onto the M1 boundary the tests expect.</summary>
    private static DateTimeOffset AnchorOpen() => M1Anchor;

    /// <summary>An H1-boundary anchor: a 7 day window back from it holds 169
    /// candles — the full chart range with headroom under the limit.</summary>
    private static readonly DateTimeOffset H1Anchor =
        DateTimeOffset.FromUnixTimeSeconds(1729998000);

    /// <summary>Bounded wait for a two-tick trade feed (subscribe snapshot + one 2 s cadence tick).</summary>
    private static readonly TimeSpan TradeFeedWait = TimeSpan.FromSeconds(5);

    /// <summary>The price factor a Sell trade applies to its symbol's anchor (even sequence).</summary>
    private const decimal SellPriceFactor = 0.9999m;

    /// <summary>The price factor a Buy trade applies to its symbol's anchor (odd sequence).</summary>
    private const decimal BuyPriceFactor = 1.0001m;

    /// <summary>The documented anchor price per normalized symbol (the seeded walk's base).</summary>
    private static readonly IReadOnlyDictionary<string, decimal> AnchorPrices =
        new Dictionary<string, decimal>
        {
            ["BTC/USDT"] = 67000m,
            ["ETH/USDT"] = 3500m,
            ["SOL/USDT"] = 170m,
            ["BTC/USDC"] = 67110m,
            ["XRP/USDT"] = 0.62m,
            ["ETH/BTC"] = 0.0522m,
            ["ADA/USDT"] = 0.45m,
            ["DOGE/USDT"] = 0.12m,
            ["BNB/USDT"] = 580m,
            ["LINK/USDT"] = 14m,
            ["AVAX/USDT"] = 28m,
            ["LTC/USDT"] = 66m,
        };

    private static Symbol BtcUsdt() => Symbol.Parse("BTC/USDT");

    [Fact]
    public async Task GetSymbolsAsync_ServesTheFixedBinanceSubset()
    {
        var symbols = await _adapter.GetSymbolsAsync();

        symbols.Select(symbol => symbol.Normalized).Should().BeEquivalentTo(
        [
            "BTC/USDT",
            "ETH/USDT",
            "SOL/USDT",
            "BTC/USDC",
            "XRP/USDT",
            "ETH/BTC",
            "ADA/USDT",
            "DOGE/USDT",
            "BNB/USDT",
            "LINK/USDT",
            "AVAX/USDT",
            "LTC/USDT",
        ]);
    }

    [Fact]
    public async Task GetSymbolsAsync_TheAnchorTable_CoversExactlyTheCatalog()
    {
        var symbols = await _adapter.GetSymbolsAsync();

        AnchorPrices.Keys.Should().BeEquivalentTo(
            symbols.Select(symbol => symbol.Normalized),
            "every tradable symbol seeds its walk from a documented anchor — and no anchor orphans a retired symbol");
    }

    [Fact]
    public async Task GetBarsAsync_SameRequest_ReproducesIdenticalBars()
    {
        var first = await _adapter.GetBarsAsync(BtcUsdt(), TimeFrame.M1, M1Anchor, M1Anchor, 5);
        var second = await _adapter.GetBarsAsync(BtcUsdt(), TimeFrame.M1, M1Anchor, M1Anchor, 5);

        second.Should().BeEquivalentTo(first, "the seeded walk is deterministic for the same request");
    }

    [Fact]
    public async Task GetBarsAsync_DifferentSalt_YieldsADifferentWalk()
    {
        var m1 = await _adapter.GetBarsAsync(BtcUsdt(), TimeFrame.M1, M1Anchor, M1Anchor, 5);
        var m5 = await _adapter.GetBarsAsync(BtcUsdt(), TimeFrame.M5, M1Anchor, M1Anchor, 5);

        m5.Should().NotBeEquivalentTo(m1, "the time frame is part of the seed");
    }

    [Fact]
    public async Task GetBarsAsync_RespectsTheRangeAndLimitWithSaneCandles()
    {
        var bars = await _adapter.GetBarsAsync(
            BtcUsdt(),
            TimeFrame.M1,
            from: AnchorOpen().AddMinutes(-9),
            to: AnchorOpen(),
            limit: 500);

        bars.Should().HaveCount(10, "ten candles fit between from and the anchor at M1");
        bars.Select(bar => bar.Time).Should().BeInAscendingOrder();
        bars[^1].Time.Should().Be(AnchorOpen(), "the anchor is a candle opening time itself");
        bars.Select(bar => bar.Time)
            .Should().OnlyHaveUniqueItems("one candle per time frame bucket");
        bars.Should().OnlyContain(bar =>
            bar.High >= Math.Max(bar.Open, bar.Close)
            && bar.Low <= Math.Min(bar.Open, bar.Close));
    }

    [Fact]
    public async Task GetBarsAsync_FullChartRange_EverySymbolStaysWithinTheDocumentedWalk()
    {
        var symbols = await _adapter.GetSymbolsAsync();

        foreach (var symbol in symbols)
        {
            var bars = await _adapter.GetBarsAsync(
                symbol,
                TimeFrame.H1,
                from: H1Anchor.AddDays(-7),
                to: H1Anchor,
                limit: 200);

            bars.Should().HaveCount(169, $"{symbol.Normalized} covers a full 7 day range at H1");
            bars[^1].Time.Should().Be(H1Anchor);

            // Seeded steps live in (−1%, +1%), so by induction every close
            // stays inside the anchor price compounded over bounded steps.
            // The unsigned-wrap regression (T-024) produced ~+42 949% steps
            // and the walk exploded beyond the decimal range instead.
            var anchorPrice = bars[0].Open;
            var upperBound = anchorPrice * Power(1.01m, bars.Count);
            var lowerBound = anchorPrice * Power(0.99m, bars.Count);

            bars.Should().OnlyContain(bar =>
                bar.Close >= lowerBound && bar.Close <= upperBound,
                $"each close of {symbol.Normalized} compounds the documented (−1%, +1%) steps");
        }
    }

    [Fact]
    public async Task GetBarsAsync_UnknownSymbol_ThrowsInsteadOfDefaulting()
    {
        var act = async () => await _adapter.GetBarsAsync(
            Symbol.Parse("PEPE/USDT"),
            TimeFrame.M1,
            M1Anchor,
            M1Anchor,
            5);

        await act.Should().ThrowAsync<ArgumentException>("unknown symbols are an error, not a default");
    }

    [Fact]
    public void SubscribeOrderBook_UnknownSymbol_ThrowsSynchronously()
    {
        var act = () => _adapter.SubscribeOrderBook(Symbol.Parse("PEPE/USDT"));

        act.Should().Throw<ArgumentException>("unknown symbols are an error, not a default");
    }

    [Fact]
    public async Task SubscribeOrderBook_EmitsASaneBookSnapshotImmediately()
    {
        var first = await FirstWithin(_adapter.SubscribeOrderBook(BtcUsdt()), TimeSpan.FromSeconds(2));

        first.Symbol.Should().Be(BtcUsdt());
        first.BestBid.Should().NotBeNull();
        first.BestAsk.Should().NotBeNull();
        first.BestBid!.Value.Price.Should().BeLessThan(first.BestAsk!.Value.Price);
        first.Bids.Should().OnlyContain(level => level.Price > 0);
    }

    [Fact]
    public async Task SubscribeOrderBook_TheStampIsTheLocalReceiveMoment()
    {
        var before = DateTimeOffset.UtcNow;

        var first = await FirstWithin(_adapter.SubscribeOrderBook(BtcUsdt()), TimeSpan.FromSeconds(2));

        first.Timestamp.Should().BeOnOrAfter(
            before,
            "the stamp is the ingestion moment, never an earlier read");
        first.Timestamp.Should().BeCloseTo(
            DateTimeOffset.UtcNow,
            TimeSpan.FromSeconds(5),
            "T-142's clock discipline: the canned surface stamps now, not an exchange clock");
    }

    [Fact]
    public async Task SubscribeTicker_EmitsASnapshotImmediately()
    {
        var first = await FirstWithin(_adapter.SubscribeTicker(BtcUsdt()), TimeSpan.FromSeconds(2));

        first.Symbol.Should().Be(BtcUsdt());
        first.Bid.Should().BeLessThan(first.Ask, "the canned spread surrounds the last price");
    }

    [Fact]
    public async Task SubscribeTicker_24hChange_StaysInTheDocumentedBand()
    {
        var symbols = await _adapter.GetSymbolsAsync();

        foreach (var symbol in symbols)
        {
            var first = await FirstWithin(_adapter.SubscribeTicker(symbol), TimeSpan.FromSeconds(2));

            first.PriceChange24hPercent.Should().BeInRange(
                -1m,
                1m,
                $"{symbol.Normalized} documents a 24 h move in (−1%, +1%)");
        }
    }

    /// <summary>
    /// All twelve documented symbol cadences fire concurrently on pool
    /// threads: every trade carries a GLOBALLY unique sequence number (the
    /// atomic increment, BUG-031) and the side/price parity it encodes stays
    /// consistent — a single lost increment would hand one sequence number to
    /// two trades and flip the parity of the trade that shared it.
    /// </summary>
    [Fact]
    public async Task SubscribeTrades_ConcurrentCadences_KeepEveryTradeSequenceGloballyUnique()
    {
        var symbols = await _adapter.GetSymbolsAsync();

        var feedsTask = Task.WhenAll(symbols
            .Select(symbol => _adapter
                .SubscribeTrades(symbol)
                .Take(2)
                .ToList()
                .ToTask(TaskCreationOptions.RunContinuationsAsynchronously)
                .WaitAsync(TradeFeedWait))
            .ToArray());

        var feeds = await feedsTask;

        var trades = feeds.SelectMany(feed => feed).ToList();
        trades.Should().HaveCount(symbols.Count * 2, "every feed yields the subscribe snapshot plus one cadence tick");

        var sequences = trades.Select(ParseSequence).ToList();
        sequences.Should().OnlyHaveUniqueItems(
            "the globally atomic sequence source never hands one number to two trades");

        foreach (var trade in trades)
        {
            var sequence = ParseSequence(trade);
            var anchor = AnchorPriceOf(trade.Symbol.Normalized);
            var isSell = sequence % 2 == 0;

            trade.Side.Should().Be(isSell ? OrderSide.Sell : OrderSide.Buy,
                $"trade '{trade.Id}' encodes its sequence parity in the side");
            trade.Price.Value.Should().Be(isSell ? anchor * SellPriceFactor : anchor * BuyPriceFactor,
                $"trade '{trade.Id}' encodes its sequence parity in the price factor");
        }
    }

    [Fact]
    public void Ctor_IsConnectedImmediately()
    {
        _adapter.IsConnected.Should().BeTrue("the canned surface is always up");
    }

    [Fact]
    public async Task ConnectDisconnect_CyclesTheFlag()
    {
        await _adapter.DisconnectAsync();
        _adapter.IsConnected.Should().BeFalse();

        await _adapter.ConnectAsync();
        _adapter.IsConnected.Should().BeTrue();
    }

    /// <summary>
    /// Receive the first emission within the bounded wait: stream snapshots
    /// are asserted to arrive promptly instead of pinning an exact cadence.
    /// </summary>
    private static async Task<TSnapshot> FirstWithin<TSnapshot>(IObservable<TSnapshot> source, TimeSpan wait)
    {
        var received = new TaskCompletionSource<TSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = source.Subscribe(received.SetResult);

        var winner = await Task.WhenAny(received.Task, Task.Delay(wait));
        winner.Should().Be(received.Task, $"the first snapshot must arrive within {wait}");
        return await received.Task;
    }

    /// <summary>Whole-power helper for the documented-walk compounding bounds.</summary>
    private static decimal Power(decimal baseValue, int exponent)
    {
        var result = 1m;
        for (var i = 0; i < exponent; i++)
        {
            result *= baseValue;
        }

        return result;
    }

    /// <summary>The documented anchor of a symbol (an unsupported symbol fails the lookup honestly).</summary>
    private static decimal AnchorPriceOf(string normalized) => AnchorPrices[normalized];

    /// <summary>The trade's sequence number: the id tail after the last separator.</summary>
    private static uint ParseSequence(MarketTrade trade)
    {
        var separatorIndex = trade.Id.LastIndexOf('-');
        return uint.Parse(trade.Id[(separatorIndex + 1)..], CultureInfo.InvariantCulture);
    }
}
