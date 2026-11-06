namespace ZettaTrade.App.Tests.ViewModels;

using System;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Livery.Localization;
using NSubstitute;
using Xunit;
using ZettaTrade.App.Services;
using ZettaTrade.App.ViewModels;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.Infrastructure.Persistence;

/// <summary>
/// Tests for the positions panel: loading populates all three sections with
/// precomputed display rows, and every cancellation goes through the
/// confirmation and passes the order's own symbol and id to the adapter
/// (the Symbol-first contract). An order not in an active status is never
/// cancellable. Every load or cancel fault reaches the user as a localized
/// error line (no silently stale sections, no unobserved command
/// exceptions), and one order renders the same wall-clock time in the panel
/// and in the order history window.
/// </summary>
public sealed class PositionsPanelViewModelTests
{
    private readonly ITradingAdapter _trading = Substitute.For<ITradingAdapter>();
    private readonly IConfirmationService _confirmation = Substitute.For<IConfirmationService>();
    private readonly ILocalizationManager _localization = Substitute.For<ILocalizationManager>();
    private readonly IOrderJournal _journal = new OrderJournal();
    private readonly IOrderHistoryStore _history = Substitute.For<IOrderHistoryStore>();

    public PositionsPanelViewModelTests()
    {
        // The fake resolves dynamic messages in a predictable, test-visible
        // form: the key plus the first argument, e.g. "[Positions.Cancelled:paper-1]".
        _localization.GetString(Arg.Any<string>()).Returns(call =>
            $"[{call.ArgAt<string>(0)}]");
        _localization.GetString(Arg.Any<string>(), Arg.Any<object[]>()).Returns(call =>
            $"[{call.ArgAt<string>(0)}:{FormatArgs(call.ArgAt<object[]>(1))}]");
    }

    private PositionsPanelViewModel CreateViewModel() =>
        new(
            _trading,
            _journal,
            _history,
            _confirmation,
            new OrderConfirmationComposer(_localization),
            _localization,
            Substitute.For<Microsoft.Extensions.Logging.ILogger<PositionsPanelViewModel>>());

