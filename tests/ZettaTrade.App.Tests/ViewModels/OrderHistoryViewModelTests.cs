namespace ZettaTrade.App.Tests.ViewModels;

using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Livery.Localization;
using NSubstitute;
using Xunit;
using ZettaTrade.App.ViewModels;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.Infrastructure.Persistence;

/// <summary>
/// Tests for the order history window view model: rows load from the
/// persistent store newest first with precomputed localized display values,
/// an empty journal shows the empty state, and a store failure surfaces the
/// localized error line.
/// </summary>
public sealed class OrderHistoryViewModelTests
{
    private readonly IOrderHistoryStore _store = Substitute.For<IOrderHistoryStore>();
    private readonly ILocalizationManager _localization = Substitute.For<ILocalizationManager>();

    public OrderHistoryViewModelTests()
    {
        // The fake resolves dynamic messages in a predictable, test-visible
        // form: the key plus the first argument, e.g. "NewOrder.OrderRejected:reason".
        _localization.GetString(Arg.Any<string>()).Returns(call =>
            $"[{call.ArgAt<string>(0)}]");
        _localization.GetString(Arg.Any<string>(), Arg.Any<object[]>()).Returns(call =>
            $"[{call.ArgAt<string>(0)}:{string.Join("|", call.ArgAt<object[]>(1))}]");
    }

    private OrderHistoryViewModel CreateViewModel(
        ZettaTrade.Infrastructure.Export.ICsvExportService? export = null) => new(
        _store,
        export ?? Substitute.For<ZettaTrade.Infrastructure.Export.ICsvExportService>(),
        _localization,
        Substitute.For<Microsoft.Extensions.Logging.ILogger<OrderHistoryViewModel>>());

    private static Order BuildOrder(
        string id,
        OrderStatus status,
        DateTimeOffset createdAt,
        decimal? commission = null,
        OrderSide side = OrderSide.Sell) => new()
        {
            Id = id,
            ExchangeId = "paper",
            Symbol = Symbol.Parse("BTC/USDT"),
            Side = side,
            Type = OrderType.Market,
            Quantity = new Quantity(0.02m),
            Price = new Price(50_100m),
            Status = status,
            TimeInForce = TimeInForce.GTC,
            CreatedAt = createdAt,
            Commission = commission,
        };

