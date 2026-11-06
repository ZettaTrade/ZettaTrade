namespace ZettaTrade.App.Tests.ViewModels;

using System;
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

    private OrderHistoryViewModel CreateViewModel() => new(
        _store,
        _localization,
        Substitute.For<Microsoft.Extensions.Logging.ILogger<OrderHistoryViewModel>>());

    private static Order BuildOrder(string id, OrderStatus status, DateTimeOffset createdAt) => new()
    {
        Id = id,
        ExchangeId = "paper",
        Symbol = Symbol.Parse("BTC/USDT"),
        Side = OrderSide.Sell,
        Type = OrderType.Market,
        Quantity = new Quantity(0.02m),
        Price = new Price(50_100m),
        Status = status,
        TimeInForce = TimeInForce.GTC,
        CreatedAt = createdAt,
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
}

