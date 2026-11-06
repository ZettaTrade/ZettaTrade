namespace ZettaTrade.App.Tests.Services;

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;
using ZettaTrade.App.Services;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// Aggregation goldens: the snapshot sums each asset across the adapters
/// that actually serve balances, keeps every source's own availability
/// explicit (a market-data-only adapter is not provided, a faulting query
/// is a fault — never a zero figure), keeps the registration order, and
/// counts a shared trading face once.
/// </summary>
public sealed class BalancesAggregationServiceTests
{
    [Fact]
    public async Task LoadSnapshot_SumsPerAssetAcrossProvidingAdapters()
    {
        // Arrange: two serving adapters plus one market-data-only adapter
        // and the paper trading face, in registration order.
        var binance = CreateTrader("binance", "Binance", Balances(("USDT", 4000m), ("BTC", 0.4m)));
        var bybit = CreateTrader("bybit", "Bybit", Balances(("USDT", 3000m), ("ETH", 2.5m)));
        var demo = CreateMarketDataOnly("demo", "Demo");
        var paper = CreateTrader("paper", "Paper Trading", Balances(("USDT", 1000m)));
        var service = CreateService([binance, bybit, demo], paper);

        // Act
        var snapshot = await service.LoadSnapshotAsync();

        // Assert: the per-exchange side stays explicit per source.
        snapshot.Sources.Select(source => source.AdapterName)
            .Should().Equal(["Binance", "Bybit", "Demo", "Paper Trading"]);
        snapshot.Sources[0].Availability.Should().Be(BalanceAvailability.Provided);
        snapshot.Sources[1].Availability.Should().Be(BalanceAvailability.Provided);
        snapshot.Sources[2].Availability.Should().Be(BalanceAvailability.NotProvided);
        snapshot.Sources[3].Availability.Should().Be(BalanceAvailability.Provided);

        // Assert: the aggregate sums ONLY the providing sources, per asset,
        // in a deterministic asset order.
        snapshot.Aggregated.Select(row => row.Asset)
            .Should().Equal(["BTC", "ETH", "USDT"]);
        snapshot.Aggregated.Single(row => row.Asset == "BTC").Amount.Should().Be(0.4m);
        snapshot.Aggregated.Single(row => row.Asset == "ETH").Amount.Should().Be(2.5m);
        snapshot.Aggregated.Single(row => row.Asset == "USDT").Amount.Should().Be(8000m);
    }

    [Fact]
    public async Task LoadSnapshot_AdapterWithoutBalances_IsIgnoredByTheAggregate()
    {
        // Arrange: the market-data-only adapter contributes no rows, no zeros.
        var demo = CreateMarketDataOnly("demo", "Demo");
        var paper = CreateTrader("paper", "Paper Trading", Balances(("USDT", 1000m)));
        var service = CreateService([demo], paper);

        // Act
        var snapshot = await service.LoadSnapshotAsync();

        // Assert
        snapshot.Sources.Should().HaveCount(2);
        snapshot.Sources[0].Availability.Should().Be(BalanceAvailability.NotProvided);
        snapshot.Sources[0].Balances.Should().BeEmpty("absence is never a zero figure");
        snapshot.Aggregated.Should().HaveCount(1);
        snapshot.Aggregated[0].Asset.Should().Be("USDT");
        snapshot.Aggregated[0].Amount.Should().Be(1000m, "only the providing source enters the aggregate");
    }

    [Fact]
    public async Task LoadSnapshot_ProvidingAdaptersWithEmptyBalances_YieldAnEmptyAggregate()
    {
        // Arrange: provided but empty is the adapter's honest answer (e.g.
        // before any fill); the aggregate stays empty, never a zero row.
        var demoTrader = CreateTrader("demo", "Demo Trader", Balances());
        var paper = CreateTrader("paper", "Paper Trading", Balances());
        var service = CreateService([demoTrader], paper);

        // Act
        var snapshot = await service.LoadSnapshotAsync();

        // Assert
        snapshot.Sources.Should().OnlyContain(source => source.Availability == BalanceAvailability.Provided);
        snapshot.Aggregated.Should().BeEmpty();
    }

