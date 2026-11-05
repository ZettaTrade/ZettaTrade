namespace ZettaTrade.App.Tests.ViewModels;

using System.Collections.Generic;
using System.Net.Http;
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
/// offline, free-form input always usable). The dialog header mirrors the
/// active exchange: it bootstraps from the facade behavioral stream on open
/// and tracks toolbar switches while the dialog stays open.
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

    private NewOrderDialogViewModel CreateViewModel(
        IMarketDataAdapter? marketData = null,
        ActiveExchangeService? exchanges = null)
    {
        // The dialog sees the market data side through the provider, and the
        // provider is now scoped to the active-exchange facade: compose a
        // one-entry catalog for the test subject, unless the test supplies
        // its own service (the header tests use a two-entry catalog to
        // drive a live switch).
        var adapter = marketData ?? _trading;
        var service = exchanges ?? new ActiveExchangeService(
            [adapter],
            Substitute.For<ISettingsService>(),
            Substitute.For<Microsoft.Extensions.Logging.ILogger<ActiveExchangeService>>());
        return new NewOrderDialogViewModel(
            _trading,
            new SymbolListProvider(
                service,
                Substitute.For<Microsoft.Extensions.Logging.ILogger<SymbolListProvider>>()),
            service,
            _riskManager,
            _confirmation,
            new OrderConfirmationComposer(_localization),
            _journal,
            _history,
            _localization,
            Substitute.For<Microsoft.Extensions.Logging.ILogger<NewOrderDialogViewModel>>());
    }

    /// <summary>
    /// A two-adapter facade for the header tests: named adapter stubs with
    /// registered ids and a settings service reporting "no persisted
    /// choice", so switches start from the default entry.
    /// </summary>
    private static ActiveExchangeService CreateSwitchableFacade(
        IMarketDataAdapter first,
        IMarketDataAdapter second)
    {
        // Explicit "no persisted choice": the restore is never exercised in
        // these tests, the default entry starts active.
        var settings = Substitute.For<ISettingsService>();
        settings.GetAsync<string>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(string.Empty);

        return new ActiveExchangeService(
            [first, second],
            settings,
            Substitute.For<Microsoft.Extensions.Logging.ILogger<ActiveExchangeService>>());
    }

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

    /// <summary>
    /// The exact post-merge book-click repro: the prefill carries symbol,
    /// side and price, the quantity stays empty by design ("a click is never
    /// a size"), and an early Place press must ask for the size instead of
    /// the dangled "not a number: ".
    /// </summary>
    [Fact]
    public async Task PlaceOrderCommand_WithEmptyQuantity_AsksForTheQuantity()
    {
        // Arrange
        var viewModel = CreateViewModel();
        viewModel.Prefill(new OrderPrefill(
            Symbol.Parse("BTC/USDT"),
            OrderSide.Buy,
            66993.30m));

        // Act
        await viewModel.PlaceOrderCommand.Execute(ReactiveUI.Primitives.RxVoid.Default);

        // Assert
        viewModel.ErrorMessage.Should().Be("[NewOrder.QuantityRequired]");
        await _riskManager.DidNotReceive().CheckOrderAsync(Arg.Any<OrderRequest>());
        await _trading.DidNotReceive().PlaceOrderAsync(Arg.Any<OrderRequest>());
    }

    [Fact]
    public async Task PlaceOrderCommand_WithWhitespaceQuantity_AsksForTheQuantity()
    {
        // Arrange
        var viewModel = CreateViewModel();
        viewModel.SymbolInput = "BTC/USDT";
        viewModel.QuantityInput = "   ";
        viewModel.SelectedTypeIndex = 0; // Market: price parsing is skipped

        // Act
        await viewModel.PlaceOrderCommand.Execute(ReactiveUI.Primitives.RxVoid.Default);

        // Assert: whitespace-only input is MISSING, not unparsable.
        viewModel.ErrorMessage.Should().Be("[NewOrder.QuantityRequired]");
        await _trading.DidNotReceive().PlaceOrderAsync(Arg.Any<OrderRequest>());
    }

    /// <summary>
    /// A Limit order without a typed price: the honest refusal names the
    /// REQUIREMENT — the former wording ("Limit price is not a number: ")
    /// dangled on a blank argument. Whitespace-only input covers the trim.
    /// </summary>
    [Fact]
    public async Task PlaceOrderCommand_WithLimitOrderMissingPrice_AsksForThePrice()
    {
        // Arrange
        var viewModel = CreateViewModel();
        viewModel.SymbolInput = "BTC/USDT";
        viewModel.QuantityInput = "0.01";
        viewModel.SelectedTypeIndex = 1; // Limit
        viewModel.PriceInput = "   ";

        // Act
        await viewModel.PlaceOrderCommand.Execute(ReactiveUI.Primitives.RxVoid.Default);

        // Assert
        viewModel.ErrorMessage.Should().Be("[NewOrder.PriceRequired]");
        await _riskManager.DidNotReceive().CheckOrderAsync(Arg.Any<OrderRequest>());
        await _trading.DidNotReceive().PlaceOrderAsync(Arg.Any<OrderRequest>());
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

    /// <summary>
    /// BUG-017: a placement can fail BELOW the validation surface — the
    /// paper adapter reaches its reference rate over REST, so the wire
    /// failures land in the pipeline as an <see cref="HttpRequestException"/>
    /// or as a <see cref="TaskCanceledException"/> (the HttpClient timeout's
    /// shape). Each must end as the localized failure (the pre-fix catch let
    /// them escape into ThrownExceptions nothing observes — the order was
    /// silently not placed: no message, no journal entry).
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PlaceOrderCommand_WhenTheAdapterFailsOnANetworkError_ShowsLocalizedError(bool isConnectionFault)
    {
        var viewModel = CreateViewModel();
        FillValidLimitOrder(viewModel);
        _riskManager.CheckOrderAsync(Arg.Any<OrderRequest>())
            .Returns(RiskCheckResult.Approved(requiresConfirmation: true));
        _confirmation.RequestConfirmationAsync(Arg.Any<ConfirmationRequest>()).Returns(true);
        _trading.PlaceOrderAsync(Arg.Any<OrderRequest>())
            .Returns(_ => isConnectionFault
                ? Task.FromException<Order>(new HttpRequestException("wire down"))
                : Task.FromException<Order>(new TaskCanceledException("the request timed out")));

        var closes = 0;
        viewModel.CloseRequested.RegisterHandler(context =>
        {
            Interlocked.Increment(ref closes);
            context.SetOutput(ReactiveUI.Primitives.RxVoid.Default);
            return Task.CompletedTask;
        });

        // Act
        await viewModel.PlaceOrderCommand.Execute(ReactiveUI.Primitives.RxVoid.Default);

        // Assert: error shown, dialog stays open, pipeline re-runnable.
        viewModel.ErrorMessage.Should().Contain("NewOrder.PlaceFailed");
        viewModel.ErrorMessage.Should().Contain(isConnectionFault ? "wire down" : "the request timed out");
        closes.Should().Be(0, "a failed placement must not close the dialog");
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
        _trading.GetSymbolsAsync(Arg.Any<CancellationToken>()).Returns(new List<Symbol>
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
        _trading.GetSymbolsAsync(Arg.Any<CancellationToken>()).Returns(new List<Symbol> { Symbol.Parse("BTC/USDT") });

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
        _trading.GetSymbolsAsync(Arg.Any<CancellationToken>())
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

    [Fact]
    public async Task LoadSymbolsCommand_WhenTheExecutionSubscriptionIsDisposed_CancelsTheFetch()
    {
        // Arrange: the real window owns the load execution —
        // Execute().Subscribe(), disposed with the WhenActivated disposables.
        // The fake fetch parks on the token it receives (only cancellation
        // completes it), so the test observes the exact token the dialog
        // flows into the provider and asserts it dies with the window.
        var fetchEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var consumedToken = (CancellationToken?)null;
        var marketData = Substitute.For<IMarketDataAdapter>();
        marketData.GetSymbolsAsync(Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var token = call.ArgAt<CancellationToken>(0);
                consumedToken = token;
                fetchEntered.SetResult();
                return AwaitCancellationAsync(token);
            });
        var viewModel = CreateViewModel(marketData);

        // Act: subscribe, wait until the fetch is in flight, then dispose
        // the execution subscription (the dialog closed while it ran).
        var execution = viewModel.LoadSymbolsCommand.Execute(ReactiveUI.Primitives.RxVoid.Default);
        using (execution.Subscribe(_ => { }, _ => { }))
        {
            await fetchEntered.Task;
        }

        // Assert
        consumedToken.Should().NotBeNull("the fetch ran through the dialog's own token");
        consumedToken!.Value.IsCancellationRequested.Should().BeTrue(
            "disposing the execution subscription must cancel the in-flight fetch");
    }

    [Fact]
    public void Header_BootstrapsFromTheActiveExchangeOnOpen()
    {
        // Arrange: the facade starts on its first registered adapter.
        var service = CreateSwitchableFacade(
            CreateNamedAdapter("binance", "Binance Spot"),
            CreateNamedAdapter("demo", "Demo"));

        // Act + assert: the behavioral stream emits its current adapter on
        // subscribe, so the bootstrap is a synchronous constructor side
        // effect — a direct assertion is deterministic (no cross-thread
        // event ever happened here). `using` exercises the transient VM's
        // own disposal path (the one the window closure calls).
        using var viewModel = CreateViewModel(exchanges: service);
        viewModel.ActiveExchangeName.Should().Be("Binance Spot");
    }

    [Fact]
    public async Task Header_FollowsTheToolbarSwitchWhileOpen()
    {
        // Arrange: the facade starts on Binance; the dialog stays open (the
        // VM subscribes to the same behavioral stream) while the toolbar
        // switches to Demo.
        var service = CreateSwitchableFacade(
            CreateNamedAdapter("binance", "Binance Spot"),
            CreateNamedAdapter("demo", "Demo"));
        using var viewModel = CreateViewModel(exchanges: service);
        viewModel.ActiveExchangeName.Should().Be("Binance Spot");

        // Act: the toolbar switch — persist-then-activate inside the
        // facade. The VM property assignment runs synchronously inside
        // Activate (same thread as this call), so the direct assertions
        // observe no cross-thread event.
        await service.SwitchToAsync("demo");

        // Assert: the header flipped without reopening the dialog.
        viewModel.ActiveExchangeName.Should().Be("Demo");
    }

    /// <summary>
    /// Prefill from a clicked book level: the clicked symbol, side and price
    /// land in the inputs, the type switches to an explicit Limit, and the
    /// quantity stays EMPTY — a click is never a size. The bid→Buy pair has
    /// its own fact below.
    /// </summary>
    [Fact]
    public void Prefill_FromClickedAskLevel_CarriesInputsAndLeavesQuantityEmpty()
    {
        using var viewModel = CreateViewModel();

        viewModel.Prefill(new OrderPrefill(
            Symbol.Parse("btc/usdt"),
            OrderSide.Sell,
            100.01m));

        viewModel.SymbolInput.Should().Be("BTC/USDT"); // normalized, not typed
        viewModel.SelectedSideIndex.Should().Be((int)OrderSide.Sell);
        viewModel.SelectedTypeIndex.Should().Be((int)OrderType.Limit);
        viewModel.PriceInput.Should().Be("100.01"); // invariant
        viewModel.QuantityInput.Should().BeEmpty();
        viewModel.IsPriceRequired.Should().BeTrue();
    }

    [Fact]
    public void Prefill_FromClickedBidLevel_MapsBuyAndForcesTwoFractionDigits()
    {
        using var viewModel = CreateViewModel();

        viewModel.Prefill(new OrderPrefill(
            Symbol.Parse("BTC/USDT"),
            OrderSide.Buy,
            99.5m));

        viewModel.SelectedSideIndex.Should().Be((int)OrderSide.Buy);
        viewModel.SelectedTypeIndex.Should().Be((int)OrderType.Limit);
        viewModel.PriceInput.Should().Be("99.50", "the money-like format keeps the trailing zero");
        viewModel.QuantityInput.Should().BeEmpty();
    }

    [Fact]
    public void Prefill_TouchesOnlyTheCarriedFields()
    {
        // Arrange: even if the dialog already holds user input, a prefill
        // rewrites exactly what the click carries (symbol, side, type, price)
        // and never the quantity — a click is never a size, typed or not.
        using var viewModel = CreateViewModel();
        viewModel.QuantityInput = "0.42";
        viewModel.SelectedTypeIndex = (int)OrderType.Market;

        viewModel.Prefill(new OrderPrefill(
            Symbol.Parse("ETH/USDT"),
            OrderSide.Buy,
            3.5m));

        viewModel.SymbolInput.Should().Be("ETH/USDT");
        viewModel.PriceInput.Should().Be("3.50");
        viewModel.SelectedTypeIndex.Should().Be((int)OrderType.Limit);
        viewModel.QuantityInput.Should().Be("0.42");
    }

    /// <summary>
    /// The post-merge defect: the load handler opened the dropdown for ANY
    /// text in the field, so a click-prefilled dialog came up with the popup
    /// echoing just the clicked symbol. While the field holds its untouched
    /// prefill the suggestions stay empty (the dropdown cannot open); the
    /// first edit restores the typed pipeline.
    /// </summary>
    [Fact]
    public async Task Prefill_KeepsTheDropdownQuiet_UntilTheSymbolIsEdited()
    {
        // Arrange: a warm symbol catalog — the application-lifetime provider
        // answers from its cache while the dialog is still opening.
        _trading.GetSymbolsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Symbol> { Symbol.Parse("ETH/BTC") });

        // Act: the production order — the click prefills, THEN the load
        // completes (activation runs after the window shows).
        var viewModel = CreateViewModel();
        viewModel.Prefill(new OrderPrefill(
            Symbol.Parse("ETH/BTC"),
            OrderSide.Buy,
            0.02941m));
        await viewModel.LoadSymbolsCommand.Execute(ReactiveUI.Primitives.RxVoid.Default);

        // Assert
        viewModel.SymbolSuggestions.Should().BeEmpty(
            "the click already names the full symbol; an echoing dropdown would cover the layout");
        viewModel.IsSymbolSuggestionsOpen.Should().BeFalse("a prefill is not a typist's intent");

        // The first edit releases the hold: typed text resumes suggestions.
        viewModel.SymbolInput = "ETH/B";
        viewModel.SymbolSuggestions.Should().Contain("ETH/BTC", "typed intent restores the suggestion pipeline");
    }

    /// <summary>Named adapter stub for the two-entry facade tests.</summary>
    private static IMarketDataAdapter CreateNamedAdapter(string id, string name)
    {
        var adapter = Substitute.For<IMarketDataAdapter>();
        adapter.Id.Returns(id);
        adapter.Name.Returns(name);
        return adapter;
    }

    /// <summary>
    /// Fake adapter fetch: enters once, then parks on the received token —
    /// only cancellation completes it.
    /// </summary>
    private static async Task<IReadOnlyList<Symbol>> AwaitCancellationAsync(CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct);
        throw new InvalidOperationException("the delay returned without cancellation");
    }
}
