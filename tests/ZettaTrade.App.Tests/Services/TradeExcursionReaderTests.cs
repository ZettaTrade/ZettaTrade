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
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.Infrastructure.Persistence;

/// <summary>
/// Tests for the MAE/MFE derivation lane (T-251): the empty batch that
/// never touches the cache, the candidate fall-through whose winning
/// series still keys the facts by the TRADE'S venue (the join key half is
/// the journal's, never the cache's), the symbol groups that read their
/// series separately and mix nothing, the trade whose window the series
/// does not cover producing no fact at all, and the walk-back that stops
/// once the window's start is covered — every cache read bounded.
/// </summary>
public sealed class TradeExcursionReaderTests
{
    #region Test Constants

    private static readonly DateTimeOffset BaseTime = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    #endregion Test Constants

    #region Helpers

    private readonly ICandleCache _cache = Substitute.For<ICandleCache>();

    private TradeExcursionReader CreateReader() => new(_cache);

    private static Bar Bar(DateTimeOffset time, decimal high, decimal low) =>
        new(time, low, high, low, high, 1m);

    private static TradeStatsTradeFact Trade(
        string exchangeId,
        string orderId,
        string symbol,
        DateTimeOffset openedAt,
        DateTimeOffset closedAt,
        decimal entryPrice,
        bool isLong = true) => new(
        ClosedAt: closedAt,
        OpenedAt: openedAt,
        ExchangeId: exchangeId,
        Symbol: symbol,
        ClosingOrderId: orderId,
        IsLong: isLong,
        EntryPrice: entryPrice,
        RMultiple: null);

    #endregion Helpers

    [Fact]
    public async Task EmptyBatch_NeverTouchesTheCache()
    {
        var reader = CreateReader();

        var derived = await reader.DeriveAsync([], ["demo"], CancellationToken.None);

        derived.Should().BeEmpty();
        _cache.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task NoCandidateHoldsBars_NoFacts()
    {
        _cache.GetOlderThanAsync(
                Arg.Any<string>(),
                Arg.Any<Symbol>(),
                Arg.Any<TimeFrame>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Bar>());

        var derived = await CreateReader().DeriveAsync(
            [Trade("paper", "o-2", "BTC/USDT", BaseTime, BaseTime.AddHours(2), 100m)],
            ["paper", "demo"],
            CancellationToken.None);

        derived.Should().BeEmpty("an uncovered window is the honest dash, never a fabricated excursion");
    }

    [Fact]
    public async Task TheFirstCandidateWithBarsWins_AndTheFactKeepsTheTradesOwnVenue()
    {
        // The "paper" series holds nothing (the paper lane prices off the
        // venue the facade stands on); the "demo" series covers the window.
        _cache.GetOlderThanAsync(
                "paper",
                Arg.Any<Symbol>(),
                Arg.Any<TimeFrame>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Bar>());
        _cache.GetOlderThanAsync(
                "demo",
                Arg.Any<Symbol>(),
                Arg.Any<TimeFrame>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                Bar(BaseTime.AddHours(0), 105m, 95m),
                Bar(BaseTime.AddHours(1), 103m, 99m),
            });

        var derived = await CreateReader().DeriveAsync(
            [Trade("paper", "o-2", "BTC/USDT", BaseTime, BaseTime.AddHours(2), 100m)],
            ["paper", "demo"],
            CancellationToken.None);

        derived.Should().ContainSingle();
        var fact = derived.Single();
        fact.ExchangeId.Should().Be("paper", "the join's key half is the journal's venue, never the cache's");
        fact.ClosingOrderId.Should().Be("o-2");
        fact.Mae.Should().Be(5m);
        fact.Mfe.Should().Be(5m);
    }

    [Fact]
    public async Task SymbolGroupsReadSeparately_NeverStitchAcrossSeries()
    {
        // BTC's series covers its trade's window; ETH's holds nothing — the
        // BTC fact derives, the ETH trade keeps its dash.
        var btc = Symbol.Parse("BTC/USDT");
        var eth = Symbol.Parse("ETH/USDT");
        _cache.GetOlderThanAsync(
                Arg.Any<string>(),
                btc,
                Arg.Any<TimeFrame>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(new[] { Bar(BaseTime.AddHours(0), 105m, 95m) });
        _cache.GetOlderThanAsync(
                Arg.Any<string>(),
                eth,
                Arg.Any<TimeFrame>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Bar>());

        var derived = await CreateReader().DeriveAsync(
            [
                Trade("demo", "btc-close", "BTC/USDT", BaseTime, BaseTime.AddHours(1), 100m),
                Trade("demo", "eth-close", "ETH/USDT", BaseTime, BaseTime.AddHours(1), 100m),
            ],
            ["demo"],
            CancellationToken.None);

        derived.Select(fact => fact.ClosingOrderId).Should().Equal("btc-close");
    }

    [Fact]
    public async Task AWindowTheSeriesDoesNotCover_ProducesNoFact()
    {
        // The series only holds a bar at 16:00 while the trade closed at
        // 14:00 — the slice finds nothing inside the window and the trade
        // contributes no fact (the honest absence again).
        _cache.GetOlderThanAsync(
                Arg.Any<string>(),
                Arg.Any<Symbol>(),
                Arg.Any<TimeFrame>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(
                new[] { Bar(BaseTime.AddHours(16), 120m, 110m) },
                Array.Empty<Bar>());

        var derived = await CreateReader().DeriveAsync(
            [Trade("demo", "o-2", "BTC/USDT", BaseTime, BaseTime.AddHours(2), 100m)],
            ["demo"],
            CancellationToken.None);

        derived.Should().BeEmpty();
    }

    [Fact]
    public async Task TheWalkBackStopsOnceTheWindowStartIsCovered()
    {
        // Page 1 (before 14:00) holds the 13:00 bar — its oldest bar is past
        // the window's start, so one more page walks; page 2 holds the 12:00
        // bar, whose time reaches the start, and the walk stops: exactly two
        // bounded reads, and the union covers the derivation.
        _cache.GetOlderThanAsync(
                Arg.Any<string>(),
                Arg.Any<Symbol>(),
                Arg.Any<TimeFrame>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(
                new[] { Bar(BaseTime.AddHours(1), 103m, 99m) },
                new[] { Bar(BaseTime.AddHours(0), 105m, 95m) });

        var derived = await CreateReader().DeriveAsync(
            [Trade("demo", "o-2", "BTC/USDT", BaseTime, BaseTime.AddHours(2), 100m)],
            ["demo"],
            CancellationToken.None);

        derived.Should().ContainSingle();
        derived.Single().Mae.Should().Be(5m, "the union of the pages carries the 95 dip");
        _cache.ReceivedCalls().Should().HaveCount(2, "the walk stops once the window's start is covered");
    }

    [Fact]
    public async Task ThePageSizeRideTheReaderBound()
    {
        _cache.GetOlderThanAsync(
                Arg.Any<string>(),
                Arg.Any<Symbol>(),
                Arg.Any<TimeFrame>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Bar>());

        await CreateReader().DeriveAsync(
            [Trade("demo", "o-2", "BTC/USDT", BaseTime, BaseTime.AddHours(2), 100m)],
            ["demo"],
            CancellationToken.None);

        await _cache.Received(1).GetOlderThanAsync(
            "demo",
            Arg.Any<Symbol>(),
            TimeFrame.H1,
            Arg.Any<DateTimeOffset>(),
            1_000,
            Arg.Any<CancellationToken>());
    }
}
