namespace ZettaTrade.App.Tests.ViewModels;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Livery.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ReactiveUI.Primitives;
using Xunit;
using ZettaTrade.App.ViewModels;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.Infrastructure.Persistence;

/// <summary>
/// Tests for the trade-statistics window view model: the paged scan rides
/// the store's keyset contract (the anchor moves from null to the previous
/// page's last row, a short page reads exhaust), the honest zero/dash
/// baseline on an empty scope, a restart that scopes to the freshly edited
/// filters, an unparsable symbol that reports an error line without ever
/// touching the store, and the pure dash state a fresh window carries
/// before the first apply.
/// </summary>
public sealed class TradeStatsViewModelTests
{
    #region Test Constants

    /// <summary>The window's scan page size: asserted, not understood.</summary>
    private const int ExpectedPageSize = 2000;

    private static readonly DateTimeOffset BaseTime = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    #endregion Test Constants

    #region Fixture

    private readonly IOrderStatsQuery _query = Substitute.For<IOrderStatsQuery>();
    private readonly ILocalizationManager _localization = Substitute.For<ILocalizationManager>();

    public TradeStatsViewModelTests()
    {
        // The fake resolves dynamic messages in a predictable, test-visible
        // form: the key alone or the key plus the first argument.
        _localization.GetString(Arg.Any<string>()).Returns(call =>
            $"[{call.ArgAt<string>(0)}]");
        _localization.GetString(Arg.Any<string>(), Arg.Any<object[]>()).Returns(call =>
            $"[{call.ArgAt<string>(0)}:{string.Join("|", call.ArgAt<object[]>(1))}]");
    }

    private TradeStatsViewModel CreateViewModel() => new(
        _query,
        _localization,
        NullLogger<TradeStatsViewModel>.Instance);

    private static Order Fill(OrderSide side, decimal quantity, decimal price, int minute, string id) => new()
    {
        Id = id,
        ExchangeId = "paper",
        Symbol = Symbol.Parse("BTC/USDT"),
        Side = side,
        Type = OrderType.Limit,
        Quantity = new Quantity(quantity),
        Price = new Price(price),
        Status = OrderStatus.Filled,
        TimeInForce = TimeInForce.GTC,
        CreatedAt = BaseTime.AddMinutes(minute),
        FilledQuantity = new Quantity(quantity),
    };

    private async Task ApplyAsync(TradeStatsViewModel viewModel) =>
        await viewModel.ApplyCommand.Execute(RxVoid.Default);

    #endregion Fixture

    #region Paged Scan

