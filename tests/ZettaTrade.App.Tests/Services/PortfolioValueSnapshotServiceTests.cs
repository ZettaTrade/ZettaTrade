namespace ZettaTrade.App.Tests.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using ZettaTrade.App.Services;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.Infrastructure.Persistence;

/// <summary>
/// The portfolio-value snapshot service battery (T-209): the quote asset
/// rides at par, a priced asset enters the total at its ticker price, an
/// unpriced asset is disclosed and excluded, the allocation sums to the
/// row's total in camelCase JSON, an idle account writes once and skips,
/// a moved account writes again, an empty account writes nothing, a
/// store fault surfaces honestly, and the cadence constant stays pinned.
/// </summary>
public sealed class PortfolioValueSnapshotServiceTests
{
    #region Helpers

    private static readonly DateTimeOffset TickTime = new(2026, 11, 13, 10, 0, 0, TimeSpan.Zero);

    private readonly IPortfolioValueStore _store = Substitute.For<IPortfolioValueStore>();
    private readonly IMarketDataAdapter _marketData = Substitute.For<IMarketDataAdapter>();
    private readonly ITradingAdapter _account = Substitute.For<ITradingAdapter>();
    private readonly List<PortfolioValueSnapshotRow> _written = [];

    public PortfolioValueSnapshotServiceTests()
    {
        _account.Id.Returns("paper");
        _store
            .WriteAsync(Arg.Do<PortfolioValueSnapshotRow>(_written.Add), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
    }

    private PortfolioValueSnapshotService CreateService() => new(
        _account,
        _marketData,
        _store,
        NullLogger<PortfolioValueSnapshotService>.Instance);

    private static Ticker BuildTicker(decimal price) => new(
        Symbol: Symbol.Parse("BTC/USDT"),
        LastPrice: price,
        Bid: price,
        Ask: price,
        Volume24h: 0m,
        PriceChange24hPercent: 0m,
        Timestamp: TickTime);

    /// <summary>The facade replays one price to every late subscriber (the hot-subject trap avoided).</summary>
    private void ServePrice(Symbol symbol, decimal price)
    {
        var subject = new ReplaySubject<Ticker>(1);
        subject.OnNext(BuildTicker(price) with { Symbol = symbol });
        _marketData.SubscribeTicker(symbol).Returns(subject);
    }

    /// <summary>The facade serves the stream but prices nothing yet (the honest unpriced path).</summary>
    private void ServeSilent(Symbol symbol)
    {
        _marketData.SubscribeTicker(symbol).Returns(new Subject<Ticker>());
    }

    /// <summary>The facade refuses the subscription (no feed for the pair).</summary>
    private void RefuseSubscription(Symbol symbol) =>
        _marketData
            .When(subscribe => subscribe.SubscribeTicker(symbol))
            .Do(_ => throw new NotSupportedException("no feed for the pair"));

    private static IReadOnlyDictionary<string, Money> Balances(params (string Asset, decimal Amount)[] entries) =>
        entries.ToDictionary(entry => entry.Asset, entry => new Money(entry.Amount, entry.Asset));

    private void ServeBalances(params (string Asset, decimal Amount)[] entries) =>
        _account.GetBalancesAsync(Arg.Any<CancellationToken>()).Returns(Balances(entries));

    #endregion Helpers

    #region Facts

    /// <summary>The quote-only account writes one row at par; the allocation sums to the total.</summary>
    [Fact]
    public async Task FirstPass_WithQuoteOnlyBalances_WritesOneRowAtPar()
    {
        // Arrange
        ServeBalances(("USDT", 1000m));
        await using var service = CreateService();

        // Act
        await service.TakeSnapshotAsync();

        // Assert
        _written.Should().HaveCount(1);
        var row = _written[0];
        row.ExchangeId.Should().Be("paper");
        row.QuoteCurrency.Should().Be("USDT");
        row.TotalValue.Should().Be(1000m);
        var allocation = PortfolioValueAllocationEntry.ParseAllocation(row.AllocationJson);
        allocation.Should().ContainSingle().Which.Should().Be(
            new PortfolioValueAllocationEntry("USDT", 1000m, 1m, 1000m, Priced: true));
        PortfolioValueAllocationEntry.PricedTotal(allocation).Should().Be(row.TotalValue);
    }

    /// <summary>The idle account writes once and then skips: nothing changed materially.</summary>
    [Fact]
    public async Task IdenticalSecondPass_WritesNothing()
    {
        // Arrange
        ServeBalances(("USDT", 1000m));
        await using var service = CreateService();

        // Act
        await service.TakeSnapshotAsync();
        await service.TakeSnapshotAsync();

        // Assert
        _written.Should().HaveCount(1, "an idle account's second pass is skipped");
    }

    /// <summary>A moved account writes again: the new facts land as one more row.</summary>
    [Fact]
    public async Task MovedAccount_WritesAgain()
    {
        // Arrange
        ServeBalances(("USDT", 1000m));
        await using var service = CreateService();
        await service.TakeSnapshotAsync();

        // Act: the account moved (a fill changed the balances).
        ServeBalances(("USDT", 1200m));
        await service.TakeSnapshotAsync();

        // Assert
        _written.Should().HaveCount(2);
        _written[1].TotalValue.Should().Be(1200m);
    }

    /// <summary>An account holding nothing has no value to snapshot: nothing is written.</summary>
    [Fact]
    public async Task EmptyBalances_WritesNothing()
    {
        // Arrange
        ServeBalances();
        await using var service = CreateService();

        // Act
        await service.TakeSnapshotAsync();
        await service.TakeSnapshotAsync();

        // Assert
        _written.Should().BeEmpty();
    }

    /// <summary>A store fault surfaces honestly from the pass (the loop catches and retries).</summary>
    [Fact]
    public async Task StoreFault_ThePassFaultsHonestly()
    {
        // Arrange
        ServeBalances(("USDT", 1000m));
        _store
            .WriteAsync(Arg.Any<PortfolioValueSnapshotRow>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("store boom"));
        await using var service = CreateService();

        // Act + Assert
        var pass = async () => await service.TakeSnapshotAsync();
        await pass.Should().ThrowAsync<InvalidOperationException>();
    }

    /// <summary>A priced asset enters the total at its ticker price.</summary>
    [Fact]
    public async Task PricedAsset_EntersTheTotalAtTheTickerPrice()
    {
        // Arrange: the facade prices BTC/USDT at 50_000 (replayed to late subscribers).
        ServeBalances(("USDT", 100m), ("BTC", 0.5m));
        ServePrice(Symbol.Parse("BTC/USDT"), 50_000m);
        await using var service = CreateService();

        // Act
        await service.TakeSnapshotAsync();

        // Assert
        _written.Should().HaveCount(1);
        var row = _written[0];
        row.TotalValue.Should().Be(25_100m, "the USDT rides at par, the BTC rides its ticker price");
        var allocation = PortfolioValueAllocationEntry.ParseAllocation(row.AllocationJson);
        allocation.Should().Contain(entry => entry.Asset == "BTC").Which.Should().Be(
            new PortfolioValueAllocationEntry("BTC", 0.5m, 50_000m, 25_000m, Priced: true));
        PortfolioValueAllocationEntry.PricedTotal(allocation).Should().Be(row.TotalValue);
    }

    /// <summary>An unpriced asset is disclosed with its amount and excluded from the total.</summary>
    [Fact]
    public async Task UnpricedAsset_IsDisclosedAndExcluded()
    {
        // Arrange: the stream is served but silent — no price ever arrived.
        ServeBalances(("USDT", 100m), ("BTC", 0.5m));
        ServeSilent(Symbol.Parse("BTC/USDT"));
        await using var service = CreateService();

        // Act
        await service.TakeSnapshotAsync();

        // Assert
        _written.Should().HaveCount(1);
        var row = _written[0];
        row.TotalValue.Should().Be(100m, "the unpriced asset never enters the total");
        var allocation = PortfolioValueAllocationEntry.ParseAllocation(row.AllocationJson);
        allocation.Should().Contain(entry => entry.Asset == "BTC").Which.Should().Be(
            new PortfolioValueAllocationEntry("BTC", 0.5m, null, null, Priced: false));
        PortfolioValueAllocationEntry.PricedTotal(allocation).Should().Be(row.TotalValue);
    }

    /// <summary>A refused subscription is honest too: the asset stays unpriced, disclosed.</summary>
    [Fact]
    public async Task RefusedSubscription_TheAssetStaysUnpriced()
    {
        // Arrange
        ServeBalances(("USDT", 100m), ("BTC", 0.5m));
        RefuseSubscription(Symbol.Parse("BTC/USDT"));
        await using var service = CreateService();

        // Act
        await service.TakeSnapshotAsync();

        // Assert
        _written.Should().HaveCount(1);
        var allocation = PortfolioValueAllocationEntry.ParseAllocation(_written[0].AllocationJson);
        allocation.Should().Contain(entry => entry.Asset == "BTC").Which.Priced.Should().BeFalse();
        _written[0].TotalValue.Should().Be(100m);
    }

    /// <summary>The allocation document rides camelCase keys (the canonical stored shape).</summary>
    [Fact]
    public async Task TheAllocationDocument_IsCamelCase()
    {
        // Arrange
        ServeBalances(("USDT", 1000m));
        await using var service = CreateService();

        // Act
        await service.TakeSnapshotAsync();

        // Assert
        _written[0].AllocationJson.Should().Contain("\"asset\":\"USDT\"");
        _written[0].AllocationJson.Should().Contain("\"priced\":true");
    }

    /// <summary>The cadence constant stays pinned: the write bound is 30 seconds.</summary>
    [Fact]
    public void TheCadenceConstant_IsTheBoundedThirtySeconds()
    {
        PortfolioValueSnapshotService.SnapshotInterval.Should().Be(TimeSpan.FromSeconds(30));
    }

    #endregion Facts
}
