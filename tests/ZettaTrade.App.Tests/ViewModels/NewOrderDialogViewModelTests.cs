namespace ZettaTrade.App.Tests.ViewModels;

using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
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
/// Tests for the New Order dialog pipeline: every placement must pass risk
/// validation and human confirmation before reaching the trading adapter, a
/// rejected order never opens the confirmation dialog, and an unparsable
/// input surfaces a localized error instead of a defaulted number. The
/// symbol autocomplete additionally loads the adapter symbol list and
/// derives case-insensitive suggestions per keystroke (silently empty
/// offline, free-form input always usable).
/// </summary>
public sealed class NewOrderDialogViewModelTests
{
    private readonly ITradingAdapter _trading = Substitute.For<ITradingAdapter>();
    private readonly IRiskManager _riskManager = Substitute.For<IRiskManager>();
    private readonly IConfirmationService _confirmation = Substitute.For<IConfirmationService>();
    private readonly ILocalizationManager _localization = Substitute.For<ILocalizationManager>();
    private readonly IOrderJournal _journal = new OrderJournal();
    private readonly IOrderHistoryStore _history = Substitute.For<IOrderHistoryStore>();

    public NewOrderDialogViewModelTests()
    {
        // The fake resolves dynamic messages in a predictable, test-visible
        // form: the key plus the first argument, e.g. "NewOrder.OrderRejected:reason".
        _localization.GetString(Arg.Any<string>()).Returns(call =>
            $"[{call.ArgAt<string>(0)}]");
        _localization.GetString(Arg.Any<string>(), Arg.Any<object[]>()).Returns(call =>
            $"[{call.ArgAt<string>(0)}:{FormatArgs(call.ArgAt<object[]>(1))}]");
    }

    private NewOrderDialogViewModel CreateViewModel() =>
        new(
            _trading,
            new SymbolListProvider(
                _trading,
                Substitute.For<Microsoft.Extensions.Logging.ILogger<SymbolListProvider>>()),
            _riskManager,
            _confirmation,
            new OrderConfirmationComposer(_localization),
            _journal,
            _history,
            _localization,
            Substitute.For<Microsoft.Extensions.Logging.ILogger<NewOrderDialogViewModel>>());

    private static void FillValidLimitOrder(NewOrderDialogViewModel viewModel)
    {
        viewModel.SymbolInput = "BTC/USDT";
        viewModel.QuantityInput = "0.01";
        viewModel.SelectedSideIndex = 0; // Buy
        viewModel.SelectedTypeIndex = 1; // Limit
        viewModel.PriceInput = "50000";
    }

