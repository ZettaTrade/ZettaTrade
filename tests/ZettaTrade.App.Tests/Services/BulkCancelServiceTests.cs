namespace ZettaTrade.App.Tests.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Livery.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using ZettaTrade.App.Services;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.Infrastructure.Exchange;
using ZettaTrade.Infrastructure.Persistence;

/// <summary>
/// Tests for the bulk cancel service: a mixed working/cancelled set cancels
/// ONLY the working orders on the workspace's active symbol (every command
/// carrying its own symbol and id — the Symbol-first contract), one adapter
/// fault turns into that item's report and never stops the others, one
/// confirmation gates the whole pass (a refusal leaves everything
/// untouched), rows already in flight are skipped and reported rather than
/// double-commanded, and every dead end (no workspace symbol, nothing
/// working, a failed fresh read) lands as an explicit localized answer.
/// </summary>
public sealed class BulkCancelServiceTests
{
    #region Test Constants

    private static readonly Symbol BtcUsdt = Symbol.Parse("BTC/USDT");

    /// <summary>The order's own price: every order carries an anchor, so the notional is computable.</summary>
    private const decimal OrderPrice = 50_000m;

    #endregion Test Constants

    #region Test Fields

    private readonly ITradingAdapter _trading = Substitute.For<ITradingAdapter>();
    private readonly IConfirmationService _confirmation = Substitute.For<IConfirmationService>();
    private readonly ILocalizationManager _localization = Substitute.For<ILocalizationManager>();
    private readonly IOrderHistoryStore _history = Substitute.For<IOrderHistoryStore>();
    private readonly BehaviorSubject<Symbol> _workspaceSymbols = new(Symbol.Parse("BTC/USDT"));
    private readonly List<Order> _openOrders = [];

    #endregion Test Fields

    public BulkCancelServiceTests()
    {
        // The fake resolves dynamic messages in a predictable, test-visible
        // form: the key plus the arguments, e.g. "[Positions.BulkCancelNothing:BTC/USDT]".
        _localization.GetString(Arg.Any<string>()).Returns(call =>
            $"[{call.ArgAt<string>(0)}]");
        _localization.GetString(Arg.Any<string>(), Arg.Any<object[]>()).Returns(call =>
            $"[{call.ArgAt<string>(0)}:{FormatArgs(call.ArgAt<object[]>(1))}]");

        // The confirmation opens by default; the gate tests rewire it.
        _confirmation
            .RequestConfirmationAsync(Arg.Any<ConfirmationRequest>(), Arg.Any<CancellationToken>())
            .Returns(true);

        // The venue tags: the shipped paper ids (the tag the composer embeds).
        _trading.Id.Returns(PaperTradingAdapter.ExchangeId);
        _trading.Name.Returns(PaperTradingAdapter.ExchangeName);

        _trading.GetOpenOrdersAsync().Returns(_ => [.. _openOrders]);
    }

    #region Golden