    private static Order BuildOrder(string id, OrderStatus status, string symbol = "BTC/USDT") => new()
    {
        Id = id,
        ExchangeId = "paper",
        Symbol = Symbol.Parse(symbol),
        Side = OrderSide.Buy,
        Type = OrderType.Limit,
        Quantity = new Quantity(0.01m),
        Price = new Price(50_000m),
        Status = status,
        TimeInForce = TimeInForce.GTC,
        CreatedAt = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero),
    };

    private static Position BuildPosition(string symbol) => new()
    {
        Id = "pos-1",
        ExchangeId = "paper",
        Symbol = Symbol.Parse(symbol),
        Side = OrderSide.Buy,
        Quantity = new Quantity(0.01m),
        EntryPrice = new Price(50_000m),
        CurrentPrice = new Price(50_100m),
        OpenedAt = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero),
    };

    [Fact]
    public async Task LoadCommand_WithData_PopulatesAllSections()
    {
        // Arrange
        var viewModel = CreateViewModel();
        _journal.RecordPlaced(BuildOrder("paper-1", OrderStatus.Filled));
        _journal.RecordPlaced(BuildOrder("paper-2", OrderStatus.Pending, "ETH/USDT"));
        _trading.GetOpenOrdersAsync().Returns([BuildOrder("paper-2", OrderStatus.Pending, "ETH/USDT")]);
        _trading.GetPositionsAsync().Returns([BuildPosition("BTC/USDT")]);

        // Act
        await viewModel.LoadCommand.Execute(ReactiveUI.Primitives.RxVoid.Default);

        // Assert: positions carry localized, precomputed display values. The
        // time expectation derives from the same fixture the row built from
        // and converts to the local zone — zone-agnostic on any runner.
        var expectedOpened = BuildPosition("BTC/USDT").OpenedAt
            .ToLocalTime()
            .ToString(TimeFormats.OrderTimestamp, CultureInfo.InvariantCulture);
        viewModel.Positions.Should().HaveCount(1);
        viewModel.Positions[0].Symbol.Should().Be("BTC/USDT");
        viewModel.Positions[0].Side.Should().Be("[Order.Buy]");
        viewModel.Positions[0].Quantity.Should().Be("0.01");
        viewModel.Positions[0].EntryPrice.Should().Be("50000.00000000");
        viewModel.Positions[0].Opened.Should().Be(expectedOpened);
        viewModel.HasPositions.Should().BeTrue();

        viewModel.OpenOrders.Should().HaveCount(1);
        viewModel.OpenOrders[0].OrderId.Should().Be("paper-2");
        viewModel.OpenOrders[0].Symbol.Should().Be("ETH/USDT");
        viewModel.OpenOrders[0].Status.Should().Be("[Positions.StatusPending]");
        viewModel.OpenOrders[0].Price.Should().Be("50000.00000000");
        viewModel.HasOpenOrders.Should().BeTrue();

        // The journal snapshot is newest first.
        viewModel.RecentOrders.Should().HaveCount(2);
        viewModel.RecentOrders[0].OrderId.Should().Be("paper-2");
        viewModel.RecentOrders[1].OrderId.Should().Be("paper-1");
        viewModel.RecentOrders[1].Status.Should().Be("[Positions.StatusFilled]");
        viewModel.HasRecentOrders.Should().BeTrue();
    }

    [Fact]
    public async Task LoadCommand_WithEmptyAdapterAndJournal_ShowsEmptyFlagsOnly()
    {
        // Arrange
        var viewModel = CreateViewModel();
        _trading.GetOpenOrdersAsync().Returns([]);
        _trading.GetPositionsAsync().Returns([]);

        // Act
        await viewModel.LoadCommand.Execute(ReactiveUI.Primitives.RxVoid.Default);

        // Assert: every section is empty, all empty-states visible.
        viewModel.Positions.Should().BeEmpty();
        viewModel.OpenOrders.Should().BeEmpty();
        viewModel.RecentOrders.Should().BeEmpty();
        viewModel.HasPositions.Should().BeFalse();
        viewModel.HasOpenOrders.Should().BeFalse();
        viewModel.HasRecentOrders.Should().BeFalse();
        viewModel.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task LoadCommand_WhenAdapterThrows_ShowsLoadFailedError()
    {
        // Arrange
        var viewModel = CreateViewModel();
        _trading.GetOpenOrdersAsync().Returns([]);
        _trading.GetPositionsAsync().Returns(_ =>
            Task.FromException<IReadOnlyList<Position>>(new InvalidOperationException("adapter offline")));

        // Act
        await viewModel.LoadCommand.Execute(ReactiveUI.Primitives.RxVoid.Default);

        // Assert
        viewModel.ErrorMessage.Should().Contain("Positions.LoadFailed").And.Contain("adapter offline");
        viewModel.HasErrorMessage.Should().BeTrue();
    }

    /// <summary>
    /// BUG-054: a load failure is not limited to validation refusals — the
    /// read reaches the trading adapter over the network, so it can surface
    /// as an <see cref="HttpRequestException"/> or as a
    /// <see cref="TaskCanceledException"/> (the HttpClient timeout's shape).
    /// Every one of them lands as the localized error line instead of
    /// escaping into the unobserved ThrownExceptions (silently stale or
    /// empty sections).
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LoadCommand_WhenTheNetworkFails_ShowsLoadFailedError(bool isConnectionFault)
    {
        var viewModel = CreateViewModel();
        _trading.GetOpenOrdersAsync().Returns([]);
        _trading.GetPositionsAsync().Returns(_ => isConnectionFault
            ? Task.FromException<IReadOnlyList<Position>>(new HttpRequestException("wire down"))
            : Task.FromException<IReadOnlyList<Position>>(new TaskCanceledException("the request timed out")));

        await viewModel.LoadCommand.Execute(ReactiveUI.Primitives.RxVoid.Default);

        viewModel.ErrorMessage.Should().Contain("Positions.LoadFailed");
        viewModel.HasErrorMessage.Should().BeTrue();
        viewModel.Positions.Should().BeEmpty("a failed load reports instead of showing stale data");
    }

    /// <summary>
    /// BUG-054: the cancellation request rides the same network surface and
    /// can die on the wire; the panel reports it localized, keeps the
    /// success line clear and never writes the terminal history state.
    /// </summary>
    [Fact]
    public async Task CancelCommand_WhenTheNetworkFails_ShowsCancelFailedError()
    {
        var viewModel = CreateViewModel();
        _journal.RecordPlaced(BuildOrder("paper-1", OrderStatus.Pending, "ETH/USDT"));
        _trading.GetOpenOrdersAsync().Returns([]);
        _trading.GetPositionsAsync().Returns([]);
        _trading.CancelOrderAsync(Arg.Any<Symbol>(), Arg.Any<string>())
            .Returns(_ => Task.FromException(new HttpRequestException("wire down")));
        _confirmation.RequestConfirmationAsync(Arg.Any<ConfirmationRequest>()).Returns(true);

        await viewModel.LoadCommand.Execute(ReactiveUI.Primitives.RxVoid.Default);

        await viewModel.RecentOrders.Single().CancelCommand.Execute(ReactiveUI.Primitives.RxVoid.Default);

        viewModel.ErrorMessage.Should().Contain("Positions.CancelFailed").And.Contain("wire down");
        viewModel.ResultMessage.Should().BeNull();
        await _history.DidNotReceive().MarkCancelledAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>());
    }

    [Fact]
    public async Task CancelCommand_WhenConfirmed_PassesOrderSymbolAndIdToAdapter()
    {
        // Arrange: the journal holds a filled (not cancellable) BTC order and
        // a pending ETH order; cancelling must hit the ETH order's own symbol
        // and id — never a substituted or defaulted one.
        var viewModel = CreateViewModel();
        _journal.RecordPlaced(BuildOrder("paper-2", OrderStatus.Filled));
        _journal.RecordPlaced(BuildOrder("paper-1", OrderStatus.Pending, "ETH/USDT"));
        _trading.GetOpenOrdersAsync().Returns([]);
        _trading.GetPositionsAsync().Returns([]);

        var capturedSymbol = (Symbol?)null;
        var capturedId = (string?)null;
        _trading.CancelOrderAsync(
                Arg.Do<Symbol>(s => capturedSymbol = s),
                Arg.Do<string>(id => capturedId = id))
            .Returns(Task.CompletedTask);

        var confirmationRequest = (ConfirmationRequest?)null;
        _confirmation.RequestConfirmationAsync(Arg.Do<ConfirmationRequest>(cr => confirmationRequest = cr))
            .Returns(true);

        await viewModel.LoadCommand.Execute(ReactiveUI.Primitives.RxVoid.Default);
        var row = viewModel.RecentOrders.Single(r => r.OrderId == "paper-1");

        // Act
        await row.CancelCommand.Execute(ReactiveUI.Primitives.RxVoid.Default);

        // Assert: symbol-first adapter contract — the order's own symbol and id.
        capturedSymbol.Should().Be(Symbol.Parse("ETH/USDT"));
        capturedId.Should().Be("paper-1");

        confirmationRequest.Should().NotBeNull();
        confirmationRequest!.MessageKey.Should().Be("Positions.ConfirmCancelFormat");
        confirmationRequest.Arguments[1].Should().Be("paper-1");

        viewModel.ResultMessage.Should().Contain("Positions.Cancelled").And.Contain("paper-1");
        viewModel.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task CancelCommand_WhenConfirmed_MarksOrderCancelledInHistoryStore()
    {
        // Arrange: after the adapter confirms the cancellation the persistent
        // SQLite journal must carry the terminal state for restarts.
        var viewModel = CreateViewModel();
        _journal.RecordPlaced(BuildOrder("paper-1", OrderStatus.Pending, "ETH/USDT"));
        _trading.GetOpenOrdersAsync().Returns([]);
        _trading.GetPositionsAsync().Returns([]);

        var capturedExchangeId = (string?)null;
        var capturedOrderId = (string?)null;
        _history.MarkCancelledAsync(
                Arg.Do<string>(exchangeId => capturedExchangeId = exchangeId),
                Arg.Do<string>(orderId => capturedOrderId = orderId),
                Arg.Any<DateTimeOffset>())
            .Returns(Task.CompletedTask);
        _confirmation.RequestConfirmationAsync(Arg.Any<ConfirmationRequest>()).Returns(true);

        await viewModel.LoadCommand.Execute(ReactiveUI.Primitives.RxVoid.Default);

        // Act
        await viewModel.RecentOrders.Single().CancelCommand.Execute(ReactiveUI.Primitives.RxVoid.Default);

        // Assert: the order's own exchange and id, no substitution.
        capturedExchangeId.Should().Be("paper");
        capturedOrderId.Should().Be("paper-1");
        viewModel.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task CancelCommand_WhenHistoryStoreFails_CancellationStillSucceeds()
    {
        // Arrange: persistence is best effort — a journal fault must never
        // undo or fail a cancellation the adapter already performed.
        _journal.RecordPlaced(BuildOrder("paper-1", OrderStatus.Pending, "ETH/USDT"));
        _trading.GetOpenOrdersAsync().Returns([]);
        _trading.GetPositionsAsync().Returns([]);
        _history.MarkCancelledAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>())
            .Returns(_ => Task.FromException(new InvalidOperationException("disk full")));
        _confirmation.RequestConfirmationAsync(Arg.Any<ConfirmationRequest>()).Returns(true);

        var viewModel = CreateViewModel();
        await viewModel.LoadCommand.Execute(ReactiveUI.Primitives.RxVoid.Default);

        // Act
        await viewModel.RecentOrders.Single().CancelCommand.Execute(ReactiveUI.Primitives.RxVoid.Default);

        // Assert: localized success, no stale error.
        viewModel.ResultMessage.Should().Contain("Positions.Cancelled").And.Contain("paper-1");
        viewModel.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task CancelCommand_WhenUserDeclines_NeverTouchesAdapter()
    {
        // Arrange
        var viewModel = CreateViewModel();
        _journal.RecordPlaced(BuildOrder("paper-1", OrderStatus.Pending, "ETH/USDT"));
        _trading.GetOpenOrdersAsync().Returns([]);
        _trading.GetPositionsAsync().Returns([]);
        _confirmation.RequestConfirmationAsync(Arg.Any<ConfirmationRequest>()).Returns(false);

        await viewModel.LoadCommand.Execute(ReactiveUI.Primitives.RxVoid.Default);

        // Act
        await viewModel.RecentOrders.Single().CancelCommand.Execute(ReactiveUI.Primitives.RxVoid.Default);

        // Assert: the confirmation shows the cancel template, the adapter
        // stays untouched, and no outcome line is shown.
        await _trading.DidNotReceive().CancelOrderAsync(Arg.Any<Symbol>(), Arg.Any<string>());
        await _history.DidNotReceive().MarkCancelledAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>());
        viewModel.ResultMessage.Should().BeNull();
        viewModel.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task CancelCommand_WhenAdapterRefuses_ShowsLocalizedFailure()
    {
        // Arrange: the adapter refuses non-active statuses with a clear
        // InvalidOperationException; the panel surfaces it localized and
        // stays re-runnable.
        var viewModel = CreateViewModel();
        _journal.RecordPlaced(BuildOrder("paper-1", OrderStatus.Pending, "ETH/USDT"));
        _trading.GetOpenOrdersAsync().Returns([]);
        _trading.GetPositionsAsync().Returns([]);
        var confirmations = 0;
        _confirmation.RequestConfirmationAsync(Arg.Do<ConfirmationRequest>(_ => Interlocked.Increment(ref confirmations)))
            .Returns(true);
        _trading.CancelOrderAsync(Arg.Any<Symbol>(), Arg.Any<string>())
            .Returns(_ => Task.FromException(new InvalidOperationException("not cancellable in status Filled")));

        await viewModel.LoadCommand.Execute(ReactiveUI.Primitives.RxVoid.Default);

        // Act
        await viewModel.RecentOrders.Single().CancelCommand.Execute(ReactiveUI.Primitives.RxVoid.Default);

        // Assert
        viewModel.ErrorMessage.Should().Contain("Positions.CancelFailed").And.Contain("not cancellable");
        viewModel.ResultMessage.Should().BeNull();
        confirmations.Should().Be(1);
        await _history.DidNotReceive().MarkCancelledAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>());
    }

    [Fact]
    public async Task CancelCommand_SecondAttempt_ReplacesPreviousError()
    {
        // Arrange: the first attempt fails in the adapter, the second is
        // declined by the user; the stale error line must be replaced.
        var viewModel = CreateViewModel();
        _journal.RecordPlaced(BuildOrder("paper-1", OrderStatus.Pending, "ETH/USDT"));
        _trading.GetOpenOrdersAsync().Returns([]);
        _trading.GetPositionsAsync().Returns([]);
        _trading.CancelOrderAsync(Arg.Any<Symbol>(), Arg.Any<string>())
            .Returns(_ => Task.FromException(new InvalidOperationException("not cancellable")));
        var attempts = 0;
        _confirmation.RequestConfirmationAsync(Arg.Any<ConfirmationRequest>())
            .Returns(_ =>
            {
                attempts++;
                return attempts == 1;
            });

        await viewModel.LoadCommand.Execute(ReactiveUI.Primitives.RxVoid.Default);

        // Act 1: a failing cancellation leaves the localized error line.
        await viewModel.RecentOrders.Single().CancelCommand.Execute(ReactiveUI.Primitives.RxVoid.Default);
        viewModel.ErrorMessage.Should().Contain("Positions.CancelFailed");

        // Act 2: the user declines the next confirmation; before that the
        // flow resets its messages — so the adapter is reached exactly once.
        await viewModel.RecentOrders.Single().CancelCommand.Execute(ReactiveUI.Primitives.RxVoid.Default);

        // Assert
        viewModel.ErrorMessage.Should().BeNull();
        viewModel.ResultMessage.Should().BeNull();
        attempts.Should().Be(2);
        await _trading.Received(1).CancelOrderAsync(Arg.Any<Symbol>(), Arg.Any<string>());
    }

    [Theory]
    [InlineData(OrderStatus.Pending, true)]
    [InlineData(OrderStatus.Submitted, true)]
    [InlineData(OrderStatus.PartiallyFilled, true)]
    [InlineData(OrderStatus.Filled, false)]
    [InlineData(OrderStatus.Cancelled, false)]
    [InlineData(OrderStatus.Rejected, false)]
    [InlineData(OrderStatus.Expired, false)]
    public async Task OrderRows_AreCancellableOnlyInActiveStatuses(OrderStatus status, bool expected)
    {
        // Arrange: journal rows carry the status at placement time.
        var viewModel = CreateViewModel();
        _journal.RecordPlaced(BuildOrder("paper-1", status));
        _trading.GetOpenOrdersAsync().Returns([]);
        _trading.GetPositionsAsync().Returns([]);

        await viewModel.LoadCommand.Execute(ReactiveUI.Primitives.RxVoid.Default);

        // Act + Assert: both the flag and the command's can-execute gate.
        var row = viewModel.RecentOrders.Single();
        row.IsCancellable.Should().Be(expected);

        var canExecute = (bool?)null;
        row.CancelCommand.CanExecute
            .Subscribe(value => canExecute = value);
        canExecute.Should().Be(expected);
    }

    /// <summary>
    /// BUG-055: the same order must render the same wall-clock time in the
    /// positions panel and in the order history window — one
    /// UTC-to-local-time convention across the two windows (the panel used
    /// to print the raw UTC field). The assertion is zone-agnostic: both
    /// rows render in the runner's local zone and compare equal.
    /// </summary>
    [Fact]
    public async Task TheSameOrder_RendersTheSameOpenedTime_InThePanelAndTheHistory()
    {
        // Arrange: one fixed true-UTC order, visible to both consumers —
        // the panel reads the session journal, the history window reads the
        // persistent store.
        var order = BuildOrder("paper-1", OrderStatus.Pending, "ETH/USDT");
        _journal.RecordPlaced(order);
        _trading.GetOpenOrdersAsync().Returns([]);
        _trading.GetPositionsAsync().Returns([]);
        _history.GetRecentAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([order]);

        var viewModel = CreateViewModel();
        var history = new OrderHistoryViewModel(
            _history,
            _localization,
            Substitute.For<Microsoft.Extensions.Logging.ILogger<OrderHistoryViewModel>>());

        // Act: both windows load the same order.
        await viewModel.LoadCommand.Execute(ReactiveUI.Primitives.RxVoid.Default);
        await history.LoadCommand.Execute(ReactiveUI.Primitives.RxVoid.Default);

        // Assert: identical precomputed time strings.
        viewModel.RecentOrders.Single(row => row.OrderId == "paper-1").Opened
            .Should().Be(history.Rows.Single().Opened);
    }

    private static string FormatArgs(object[] args) => string.Join("|", args);
}

