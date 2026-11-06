namespace ZettaTrade.App.Tests.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using NSubstitute;
using Xunit;
using ZettaTrade.App.Services;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// Tests for the dialog-side market reference rate: the newest H1 close must
/// be selected BY ITS TIME whatever page order a backend serves (ascending
/// Binance-style or descending Bybit/demo-style), and an empty history is a
/// loud failure — an order-building flow must never guess its anchor.
/// </summary>
public sealed class MarketReferenceTests
{
    private static readonly DateTimeOffset BaseBarTime =
        new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    private static IReadOnlyList<Bar> History() =>
    [
        new(BaseBarTime, 100m, 100m, 100m, 100m, 10m),
        new(BaseBarTime.AddHours(1), 100m, 102m, 100m, 102m, 11m),
        new(BaseBarTime.AddHours(2), 101m, 101m, 99m, 101m, 12m),
    ];

    /// <summary>
    /// A paging-faithful backend: serves the bars in the given page order,
    /// capped at the caller's limit — the shape the real adapters page in.
    /// </summary>
    private static IMarketDataAdapter CreateBackend(IReadOnlyList<Bar> bars, bool newestFirst)
    {
        var marketData = Substitute.For<IMarketDataAdapter>();
        marketData.GetBarsAsync(
                Arg.Any<Symbol>(),
                Arg.Any<TimeFrame>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var limit = call.ArgAt<int>(4);
                var paged = bars.Take(limit);
                return (IReadOnlyList<Bar>)(newestFirst
                    ? paged.OrderByDescending(bar => bar.Time).ToList()
                    : paged.OrderBy(bar => bar.Time).ToList());
            });

        return marketData;
    }

    [Fact]
    public async Task GetLatestCloseAsync_AscendingPagedBackend_PicksTheNewestBarByTime()
    {
        var marketData = CreateBackend(History(), newestFirst: false);

        var close = await MarketReference.GetLatestCloseAsync(
            marketData,
            Symbol.Parse("BTC/USDT"),
            CancellationToken.None);

        close.Value.Should().Be(
            101m,
            "the newest bar by time closes at 101 whatever the ascending paging");
    }

    [Fact]
    public async Task GetLatestCloseAsync_DescendingPagedBackend_PicksTheNewestBarByTime()
    {
        var marketData = CreateBackend(History(), newestFirst: true);

        var close = await MarketReference.GetLatestCloseAsync(
            marketData,
            Symbol.Parse("BTC/USDT"),
            CancellationToken.None);

        close.Value.Should().Be(101m);
    }

    [Fact]
    public async Task GetLatestCloseAsync_EmptyHistory_FailsLoudly()
    {
        var marketData = CreateBackend([], newestFirst: false);

        var act = async () => await MarketReference.GetLatestCloseAsync(
            marketData,
            Symbol.Parse("BTC/USDT"),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*No reference bar available*");
    }

    [Fact]
    public void GetLatestCloseAsync_NullAdapter_Throws()
    {
        var act = () => MarketReference.GetLatestCloseAsync(
            null!,
            Symbol.Parse("BTC/USDT"),
            CancellationToken.None);
        act.Should().ThrowAsync<ArgumentNullException>();
    }
}