    [Fact]
    public async Task MixedSet_CancelsOnlyTheWorkingOrdersOnTheSymbol()
    {
        // Arrange: a deliberately mixed set — two working on BTC/USDT, two
        // terminal on BTC/USDT, one working on another symbol.
        _openOrders.Add(BuildOrder("paper-1", OrderStatus.Pending));
        _openOrders.Add(BuildOrder("paper-2", OrderStatus.PartiallyFilled, filled: 0.004m));
        _openOrders.Add(BuildOrder("paper-3", OrderStatus.Filled));
        _openOrders.Add(BuildOrder("paper-4", OrderStatus.Cancelled));
        _openOrders.Add(BuildOrder("paper-5", OrderStatus.Pending, "ETH/USDT"));
        var service = CreateService();

        // Act
        var result = await service.CancelWorkingOrdersAsync([]);

        // Assert: DONE, and exactly the two working BTC/USDT orders were
        // commanded — each with its own symbol and id.
        result.Outcome.Should().Be(BulkCancelOutcome.Done);
        result.Reports.Should().HaveCount(2);
        result.Reports.Select(r => r.Outcome).Should().OnlyContain(o => o == BulkCancelReportOutcome.Cancelled);
        result.ResultMessage.Should()
            .Contain("Positions.BulkCancelDone")
            .And.Contain("[Positions.BulkCancelDone:BTC/USDT|2|0|0|0|.]");
        await _trading.Received(1).CancelOrderAsync(
            BtcUsdt, "paper-1", Arg.Any<CancellationToken>());
        await _trading.Received(1).CancelOrderAsync(
            BtcUsdt, "paper-2", Arg.Any<CancellationToken>());
        await _trading.DidNotReceive().CancelOrderAsync(
            Arg.Any<Symbol>(), "paper-3", Arg.Any<CancellationToken>());
        await _trading.DidNotReceive().CancelOrderAsync(
            Arg.Any<Symbol>(), "paper-4", Arg.Any<CancellationToken>());
        await _trading.DidNotReceive().CancelOrderAsync(
            Arg.Any<Symbol>(), "paper-5", Arg.Any<CancellationToken>());

        // The history recorded both cancellations (the order's own venue id).
        await _history.Received(1).MarkCancelledAsync(
            "paper", "paper-1", Arg.Any<DateTimeOffset>());
        await _history.Received(1).MarkCancelledAsync(
            "paper", "paper-2", Arg.Any<DateTimeOffset>());
    }

    [Fact]
    public async Task Confirmations_ComposesCountNotionalAndThePaperTag()
    {
        // Arrange: 0.01 remaining + (0.01 - 0.004) remaining = 0.016 @ 50000.
        _openOrders.Add(BuildOrder("paper-1", OrderStatus.Pending));
        _openOrders.Add(BuildOrder("paper-2", OrderStatus.PartiallyFilled, filled: 0.004m));
        ConfirmationRequest? composed = null;
        _confirmation
            .RequestConfirmationAsync(Arg.Do<ConfirmationRequest>(r => composed = r),
                Arg.Any<CancellationToken>())
            .Returns(true);
        var service = CreateService();

        // Act
        await service.CancelWorkingOrdersAsync([]);

        // Assert: symbol, count, remaining notional, quote asset, paper tag.
        composed.Should().NotBeNull();
        composed!.MessageKey.Should().Be("Positions.ConfirmBulkCancelFormat");
        string[] arguments = [.. composed.Arguments.Select(a => a.ToString() ?? string.Empty)];
        arguments.Should()
            .Equal(["BTC/USDT", "2", "800", "USDT", "[Positions.BulkTagPaper]"]);
    }

    #endregion Golden

    #region Best-effort reporting

    [Fact]
    public async Task PartialFault_CarriesOnWithTheRemainingOrders()
    {
        // Arrange: three working; the middle one's cancel dies on the wire.
        _openOrders.Add(BuildOrder("paper-1", OrderStatus.Pending));
        _openOrders.Add(BuildOrder("paper-2", OrderStatus.Pending));
        _openOrders.Add(BuildOrder("paper-3", OrderStatus.Pending));
        _trading
            .CancelOrderAsync(
                BtcUsdt,
                "paper-2",
                Arg.Any<CancellationToken>())
            .Returns(_ => throw new HttpRequestException("wire down"));
        var service = CreateService();

        // Act
        var result = await service.CancelWorkingOrdersAsync([]);

        // Assert: the neighbors cancelled across the fault; the fault became
        // exactly one honest report named in the result line.
        result.Outcome.Should().Be(BulkCancelOutcome.Done);
        result.Reports.Select(r => (r.OrderId, r.Outcome)).Should().Equal(
            ("paper-1", BulkCancelReportOutcome.Cancelled),
            ("paper-2", BulkCancelReportOutcome.Failed),
            ("paper-3", BulkCancelReportOutcome.Cancelled));
        result.Reports.Single(r => r.Outcome == BulkCancelReportOutcome.Failed).Message
            .Should().Contain("wire down");
        result.ResultMessage.Should()
            .Contain("Positions.BulkCancelDone")
            .And.Contain("Positions.BulkCancelFailedItems")
            .And.Contain("paper-2");
        await _history.Received(2).MarkCancelledAsync(
            "paper", Arg.Is<string>(id => id == "paper-1" || id == "paper-3"), Arg.Any<DateTimeOffset>());
    }