    [Fact]
    public async Task LoadCommand_WithPersistedOrders_PopulatesRowsNewestFirst()
    {
        // Arrange: the store orders rows newest first by contract. CreatedAt
        // is built in the local offset: the journal keeps UTC and the row
        // display converts back to the local wall clock (see ToLocalTime),
        // so a fixed local moment maps to itself on any machine's zone.
        var localOffset = TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 1, 1, 12, 5, 0));
        _store.GetRecentAsync(Arg.Any<int>())
            .Returns(
            [
                BuildOrder("paper-2", OrderStatus.Cancelled, new DateTimeOffset(2026, 1, 1, 12, 5, 0, localOffset)),
                BuildOrder("paper-1", OrderStatus.Filled, new DateTimeOffset(2026, 1, 1, 12, 0, 0, localOffset)),
            ]);

        var viewModel = CreateViewModel();

        // Act
        await viewModel.LoadCommand.Execute(ReactiveUI.Primitives.RxVoid.Default);

        // Assert: localized, precomputed display rows.
        viewModel.HasRows.Should().BeTrue();
        viewModel.Rows.Should().HaveCount(2);
        viewModel.Rows[0].Opened.Should().Be("2026-01-01 12:05");
        viewModel.Rows[0].Exchange.Should().Be("paper");
        viewModel.Rows[0].Symbol.Should().Be("BTC/USDT");
        viewModel.Rows[0].Side.Should().Be("[Order.Sell]");
        viewModel.Rows[0].Type.Should().Be("[Order.Market]");
        viewModel.Rows[0].Quantity.Should().Be("0.02");
        viewModel.Rows[0].Price.Should().Be("50100.00000000");
        viewModel.Rows[0].Status.Should().Be("[Positions.StatusCancelled]");
        viewModel.Rows[1].Opened.Should().Be("2026-01-01 12:00");
        viewModel.ErrorMessage.Should().BeNull();
    }

    /// <summary>
    /// The side chips' row state (T-157): the computed flags ride the row
    /// exactly as the parent built it — one true per side, derived from the
    /// order side itself, never re-parsed from the localized display text.
    /// </summary>
    [Fact]
    public async Task LoadCommand_BuildsTheRowSideStateForTheChips()
    {
        // Arrange: a buy and a sell order, both through the store.
        var localOffset = TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 1, 1, 12, 5, 0));
        _store.GetRecentAsync(Arg.Any<int>())
            .Returns(
            [
                BuildOrder("paper-2", OrderStatus.Cancelled, new DateTimeOffset(2026, 1, 1, 12, 5, 0, localOffset), side: OrderSide.Buy),
                BuildOrder("paper-1", OrderStatus.Filled, new DateTimeOffset(2026, 1, 1, 12, 0, 0, localOffset)),
            ]);

        var viewModel = CreateViewModel();

        // Act
        await viewModel.LoadCommand.Execute(ReactiveUI.Primitives.RxVoid.Default);

        // Assert: exactly one computed flag is true per row.
        viewModel.Rows[0].IsBuySide.Should().BeTrue();
        viewModel.Rows[0].IsSellSide.Should().BeFalse();
        viewModel.Rows[1].IsBuySide.Should().BeFalse();
        viewModel.Rows[1].IsSellSide.Should().BeTrue();
    }

    [Fact]
    public async Task LoadCommand_WithEmptyStore_ShowsEmptyState()
    {
        // Arrange
        _store.GetRecentAsync(Arg.Any<int>()).Returns([]);
        var viewModel = CreateViewModel();

        // Act
        await viewModel.LoadCommand.Execute(ReactiveUI.Primitives.RxVoid.Default);

        // Assert
        viewModel.Rows.Should().BeEmpty();
        viewModel.HasRows.Should().BeFalse();
        viewModel.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task LoadCommand_WhenStoreThrows_ShowsLoadFailedError()
    {
        // Arrange
        _store.GetRecentAsync(Arg.Any<int>())
            .Returns(_ => System.Threading.Tasks.Task.FromException<System.Collections.Generic.IReadOnlyList<Order>>(
                new InvalidOperationException("database is locked")));
        var viewModel = CreateViewModel();

        // Act
        await viewModel.LoadCommand.Execute(ReactiveUI.Primitives.RxVoid.Default);

        // Assert
        viewModel.ErrorMessage.Should().Contain("History.LoadFailed").And.Contain("database is locked");
        viewModel.HasErrorMessage.Should().BeTrue();
        viewModel.HasRows.Should().BeFalse();
    }

    [Fact]
    public async Task LoadCommand_RequestsARowLimitFromTheStoreContract()
    {
        // Arrange
        _store.GetRecentAsync(Arg.Any<int>()).Returns([]);
        var viewModel = CreateViewModel();

        // Act
        await viewModel.LoadCommand.Execute(ReactiveUI.Primitives.RxVoid.Default);

        // Assert: the view asks for a positive, bounded batch, not zero.
        await _store.Received(1).GetRecentAsync(Arg.Is<int>(v => v > 0));
    }

    /// <summary>
    /// The fee column (T-080): a filled order prices its recorded commission
    /// invariant-formatted like the other money cells, an order without fee
    /// data shows the localized dash — an honest unknown, never a zero.
    /// </summary>
    [Fact]
    public async Task LoadCommand_WithFeeData_FormatsTheCommission_AndUnknownsTheAbsence()
    {
        // Arrange: two filled orders — one with a recorded commission (the
        // priced schedule answer), one without (the adapter published none).
        var localOffset = TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 1, 1, 12, 0, 0));
        _store.GetRecentAsync(Arg.Any<int>())
            .Returns(
            [
                BuildOrder("paper-fee", OrderStatus.Filled, new DateTimeOffset(2026, 1, 1, 12, 5, 0, localOffset), commission: 0.5m),
                BuildOrder("paper-no-fee", OrderStatus.Filled, new DateTimeOffset(2026, 1, 1, 12, 0, 0, localOffset)),
            ]);

        var viewModel = CreateViewModel();

        // Act
        await viewModel.LoadCommand.Execute(ReactiveUI.Primitives.RxVoid.Default);

        // Assert
        viewModel.Rows[0].Fee.Should().Be("0.50000000", "the recorded commission is invariant-formatted");
        viewModel.Rows[1].Fee.Should().Be("[Fees.Unknown]", "the fee absence is the dash");
    }

    #region Export Flows

    /// <summary>Rsx-fake message form: `[key:first-argument]`.</summary>
    private static string Localized(string key, params object[] args) =>
        $"[{key}:{string.Join("|", args)}]";

    [Fact]
    public async Task ExportOrdersCommand_WithAConfirmedDestination_StreamsExactlyTheLoadedRows()
    {
        // Arrange: a loaded journal of two rows the export must mirror 1:1.
        var orders = new[]
        {
            BuildOrder("paper-1", OrderStatus.Filled, new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero)),
            BuildOrder("paper-2", OrderStatus.Cancelled, new DateTimeOffset(2026, 1, 1, 12, 5, 0, TimeSpan.Zero)),
        };
        _store.GetRecentAsync(Arg.Any<int>()).Returns(orders);

        var export = Substitute.For<ZettaTrade.Infrastructure.Export.ICsvExportService>();
        IReadOnlyList<Order> exportedOrders = null!;
        string? exportedPath = null;
        export.ExportOrdersAsync(
                Arg.Do<string>(path => exportedPath = path),
                Arg.Do<IReadOnlyList<Order>>(rows => exportedOrders = rows),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var viewModel = CreateViewModel(export);
        await viewModel.LoadCommand.Execute(ReactiveUI.Primitives.RxVoid.Default);

        string? requestedName = null;
        using var handler = viewModel.SaveOrdersCsvRequested.RegisterHandler(context =>
        {
            requestedName = context.Input;
            context.SetOutput(@"C:\exports\orders.csv");
            return Task.CompletedTask;
        });

        // Act
        await viewModel.ExportOrdersCommand.Execute(ReactiveUI.Primitives.RxVoid.Default);

        // Assert: the write receives exactly the loaded set at the confirmed path.
        requestedName.Should().Be("orders.csv", "the dialog carries the suggested name");
        exportedPath.Should().Be(@"C:\exports\orders.csv");
        exportedOrders.Should().BeSameAs(orders, "the export set is exactly the loaded journal rows");
        viewModel.ErrorMessage.Should().BeNull();
        viewModel.HasErrorMessage.Should().BeFalse();
    }

    [Fact]
    public async Task ExportFillsCommand_StreamsTheSameLoadedRows()
    {
        // Arrange: the fills CSV derives from the journal rows, not from a
        // second source — a copy-paste slip feeding it a different set must
        // fail this fact.
        var orders = new[]
        {
            BuildOrder("paper-1", OrderStatus.Filled, new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero)),
        };
        _store.GetRecentAsync(Arg.Any<int>()).Returns(orders);

        var export = Substitute.For<ZettaTrade.Infrastructure.Export.ICsvExportService>();
        IReadOnlyList<Order> exportedOrders = null!;
        export.ExportFillsAsync(
                Arg.Any<string>(),
                Arg.Do<IReadOnlyList<Order>>(rows => exportedOrders = rows),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var viewModel = CreateViewModel(export);
        await viewModel.LoadCommand.Execute(ReactiveUI.Primitives.RxVoid.Default);

        using var handler = viewModel.SaveFillsCsvRequested.RegisterHandler(context =>
        {
            context.SetOutput(@"C:\exports\fills.csv");
            return Task.CompletedTask;
        });

        // Act
        await viewModel.ExportFillsCommand.Execute(ReactiveUI.Primitives.RxVoid.Default);

        // Assert
        exportedOrders.Should().BeSameAs(orders);
        viewModel.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task ExportOrdersCommand_WithACancelledDialog_EndsQuietly()
    {
        // Arrange
        _store.GetRecentAsync(Arg.Any<int>()).Returns([]);
        var export = Substitute.For<ZettaTrade.Infrastructure.Export.ICsvExportService>();
        var viewModel = CreateViewModel(export);
        await viewModel.LoadCommand.Execute(ReactiveUI.Primitives.RxVoid.Default);

        string? requestedName = null;
        using var handler = viewModel.SaveOrdersCsvRequested.RegisterHandler(context =>
        {
            requestedName = context.Input;
            context.SetOutput(null);
            return Task.CompletedTask;
        });

        // Act
        await viewModel.ExportOrdersCommand.Execute(ReactiveUI.Primitives.RxVoid.Default);

        // Assert: a cancelled dialog is not an error, and nothing was written.
        requestedName.Should().Be("orders.csv");
        await export.DidNotReceive().ExportOrdersAsync(
            Arg.Any<string>(),
            Arg.Any<IReadOnlyList<Order>>(),
            Arg.Any<CancellationToken>());
        viewModel.ErrorMessage.Should().BeNull();
        viewModel.HasErrorMessage.Should().BeFalse();
    }

    [Fact]
    public async Task ExportOrdersCommand_WhenTheServiceThrows_ShowsTheLocalizedFault()
    {
        // Arrange
        _store.GetRecentAsync(Arg.Any<int>()).Returns(
        [
            BuildOrder("paper-1", OrderStatus.Filled, new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero)),
        ]);
        var export = Substitute.For<ZettaTrade.Infrastructure.Export.ICsvExportService>();
        export.ExportOrdersAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<Order>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException(
                new InvalidOperationException("directory is gone")));

        var viewModel = CreateViewModel(export);
        await viewModel.LoadCommand.Execute(ReactiveUI.Primitives.RxVoid.Default);

        using var handler = viewModel.SaveOrdersCsvRequested.RegisterHandler(context =>
        {
            context.SetOutput(@"C:\exports\orders.csv");
            return Task.CompletedTask;
        });

        // Act
        await viewModel.ExportOrdersCommand.Execute(ReactiveUI.Primitives.RxVoid.Default);

        // Assert: the localized fault carries the exception message.
        viewModel.ErrorMessage.Should().Be(Localized("History.ExportFailed", "directory is gone"));
        viewModel.HasErrorMessage.Should().BeTrue();
    }

    [Fact]
    public async Task ExportOrdersCommand_AfterAStaleError_ACancelledDialogClearsTheErrorLine()
    {
        // Arrange: a failed load anchored the error line first.
        _store.GetRecentAsync(Arg.Any<int>())
            .Returns(_ => Task.FromException<IReadOnlyList<Order>>(
                new InvalidOperationException("database is locked")));
        var export = Substitute.For<ZettaTrade.Infrastructure.Export.ICsvExportService>();
        var viewModel = CreateViewModel(export);

        await viewModel.LoadCommand.Execute(ReactiveUI.Primitives.RxVoid.Default);
        viewModel.ErrorMessage.Should().Be(Localized("History.LoadFailed", "database is locked"));

        using var handler = viewModel.SaveOrdersCsvRequested.RegisterHandler(context =>
        {
            context.SetOutput(null);
            return Task.CompletedTask;
        });

        // Act: the fresh export flow clears the line before its outcome — a
        // cancelled dialog lands quietly on top of the stale load error.
        await viewModel.ExportOrdersCommand.Execute(ReactiveUI.Primitives.RxVoid.Default);

        // Assert
        await export.DidNotReceive().ExportOrdersAsync(
            Arg.Any<string>(),
            Arg.Any<IReadOnlyList<Order>>(),
            Arg.Any<CancellationToken>());
        viewModel.ErrorMessage.Should().BeNull();
        viewModel.HasErrorMessage.Should().BeFalse();
    }

    #endregion Export Flows
}