    [Fact]
    public async Task LoadSnapshot_TradingFaceAlsoRegistered_IsQueriedOnce()
    {
        // Arrange: the injected trading face IS one of the registered
        // adapters — a duplicate fetch would double-count its assets.
        var paper = CreateTrader("paper", "Paper Trading", Balances(("USDT", 1000m)));
        var service = CreateService([paper], paper);

        // Act
        var snapshot = await service.LoadSnapshotAsync();

        // Assert
        snapshot.Sources.Should().HaveCount(1);
        await paper.Received(1).GetBalancesAsync(Arg.Any<CancellationToken>());
        snapshot.Aggregated.Single(row => row.Asset == "USDT").Amount.Should().Be(1000m);
    }

    [Fact]
    public async Task LoadSnapshot_FaultingBalanceQuery_ReportsTheFaultAndKeepsTheOtherSources()
    {
        // Arrange: one adapter's query faults; the other must still serve.
        var broken = CreateTrader("broken", "Broken Trader", Balances(("USDT", 500m)));
        broken.GetBalancesAsync(Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyDictionary<string, Money>>>(callInfo =>
                throw new InvalidOperationException("socket boom"));
        var paper = CreateTrader("paper", "Paper Trading", Balances(("USDT", 1000m)));
        var service = CreateService([broken], paper);

        // Act
        var snapshot = await service.LoadSnapshotAsync();

        // Assert: the fault is explicit, scoped to its source, and zero-free.
        snapshot.Sources[0].Availability.Should().Be(BalanceAvailability.FetchFailed);
        snapshot.Sources[0].Failure.Should().Be("socket boom");
        snapshot.Sources[0].Balances.Should().BeEmpty("a failed query is never displayed as figures");
        snapshot.Sources[1].Availability.Should().Be(BalanceAvailability.Provided);

        snapshot.Aggregated.Should().HaveCount(1);
        snapshot.Aggregated[0].Asset.Should().Be("USDT");
        snapshot.Aggregated[0].Amount.Should().Be(1000m, "the failed source contributes nothing");
    }

    [Fact]
    public async Task LoadSnapshot_CancelledBalanceQuery_CancelsTheWholeSnapshot()
    {
        // Arrange: the adapter cancels its query — the caller's cancellation
        // semantics stay honest (the snapshot aborts, no partial figures).
        var cancelled = new CancellationToken(canceled: true);
        var paper = CreateTrader("paper", "Paper Trading", Balances(("USDT", 1000m)));
        paper.GetBalancesAsync(Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyDictionary<string, Money>>>(callInfo =>
                Task.FromCanceled<IReadOnlyDictionary<string, Money>>(cancelled));
        var service = CreateService([], paper);

        // Act
        var act = async () => await service.LoadSnapshotAsync();

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static BalancesAggregationService CreateService(
        IReadOnlyList<IMarketDataAdapter> registered,
        ITradingAdapter trading) =>
        new(
            registered,
            trading,
            Substitute.For<ILogger<BalancesAggregationService>>());

    private static ITradingAdapter CreateTrader(
        string id,
        string name,
        IReadOnlyDictionary<string, Money> balances)
    {
        var trader = Substitute.For<ITradingAdapter>();
        trader.Id.Returns(id);
        trader.Name.Returns(name);
        trader.GetBalancesAsync(Arg.Any<CancellationToken>()).Returns(balances);
        return trader;
    }

    private static IMarketDataAdapter CreateMarketDataOnly(string id, string name)
    {
        var adapter = Substitute.For<IMarketDataAdapter>();
        adapter.Id.Returns(id);
        adapter.Name.Returns(name);
        return adapter;
    }

    private static IReadOnlyDictionary<string, Money> Balances(params (string Asset, decimal Amount)[] entries) =>
        entries.ToDictionary(entry => entry.Asset, entry => new Money(entry.Amount, entry.Asset));
}