    private static Order BuildFilledOrder(OrderRequest request) => new()
    {
        Id = "paper-test",
        ExchangeId = "paper",
        Symbol = request.Symbol,
        Side = request.Side,
        Type = request.Type,
        Quantity = request.Quantity,
        Price = request.Price ?? Price.Zero,
        Status = OrderStatus.Filled,
        TimeInForce = request.TimeInForce,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task PlaceOrderCommand_WithValidLimitOrder_ConfirmsPlacesAndCloses()
    {
        // Arrange
        var viewModel = CreateViewModel();
        FillValidLimitOrder(viewModel);

        var capturedRequest = (OrderRequest?)null;
        _riskManager.CheckOrderAsync(Arg.Do<OrderRequest>(r => capturedRequest = r))
            .Returns(RiskCheckResult.Approved(requiresConfirmation: true));
        _confirmation.RequestConfirmationAsync(Arg.Any<ConfirmationRequest>()).Returns(true);

        var placed = (OrderRequest?)null;
        _trading.PlaceOrderAsync(Arg.Do<OrderRequest>(r => placed = r))
            .Returns(call => BuildFilledOrder(call.Arg<OrderRequest>()!));

        var closes = 0;
        viewModel.CloseRequested.RegisterHandler(context =>
        {
            Interlocked.Increment(ref closes);
            context.SetOutput(ReactiveUI.Primitives.RxVoid.Default);
            return Task.CompletedTask;
        });

        // Act
        await viewModel.PlaceOrderCommand.Execute(ReactiveUI.Primitives.RxVoid.Default);

        // Assert: the same request object went through every gate.
        capturedRequest.Should().NotBeNull();
        placed.Should().Be(capturedRequest);
        closes.Should().Be(1);
        viewModel.ErrorMessage.Should().BeNull();

        await _confirmation.Received().RequestConfirmationAsync(Arg.Any<ConfirmationRequest>());
    }

    [Fact]
    public async Task PlaceOrderCommand_WithRiskRejection_ShowsReasonAndNeverConfirms()
    {
        // Arrange
        var viewModel = CreateViewModel();
        FillValidLimitOrder(viewModel);
        _riskManager.CheckOrderAsync(Arg.Any<OrderRequest>())
            .Returns(RiskCheckResult.Rejected("too aggressive for this account"));

        // Act
        await viewModel.PlaceOrderCommand.Execute(ReactiveUI.Primitives.RxVoid.Default);

        // Assert: rejection reason is surfaced, confirmation and placement never run.
        viewModel.ErrorMessage.Should().Contain("NewOrder.OrderRejected");
        viewModel.ErrorMessage.Should().Contain("too aggressive for this account");
        await _confirmation.DidNotReceive().RequestConfirmationAsync(Arg.Any<ConfirmationRequest>());
        await _trading.DidNotReceive().PlaceOrderAsync(Arg.Any<OrderRequest>());
    }

    [Fact]
    public async Task PlaceOrderCommand_WithUserCancellingConfirmation_PlacesNothing()
    {
        // Arrange
        var viewModel = CreateViewModel();
        FillValidLimitOrder(viewModel);
        _riskManager.CheckOrderAsync(Arg.Any<OrderRequest>())
            .Returns(RiskCheckResult.Approved(requiresConfirmation: true));
        _confirmation.RequestConfirmationAsync(Arg.Any<ConfirmationRequest>()).Returns(false);

        // Act
        await viewModel.PlaceOrderCommand.Execute(ReactiveUI.Primitives.RxVoid.Default);

        // Assert
        await _trading.DidNotReceive().PlaceOrderAsync(Arg.Any<OrderRequest>());
        viewModel.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task PlaceOrderCommand_WithUnparsableQuantity_ErrorsWithoutRiskCheck()
    {
        // Arrange: "no silent substitution" — the bad text must not become a number.
        var viewModel = CreateViewModel();
        viewModel.SymbolInput = "BTC/USDT";
        viewModel.QuantityInput = "0,0x1";
        viewModel.SelectedTypeIndex = 1; // Limit
        viewModel.PriceInput = "50000";

        // Act
        await viewModel.PlaceOrderCommand.Execute(ReactiveUI.Primitives.RxVoid.Default);

        // Assert
        viewModel.ErrorMessage.Should().Contain("NewOrder.QuantityNotANumber").And.Contain("0,0x1");
        await _riskManager.DidNotReceive().CheckOrderAsync(Arg.Any<OrderRequest>());
    }

    [Fact]
    public async Task PlaceOrderCommand_WithLimitOrderMissingPrice_ErrorsWithoutRiskCheck()
    {
        // Arrange
        var viewModel = CreateViewModel();
        viewModel.SymbolInput = "BTC/USDT";
        viewModel.QuantityInput = "0.01";
        viewModel.SelectedTypeIndex = 1; // Limit; PriceInput stays empty

        // Act
        await viewModel.PlaceOrderCommand.Execute(ReactiveUI.Primitives.RxVoid.Default);

        // Assert
        viewModel.ErrorMessage.Should().Contain("NewOrder.PriceNotANumber");
        await _riskManager.DidNotReceive().CheckOrderAsync(Arg.Any<OrderRequest>());
    }

    [Fact]
    public async Task PlaceOrderCommand_WithMarketOrder_ConfirmationNamesMarketPrice()
    {
        // Arrange
        var viewModel = CreateViewModel();
        viewModel.SymbolInput = "BTC/USDT";
        viewModel.QuantityInput = "0.01";
        viewModel.SelectedTypeIndex = 0; // Market
        _riskManager.CheckOrderAsync(Arg.Any<OrderRequest>())
            .Returns(RiskCheckResult.Approved(requiresConfirmation: true));
        _confirmation.RequestConfirmationAsync(Arg.Any<ConfirmationRequest>()).Returns(true);
        _trading.PlaceOrderAsync(Arg.Any<OrderRequest>())
            .Returns(call => BuildFilledOrder(call.Arg<OrderRequest>()!));

        var closes = 0;
        var confirmationRequest = (ConfirmationRequest?)null;
        viewModel.CloseRequested.RegisterHandler(context =>
        {
            Interlocked.Increment(ref closes);
            context.SetOutput(ReactiveUI.Primitives.RxVoid.Default);
            return Task.CompletedTask;
        });
        _confirmation.RequestConfirmationAsync(Arg.Do<ConfirmationRequest>(cr => confirmationRequest = cr))
            .Returns(true);

        // Act
        await viewModel.PlaceOrderCommand.Execute(ReactiveUI.Primitives.RxVoid.Default);

        // Assert: the same localized summary the MCP trading tools build, with
        // the market label in place of a price.
        confirmationRequest.Should().NotBeNull();
        confirmationRequest!.MessageKey.Should().Be("Trading.ConfirmPlaceOrderFormat");
        confirmationRequest.Arguments.Should().HaveCount(5);
        confirmationRequest.Arguments[1].Should().Be("[Order.Market]");
        confirmationRequest.Arguments[4].Should().Be("[Trading.PriceMarket]");
        closes.Should().Be(1);
        viewModel.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task PlaceOrderCommand_WhenAdapterThrows_ShowsPlaceFailedError()
    {
        // Arrange: insufficient simulated balance is surfaced by the paper
        // adapter as InvalidOperationException; the dialog must present it
        // as a localized error, keep the dialog open, and stay re-runnable.
        var viewModel = CreateViewModel();
        FillValidLimitOrder(viewModel);
        _riskManager.CheckOrderAsync(Arg.Any<OrderRequest>())
            .Returns(RiskCheckResult.Approved(requiresConfirmation: true));
        _confirmation.RequestConfirmationAsync(Arg.Any<ConfirmationRequest>()).Returns(true);
        _trading.PlaceOrderAsync(Arg.Any<OrderRequest>())
            .Returns(_ => Task.FromException<Order>(new InvalidOperationException("Insufficient simulated balance: USDT")));

        var closes = 0;
        viewModel.CloseRequested.RegisterHandler(context =>
        {
            Interlocked.Increment(ref closes);
            context.SetOutput(ReactiveUI.Primitives.RxVoid.Default);
            return Task.CompletedTask;
        });

        // Act
        await viewModel.PlaceOrderCommand.Execute(ReactiveUI.Primitives.RxVoid.Default);

        // Assert: error shown, dialog not closed, placement pipeline released.
        viewModel.ErrorMessage.Should().Contain("NewOrder.PlaceFailed");
        viewModel.ErrorMessage.Should().Contain("Insufficient simulated balance");
        closes.Should().Be(0);
    }

    [Fact]
    public void SelectedTypeIndex_WhenChanged_TogglesPriceRequired()
    {
        // The dialog VM must not demand more than the inputs it owns: a limit
        // selection alone enables the price field (UI wiring, no service call).
        var viewModel = CreateViewModel();
        viewModel.SelectedTypeIndex = 1;
        viewModel.IsPriceRequired.Should().BeTrue();
        viewModel.SelectedTypeIndex = 0;
        viewModel.IsPriceRequired.Should().BeFalse();
    }

    [Fact]
    public async Task PlaceOrderCommand_AfterConfirmedPlacement_RecordsOrderInSessionJournal()
    {
        // Arrange: a successful placement must be visible to the positions
        // panel through the session order journal.
        var viewModel = CreateViewModel();
        FillValidLimitOrder(viewModel);
        _riskManager.CheckOrderAsync(Arg.Any<OrderRequest>())
            .Returns(RiskCheckResult.Approved(requiresConfirmation: true));
        _confirmation.RequestConfirmationAsync(Arg.Any<ConfirmationRequest>()).Returns(true);
        _trading.PlaceOrderAsync(Arg.Any<OrderRequest>())
            .Returns(call => BuildFilledOrder(call.Arg<OrderRequest>()!));

        viewModel.CloseRequested.RegisterHandler(context =>
        {
            context.SetOutput(ReactiveUI.Primitives.RxVoid.Default);
            return Task.CompletedTask;
        });

        // Act
        await viewModel.PlaceOrderCommand.Execute(ReactiveUI.Primitives.RxVoid.Default);

        // Assert
        _journal.GetRecent().Should().ContainSingle(order => order.Id == "paper-test");
    }

    [Fact]
    public async Task PlaceOrderCommand_WhenNotPlaced_JournalStaysEmpty()
    {
        // Arrange: a user-declined confirmation never places, so the journal
        // must not gain a record (the panel shows only real placements).
        var viewModel = CreateViewModel();
        FillValidLimitOrder(viewModel);
        _riskManager.CheckOrderAsync(Arg.Any<OrderRequest>())
            .Returns(RiskCheckResult.Approved(requiresConfirmation: true));
        _confirmation.RequestConfirmationAsync(Arg.Any<ConfirmationRequest>()).Returns(false);

        // Act
        await viewModel.PlaceOrderCommand.Execute(ReactiveUI.Primitives.RxVoid.Default);

        // Assert
        _journal.GetRecent().Should().BeEmpty();
    }

    [Fact]
    public async Task PlaceOrderCommand_AfterConfirmedPlacement_PersistsOrderToHistoryStore()
    {
        // Arrange: a successful placement must reach the persistent SQLite
        // journal (restart acceptance), beyond the session-only journal.
        var placed = (Order?)null;
        _history.RecordPlacedAsync(Arg.Do<Order>(order => placed = order))
            .Returns(Task.CompletedTask);
        var viewModel = CreateViewModel();
        FillValidLimitOrder(viewModel);
        _riskManager.CheckOrderAsync(Arg.Any<OrderRequest>())
            .Returns(RiskCheckResult.Approved(requiresConfirmation: true));
        _confirmation.RequestConfirmationAsync(Arg.Any<ConfirmationRequest>()).Returns(true);
        _trading.PlaceOrderAsync(Arg.Any<OrderRequest>())
            .Returns(call => BuildFilledOrder(call.Arg<OrderRequest>()!));

        viewModel.CloseRequested.RegisterHandler(context =>
        {
            context.SetOutput(ReactiveUI.Primitives.RxVoid.Default);
            return Task.CompletedTask;
        });

        // Act
        await viewModel.PlaceOrderCommand.Execute(ReactiveUI.Primitives.RxVoid.Default);

        // Assert: exactly one record, carrying the adapter's own order.
        await _history.Received(1).RecordPlacedAsync(Arg.Any<Order>());
        placed.Should().NotBeNull();
        placed!.Id.Should().Be("paper-test");
        placed.ExchangeId.Should().Be("paper");
    }

    [Fact]
    public async Task PlaceOrderCommand_WhenHistoryStoreFails_PlacementStillSucceeds()
    {
        // Arrange: persistence is best effort — a local database fault must
        // never turn an adapter-accepted order into a dialog error or keep
        // the dialog open with stale inputs.
        _history.RecordPlacedAsync(Arg.Any<Order>())
            .Returns(_ => Task.FromException(new InvalidOperationException("disk full")));
        var viewModel = CreateViewModel();
        FillValidLimitOrder(viewModel);
        _riskManager.CheckOrderAsync(Arg.Any<OrderRequest>())
            .Returns(RiskCheckResult.Approved(requiresConfirmation: true));
        _confirmation.RequestConfirmationAsync(Arg.Any<ConfirmationRequest>()).Returns(true);
        _trading.PlaceOrderAsync(Arg.Any<OrderRequest>())
            .Returns(call => BuildFilledOrder(call.Arg<OrderRequest>()!));

        var closes = 0;
        viewModel.CloseRequested.RegisterHandler(context =>
        {
            Interlocked.Increment(ref closes);
            context.SetOutput(ReactiveUI.Primitives.RxVoid.Default);
            return Task.CompletedTask;
        });

        // Act
        await viewModel.PlaceOrderCommand.Execute(ReactiveUI.Primitives.RxVoid.Default);

        // Assert
        closes.Should().Be(1);
        viewModel.ErrorMessage.Should().BeNull();
        _journal.GetRecent().Should().ContainSingle(order => order.Id == "paper-test");
    }

    private static string FormatArgs(object[] args) => string.Join("|", args);

    [Fact]
    public async Task LoadSymbolsCommand_Executed_DerivesSuggestionsWithPrefixMatchesFirst()
    {
        // Arrange: the fake adapter answers exchangeInfo with the normalized
        // BASE/QUOTE strings. The user typed "btc" (lower case) BEFORE the
        // list returns — the dialog opens faster than the remote answers.
        var viewModel = CreateViewModel();
        _trading.GetSymbolsAsync().Returns(new List<Symbol>
        {
            Symbol.Parse("ETH/BTC"),
            Symbol.Parse("BTC/USDT"),
            Symbol.Parse("BTC/USDC"),
            Symbol.Parse("XRP/BTC"),
        });
        viewModel.SymbolInput = "btc";

        // Act
        await viewModel.LoadSymbolsCommand.Execute(ReactiveUI.Primitives.RxVoid.Default);

        // Assert: case-insensitive contains matches; prefix matches (BTC/*)
        // ordered ahead of strictly-contains matches (*/BTC), source order
        // kept inside both buckets. The late-arriving list must open the
        // dropdown for the text the user already typed (the stale-empty
        // popup would otherwise need a retype).
        viewModel.SymbolSuggestions.Should()
            .Equal("BTC/USDT", "BTC/USDC", "ETH/BTC", "XRP/BTC");
        viewModel.IsSymbolSuggestionsOpen.Should()
            .BeTrue("a late list arrives while input text is present");
    }

    [Fact]
    public async Task WhitespaceInput_YieldsNoSuggestionsEvenWithSymbolsLoaded()
    {
        // Arrange
        var viewModel = CreateViewModel();
        _trading.GetSymbolsAsync().Returns(new List<Symbol> { Symbol.Parse("BTC/USDT") });

        // Act
        await viewModel.LoadSymbolsCommand.Execute(ReactiveUI.Primitives.RxVoid.Default);
        viewModel.SymbolInput = "   ";

        // Assert
        viewModel.SymbolSuggestions.Should().BeEmpty("nothing typed means nothing suggested");
        viewModel.IsSymbolSuggestionsOpen.Should().BeFalse("the dropdown stays closed for whitespace input");
    }

    [Fact]
    public async Task LoadSymbolsCommand_WhenRequestFails_StaysSilentAndFieldKeepsAcceptingText()
    {
        // Arrange: offline — the designed empty state, never an error UI.
        _trading.GetSymbolsAsync()
            .Returns(_ => Task.FromException<IReadOnlyList<Symbol>>(
                new System.Net.Http.HttpRequestException("offline")));
        var viewModel = CreateViewModel();

        // Act: the load completes despite the failure; typing still works.
        await viewModel.LoadSymbolsCommand.Execute(ReactiveUI.Primitives.RxVoid.Default);
        viewModel.SymbolInput = "btc";

        // Assert
        viewModel.SymbolSuggestions.Should().BeEmpty("a failed symbol request contributes no suggestions");
        viewModel.SymbolInput.Should().Be("btc", "the field stays free-form");
        viewModel.HasErrorMessage.Should().BeFalse("an autocomplete failure is not a placement error");
        viewModel.IsSymbolSuggestionsOpen.Should().BeFalse("nothing to show without a symbol list");
    }
}
