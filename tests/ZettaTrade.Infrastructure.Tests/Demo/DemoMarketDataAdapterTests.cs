namespace ZettaTrade.Infrastructure.Tests.Demo;

using System;
using System.Linq;
using System.Reactive.Linq;
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
        ]);
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
            Symbol.Parse("DOGE/USDT"),
            TimeFrame.M1,
            M1Anchor,
            M1Anchor,
            5);

        await act.Should().ThrowAsync<ArgumentException>("unknown symbols are an error, not a default");
    }

    [Fact]
    public void SubscribeOrderBook_UnknownSymbol_ThrowsSynchronously()
    {
        var act = () => _adapter.SubscribeOrderBook(Symbol.Parse("DOGE/USDT"));

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
}
