namespace ZettaTrade.App.Tests.ViewModels;

using System;
using System.Collections.Generic;
using System.IO;
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
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.Infrastructure.Export;
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
    private readonly ITradingAdapter _trading = Substitute.For<ITradingAdapter>();
    private readonly ICsvExportService _csv = Substitute.For<ICsvExportService>();
    private readonly IStatementExportService _statementExport = Substitute.For<IStatementExportService>();
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
        _trading,
        _csv,
        _statementExport,
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

    #region Exports (T-210)

    private static Position LongPosition(string symbol = "BTC/USDT") => new()
    {
        Id = "pos-1",
        ExchangeId = "paper",
        Symbol = Symbol.Parse(symbol),
        Side = OrderSide.Buy,
        Quantity = new Quantity(0.5m),
        EntryPrice = new Price(50_000m),
        CurrentPrice = new Price(51_000m),
        OpenedAt = BaseTime,
    };

    private void PickPositionsPath(TradeStatsViewModel viewModel, string? path) =>
        viewModel.SavePositionsCsvRequested.RegisterHandler(context =>
        {
            context.SetOutput(path);
            return Task.CompletedTask;
        });

    private void PickStatementPath(TradeStatsViewModel viewModel, string? path) =>
        viewModel.SaveStatementRequested.RegisterHandler(context =>
        {
            context.SetOutput(path);
            return Task.CompletedTask;
        });

    [Fact]
    public async Task ExportPositions_WritesTheTradedSurfacesPositionsToThePickedPath()
    {
        var positions = new[] { LongPosition() };
        _trading.GetPositionsAsync(Arg.Any<CancellationToken>()).Returns(positions);
        var viewModel = CreateViewModel();
        PickPositionsPath(viewModel, @"C:\exports\positions.csv");

        await viewModel.ExportPositionsCommand.Execute(RxVoid.Default);

        viewModel.ErrorMessage.Should().BeNull();
        await _csv.Received(1).ExportPositionsAsync(
            @"C:\exports\positions.csv",
            Arg.Is<IReadOnlyList<Position>>(list => list.SequenceEqual(positions)),
            Arg.Any<CancellationToken>());
        // The positions export reads the venue, never the journal scan.
        _query.DidNotReceiveWithAnyArgs();
    }

    [Fact]
    public async Task ExportPositions_CancelledDialog_ExportsNothing()
    {
        _trading.GetPositionsAsync(Arg.Any<CancellationToken>()).Returns(new[] { LongPosition() });
        var viewModel = CreateViewModel();
        PickPositionsPath(viewModel, null);

        await viewModel.ExportPositionsCommand.Execute(RxVoid.Default);

        viewModel.ErrorMessage.Should().BeNull("a cancelled dialog is a quiet no-op, never an error");
        await _csv.DidNotReceiveWithAnyArgs().ExportPositionsAsync(
            default!, Arg.Any<IReadOnlyList<Position>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExportPositions_VenueFault_ReportsTheLocalizedFailureAndStaysUsable()
    {
        _trading.GetPositionsAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<IReadOnlyList<Position>>(
                new InvalidOperationException("venue offline")));
        var viewModel = CreateViewModel();
        PickPositionsPath(viewModel, @"C:\exports\positions.csv");

        await viewModel.ExportPositionsCommand.Execute(RxVoid.Default);

        viewModel.ErrorMessage.Should().Be("[Stats.ExportFailedFormat:venue offline]");
        viewModel.HasErrorMessage.Should().BeTrue();

        // The window stays usable: the next export clears the failure line.
        _trading.GetPositionsAsync(Arg.Any<CancellationToken>()).Returns(new[] { LongPosition() });
        await viewModel.ExportPositionsCommand.Execute(RxVoid.Default);
        viewModel.ErrorMessage.Should().BeNull();
        await _csv.Received(1).ExportPositionsAsync(
            @"C:\exports\positions.csv",
            Arg.Any<IReadOnlyList<Position>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExportStatement_WritesTheScannedBundleUnderTheWindowFilter()
    {
        var page = new[]
        {
            Fill(OrderSide.Buy, 1m, 10m, 0, "o-1"),
            Fill(OrderSide.Sell, 1m, 12m, 1, "o-2"),
        };
        _query.ReadPageAsync(
                Arg.Any<OrderStatsFilter>(),
                Arg.Any<Order?>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(page, Array.Empty<Order>());
        _trading.GetBalancesAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, Money>
            {
                ["USDT"] = new Money(1_000m, "USDT"),
                ["BTC"] = new Money(0.5m, "BTC"),
            });
        var viewModel = CreateViewModel();
        viewModel.SymbolFilterText = " btc/usdt ";
        viewModel.VenueIndex = 1;
        PickStatementPath(viewModel, @"C:\exports\trade-statement.json");
        var captured = default(StatementExport);
        _statementExport
            .When(w => w.WriteAsync(
                Arg.Any<string>(),
                Arg.Any<StatementExport>(),
                Arg.Any<CancellationToken>()))
            .Do(call => captured = call.ArgAt<StatementExport>(1));

        await viewModel.ExportStatementCommand.Execute(RxVoid.Default);

        viewModel.ErrorMessage.Should().BeNull();
        await _statementExport.Received(1).WriteAsync(
            @"C:\exports\trade-statement.json",
            Arg.Any<StatementExport>(),
            Arg.Any<CancellationToken>());
        captured.Should().NotBeNull();
        captured!.Orders.Should().Equal(page, "the bundle carries the scan's whole order set");
        captured.SymbolFilter.Should().Be("BTC/USDT", "the filter echo carries the CORE spelling");
        captured.ExchangeFilter.Should().BeNull();
        captured.IsPaperFilter.Should().BeTrue("venue slot 1 is the paper tag");
        captured.FromUtc.Should().BeNull();
        captured.ToUtc.Should().BeNull();
        captured.Balances.Should().HaveCount(2);
        captured.Balances["USDT"].Amount.Should().Be(1_000m);
        var trade = captured.RealizedTrades.Should().ContainSingle().Subject;
        trade.Quantity.Should().Be(1m);
        trade.EntryPrice.Should().Be(10m);
        trade.ExitPrice.Should().Be(12m);
        trade.RealizedPnl.Should().Be(2m);
        trade.ClosingSide.Should().Be("Sell");
        trade.Symbol.Should().Be("BTC/USDT");
        trade.ExchangeId.Should().Be("paper");
        trade.QuoteCurrency.Should().Be("USDT");
        trade.ClosedAt.Should().Be(BaseTime.AddMinutes(1));
    }

    [Fact]
    public async Task ExportStatement_EmptyRange_RefusesWithoutTouchingTheWriters()
    {
        _query.ReadPageAsync(
                Arg.Any<OrderStatsFilter>(),
                Arg.Any<Order?>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Order>());
        var viewModel = CreateViewModel();
        PickStatementPath(viewModel, @"C:\exports\trade-statement.json");

        await viewModel.ExportStatementCommand.Execute(RxVoid.Default);

        viewModel.ErrorMessage.Should().Be(
            "[Stats.StatementEmptyRange]",
            "an empty range exports nothing, never a broken bundle");
        await _statementExport.DidNotReceiveWithAnyArgs().WriteAsync(
            default!, Arg.Any<StatementExport>(), Arg.Any<CancellationToken>());
        // The balance snapshot only rides a bundle that exists.
        _trading.DidNotReceiveWithAnyArgs();
    }

    [Fact]
    public async Task ExportStatement_UnparsableSymbol_ReportsAndNeverScans()
    {
        var viewModel = CreateViewModel();
        viewModel.SymbolFilterText = "btc / usdt";
        PickStatementPath(viewModel, @"C:\exports\trade-statement.json");

        await viewModel.ExportStatementCommand.Execute(RxVoid.Default);

        viewModel.ErrorMessage.Should().Be("[Stats.InvalidSymbol:btc / usdt]");
        _query.DidNotReceiveWithAnyArgs();
        await _statementExport.DidNotReceiveWithAnyArgs().WriteAsync(
            default!, Arg.Any<StatementExport>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExportStatement_StorageFault_ReportsTheLocalizedFailure()
    {
        var page = new[] { Fill(OrderSide.Buy, 1m, 10m, 0, "o-1") };
        _query.ReadPageAsync(
                Arg.Any<OrderStatsFilter>(),
                Arg.Any<Order?>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(page, Array.Empty<Order>());
        _trading.GetBalancesAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, Money> { ["USDT"] = new Money(100m, "USDT") });
        _statementExport
            .WriteAsync(Arg.Any<string>(), Arg.Any<StatementExport>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException(new IOException("disk full")));
        var viewModel = CreateViewModel();
        PickStatementPath(viewModel, @"C:\exports\trade-statement.json");

        await viewModel.ExportStatementCommand.Execute(RxVoid.Default);

        viewModel.ErrorMessage.Should().Be("[Stats.ExportFailedFormat:disk full]");
    }

    #endregion Exports (T-210)
}