    [Fact]
    public async Task PersistenceFault_NeverTurnsThePassIntoAFailure()
    {
        // Arrange: one working order; the history write dies.
        _openOrders.Add(BuildOrder("paper-1", OrderStatus.Pending));
        _history
            .When(store => store.MarkCancelledAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<DateTimeOffset>()))
            .Throw(new InvalidOperationException("db down"));
        var service = CreateService();

        // Act
        var result = await service.CancelWorkingOrdersAsync([]);

        // Assert: the adapter already cancelled it; the pass stays Done.
        result.Outcome.Should().Be(BulkCancelOutcome.Done);
        result.Reports.Should().ContainSingle(r => r.Outcome == BulkCancelReportOutcome.Cancelled);
        result.ErrorMessage.Should().BeNull();
        await _trading.Received(1).CancelOrderAsync(
            BtcUsdt, "paper-1", Arg.Any<CancellationToken>());
    }

    #endregion Best-effort reporting

    #region Confirmation gate

    [Fact]
    public async Task RefusedConfirmation_LeavesEveryOrderUntouched()
    {
        // Arrange
        _openOrders.Add(BuildOrder("paper-1", OrderStatus.Pending));
        _openOrders.Add(BuildOrder("paper-2", OrderStatus.Pending));
        _confirmation
            .RequestConfirmationAsync(Arg.Any<ConfirmationRequest>(), Arg.Any<CancellationToken>())
            .Returns(false);
        var service = CreateService();

        // Act
        var result = await service.CancelWorkingOrdersAsync([]);

        // Assert: no commands, no history writes, silence for the panel.
        result.Outcome.Should().Be(BulkCancelOutcome.Declined);
        result.IsDeclined.Should().BeTrue();
        result.Reports.Should().BeEmpty();
        await _trading.DidNotReceive().CancelOrderAsync(
            Arg.Any<Symbol>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _history.DidNotReceive().MarkCancelledAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>());
    }

    #endregion Confirmation gate

    #region In-flight rows

    [Fact]
    public async Task RowsAlreadyInFlight_AreSkippedAndReported_NeverDoubleCommanded()
    {
        // Arrange: three working; the middle one has a per-row flow running.
        _openOrders.Add(BuildOrder("paper-1", OrderStatus.Pending));
        _openOrders.Add(BuildOrder("paper-2", OrderStatus.Pending));
        _openOrders.Add(BuildOrder("paper-3", OrderStatus.Pending));
        var service = CreateService();

        // Act
        var result = await service.CancelWorkingOrdersAsync(["paper-2"]);

        // Assert: only the two free neighbors were commanded; the in-flight
        // row is its own honest skip.
        result.Outcome.Should().Be(BulkCancelOutcome.Done);
        result.Reports.Select(r => (r.OrderId, r.Outcome)).Should().Equal(
            ("paper-1", BulkCancelReportOutcome.Cancelled),
            ("paper-2", BulkCancelReportOutcome.SkippedInFlight),
            ("paper-3", BulkCancelReportOutcome.Cancelled));
        result.ResultMessage.Should()
            .Contain("[Positions.BulkCancelDone:BTC/USDT|2|1|0|0|.]");
        await _trading.DidNotReceive().CancelOrderAsync(
            Arg.Any<Symbol>(), "paper-2", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EverythingWorkingInFlight_DoneWithoutSendingAnything()
    {
        // Arrange: one working order; its per-row flow owns it.
        _openOrders.Add(BuildOrder("paper-1", OrderStatus.Pending));
        var service = CreateService();

        // Act
        var result = await service.CancelWorkingOrdersAsync(["paper-1"]);

        // Assert: the skip is reported, no commands, no confirmation.
        result.Outcome.Should().Be(BulkCancelOutcome.Done);
        result.Reports.Should().ContainSingle(r => r.Outcome == BulkCancelReportOutcome.SkippedInFlight);
        result.ResultMessage.Should()
            .Contain("[Positions.BulkCancelDone:BTC/USDT|0|1|0|0|.]");
        await _confirmation.DidNotReceive().RequestConfirmationAsync(
            Arg.Any<ConfirmationRequest>(), Arg.Any<CancellationToken>());
        await _trading.DidNotReceive().CancelOrderAsync(
            Arg.Any<Symbol>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    #endregion In-flight rows

    #region Dead ends

    [Fact]
    public async Task NoWorkspaceSymbol_IsAnExplicitLocalizedRejection()
    {
        // Arrange: a workspace that never confirmed a symbol.
        var service = CreateService(Observable.Never<Symbol>());

        // Act
        var result = await service.CancelWorkingOrdersAsync([]);

        // Assert: nothing read, nothing commanded, one localized answer.
        result.Outcome.Should().Be(BulkCancelOutcome.Failed);
        result.Symbol.Should().BeNull();
        result.ErrorMessage.Should().Be("[Positions.BulkNoSymbol]");
        await _trading.DidNotReceive().GetOpenOrdersAsync();
    }

    [Fact]
    public async Task NothingWorking_IsAnExplicitLocalizedAnswer()
    {
        // Arrange: the symbol's set is already terminal.
        _openOrders.Add(BuildOrder("paper-1", OrderStatus.Filled));
        _openOrders.Add(BuildOrder("paper-2", OrderStatus.Cancelled));
        var service = CreateService();

        // Act
        var result = await service.CancelWorkingOrdersAsync([]);

        // Assert: no confirmation, no commands, the honest empty state.
        result.Outcome.Should().Be(BulkCancelOutcome.Failed);
        result.ErrorMessage.Should().Be("[Positions.BulkCancelNothing:BTC/USDT]");
        await _trading.DidNotReceive().CancelOrderAsync(
            Arg.Any<Symbol>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _confirmation.DidNotReceive().RequestConfirmationAsync(
            Arg.Any<ConfirmationRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FreshReadFault_IsALocalizedAbortedAnswer()
    {
        // Arrange
        _openOrders.Add(BuildOrder("paper-1", OrderStatus.Pending));
        _trading
            .GetOpenOrdersAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<IReadOnlyList<Order>>(new HttpRequestException("wire down")));
        var service = CreateService();

        // Act
        var result = await service.CancelWorkingOrdersAsync([]);

        // Assert: the failure carries the fault text, nothing was commanded.
        result.Outcome.Should().Be(BulkCancelOutcome.Failed);
        result.ErrorMessage.Should()
            .Contain("Positions.BulkCancelAborted")
            .And.Contain("wire down");
        await _trading.DidNotReceive().CancelOrderAsync(
            Arg.Any<Symbol>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _history.DidNotReceive().MarkCancelledAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>());
    }

    #endregion Dead ends

    #region Test Helpers

    private BulkCancelService CreateService() => CreateService(_workspaceSymbols);

    private BulkCancelService CreateService(IObservable<Symbol> workspaceSymbol) => new(
        _trading,
        _confirmation,
        new OrderConfirmationComposer(_localization),
        new WorkspaceSymbolService(workspaceSymbol),
        _history,
        _localization,
        NullLogger<BulkCancelService>.Instance);

    private static Order BuildOrder(
        string id,
        OrderStatus status,
        string symbol = "BTC/USDT",
        decimal filled = decimal.Zero) => new()
        {
            Id = id,
            ExchangeId = "paper",
            Symbol = Symbol.Parse(symbol),
            Side = OrderSide.Buy,
            Type = OrderType.Limit,
            Quantity = new Quantity(0.01m),
            Price = new Price(OrderPrice),
            Status = status,
            TimeInForce = TimeInForce.GTC,
            FilledQuantity = new Quantity(filled),
            CreatedAt = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero),
        };

    private static string FormatArgs(object[] args) => string.Join("|", args);

    #endregion Test Helpers
}