    [Fact]
    public async Task Apply_PagesThroughStore_MovingTheAnchorAndStoppingOnAShortPage()
    {
        var page1 = new[]
        {
            Fill(OrderSide.Buy, 1m, 10m, 0, "o-1"),
            Fill(OrderSide.Sell, 1m, 12m, 1, "o-2"),
        };
        _query.ReadPageAsync(
                Arg.Any<OrderStatsFilter>(),
                Arg.Any<Order?>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(page1, Array.Empty<Order>());

        var viewModel = CreateViewModel();
        await ApplyAsync(viewModel);

        // The tiles carry the one closed trade...
        viewModel.HasTrades.Should().BeTrue();
        viewModel.TradesText.Should().Be("1", "one open followed by one closing fill = one trade");
        viewModel.ScanStateText.Should().Be("[Stats.Scanned:2]");

        // ...and the store saw exactly two reads with the keyset anchors.
        _query.ReceivedCalls().Should().HaveCount(2);
        var first = _query.ReceivedCalls().First();
        var second = _query.ReceivedCalls().Last();

        first.GetArguments()[2].Should().Be(ExpectedPageSize);
        first.GetArguments()[1].Should().BeNull("the first page starts at the scope's beginning");

        second.GetArguments()[2].Should().Be(ExpectedPageSize);
        second.GetArguments()[1].Should().Be(page1[^1], "the continuation is the previous page's last row");
    }

    [Fact]
    public async Task Apply_EmptyStore_ReportsTheHonestZeroBaseline()
    {
        _query.ReadPageAsync(
                Arg.Any<OrderStatsFilter>(),
                Arg.Any<Order?>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Order>());

        var viewModel = CreateViewModel();
        await ApplyAsync(viewModel);

        viewModel.HasTrades.Should().BeFalse();
        viewModel.TradesText.Should().Be("0");
        viewModel.WinsText.Should().Be("0");
        viewModel.LossesText.Should().Be("0");
        viewModel.WinRateText.Should().Be("—", "a dash stands where nothing decided");
        viewModel.ProfitFactorText.Should().Be("—", "no gross loss, no bound — but no wins either, so a dash");
        viewModel.AverageWinnerText.Should().Be("—");
        viewModel.AverageLoserText.Should().Be("—");
        viewModel.MaxDrawdownText.Should().Be("0.0000");
        viewModel.RealizedText.Should().Be("0.0000");
        viewModel.VolumeText.Should().Be("0.0000");
        viewModel.EquityPoints.Should().BeEmpty();
        viewModel.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task FreshViewModel_CarriesTheZeroBaselineBefore_FirstApply()
    {
        var viewModel = CreateViewModel();

        viewModel.HasTrades.Should().BeFalse();
        viewModel.TradesText.Should().Be("0");
        viewModel.WinRateText.Should().Be("—");
        viewModel.ScanStateText.Should().BeEmpty("nothing started yet, no pretending a scan ran");

        _query.DidNotReceiveWithAnyArgs();
    }

    #endregion Paged Scan

    #region Filters

    [Fact]
    public async Task Apply_SendsTheScopingFilterToTheStore_IncludingTheNormalizedSymbol()
    {
        _query.ReadPageAsync(
                Arg.Any<OrderStatsFilter>(),
                Arg.Any<Order?>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Order>());

        var viewModel = CreateViewModel();
        viewModel.SymbolFilterText = " btc/usdt ";
        viewModel.ExchangeFilterText = "paper";
        viewModel.VenueIndex = 1;
        viewModel.FromDate = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        viewModel.ToDate = new DateTimeOffset(2026, 1, 31, 0, 0, 0, TimeSpan.Zero);
        await ApplyAsync(viewModel);

        var filter = (OrderStatsFilter)_query.ReceivedCalls().First().GetArguments()[0]!;
        filter.Symbol.Should().Be("BTC/USDT", "the filter carries the CORE normalized spelling");
        filter.Exchange.Should().Be("paper");
        filter.IsPaper.Should().BeTrue("venue slot 1 is the paper tag");
        filter.From.Should().Be(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        filter.To.Should().Be(new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero),
            "the whole end day stays inside the inclusive-to/exclusive-to window");
    }

    [Fact]
    public async Task Restart_WithEditedFilters_ReScopesTheSameSequentialWalk()
    {
        _query.ReadPageAsync(
                Arg.Any<OrderStatsFilter>(),
                Arg.Any<Order?>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Order>());

        var viewModel = CreateViewModel();
        viewModel.SymbolFilterText = "BTC/USDT";
        await ApplyAsync(viewModel);

        viewModel.SymbolFilterText = "ETH/USDT";
        await ApplyAsync(viewModel);

        var filters = _query.ReceivedCalls()
            .Select(call => (OrderStatsFilter)call.GetArguments()[0]!)
            .ToArray();
        filters.Should().HaveCount(2);
        filters.Select(filter => filter.Symbol)
            .Should().Equal(["BTC/USDT", "ETH/USDT"]);
    }

    #endregion Filters

    #region Input Guardians

    [Fact]
    public async Task Apply_WithAnUnparsableSymbol_ReportsAndNeverTouchesTheStore()
    {
        var viewModel = CreateViewModel();
        viewModel.SymbolFilterText = "btc / usdt";
        await ApplyAsync(viewModel);

        viewModel.ErrorMessage.Should().Be("[Stats.InvalidSymbol:btc / usdt]");
        viewModel.HasErrorMessage.Should().BeTrue();
        viewModel.HasTrades.Should().BeFalse();
        _query.DidNotReceiveWithAnyArgs();
    }

    [Fact]
    public async Task Apply_WithAStoreFailure_ReportsTheLocalizedErrorLine()
    {
        _query.ReadPageAsync(
                Arg.Any<OrderStatsFilter>(),
                Arg.Any<Order?>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<IReadOnlyList<Order>>(new InvalidOperationException("database is locked")));

        var viewModel = CreateViewModel();
        await ApplyAsync(viewModel);

        viewModel.ErrorMessage.Should().Be("[Stats.LoadFailed:database is locked]");
        viewModel.HasTrades.Should().BeFalse();
    }

    #endregion Input Guardians
}
