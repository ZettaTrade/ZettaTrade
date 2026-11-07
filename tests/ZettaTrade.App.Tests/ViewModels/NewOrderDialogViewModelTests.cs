namespace ZettaTrade.App.Tests.ViewModels;

using System.Collections.Generic;
using System.Net.Http;
using System.Reactive.Linq;
using System.Reactive.Subjects;
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
using ZettaTrade.Infrastructure.Exchange;
using ZettaTrade.Infrastructure.Fees;
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
    private readonly Subject<SettingsChange> _settingsChanges = new();

    public NewOrderDialogViewModelTests()
    {
        // The fake resolves dynamic messages in a predictable, test-visible
        // form: the key plus the first argument, e.g. "NewOrder.OrderRejected:reason".
        _localization.GetString(Arg.Any<string>()).Returns(call =>
            $"[{call.ArgAt<string>(0)}]");
        _localization.GetString(Arg.Any<string>(), Arg.Any<object[]>()).Returns(call =>
            $"[{call.ArgAt<string>(0)}:{FormatArgs(call.ArgAt<object[]>(1))}]");

        // The default facade's single adapter IS the trading stub itself: with
        // the paper id the estimate goldens resolve the shipped schedule.
        _trading.Id.Returns(PaperTradingAdapter.ExchangeId);
    }

    /// <summary>
    /// The settings substitute the dialog rides: no persisted sizing
    /// default (the hardcoded one applies) and a live change stream the
    /// sizing row subscribes.
    /// </summary>
    private ISettingsService CreateSettings()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetAsync<double>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double>())
            .Returns(SizerSettingsKeys.DefaultRiskPercentValue);
        settings.Changes.Returns(_settingsChanges);
        return settings;
    }

    private NewOrderDialogViewModel CreateViewModel(
        IMarketDataAdapter? marketData = null,
        ActiveExchangeService? exchanges = null,
        IFeeScheduleProvider? feeSchedules = null)
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
            feeSchedules ?? CreatePaperFeeSchedules(),
            _localization,
            new PositionSizingService(
                new PaperInstrumentMetadataProvider(),
                _trading,
                Substitute.For<Microsoft.Extensions.Logging.ILogger<PositionSizingService>>()),
            CreateSettings(),
            Substitute.For<Microsoft.Extensions.Logging.ILogger<NewOrderDialogViewModel>>(),
            Substitute.For<Microsoft.Extensions.Logging.ILogger<OrderSizerRowViewModel>>());
    }

    /// <summary>
    /// A real fee registry over nothing configured: the shipped paper
    /// schedule answers, so the estimate goldens price known numbers.
    /// </summary>
    private static FeeScheduleService CreatePaperFeeSchedules()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.Changes.Returns(System.Reactive.Linq.Observable.Empty<SettingsChange>());
        return new(settings, Substitute.For<Microsoft.Extensions.Logging.ILogger<FeeScheduleService>>());
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

    private static Order BuildFilledOrder(OrderRequest request, string id = "paper-test") => new()
    {
        Id = id,
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

    /// <summary>One H1 history bar at the given close — the reference rate's fake.</summary>
    private static Bar BuildBar(decimal close) => new(
        DateTimeOffset.UtcNow,
        close,
        close,
        close,
        close,
        1m);

    /// <summary>
    /// The newest-by-time contract serves this close to the dialog's
    /// derivations (trailing seed, market-relative sanity checks).
    /// </summary>
    private void StubReferenceClose(decimal close) =>
        _trading.GetBarsAsync(
                Arg.Any<Symbol>(),
                Arg.Any<TimeFrame>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns([BuildBar(close)]);

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

    /// <summary>
    /// T-074 UI mapping: the conditional selections toggle exactly the rows
    /// they own — the stop trigger for the stop-limit, the trailing distance
    /// for the trailing stop, the OCO pair re-uses the price row for its
    /// take-profit leg and adds the stop-loss leg's limit row. Index 4 is
    /// deliberately NOT an OrderType cast (it aliases TrailingStop): the
    /// type mapping goes through the dialog's own table.
    /// </summary>
    [Fact]
    public void SelectedTypeIndex_WhenChanged_TogglesTheConditionalRows()
    {
        var viewModel = CreateViewModel();

        viewModel.SelectedTypeIndex = 2; // StopLimit
        viewModel.IsPriceRequired.Should().BeTrue();
        viewModel.IsTriggerVisible.Should().BeTrue();
        viewModel.IsTrailVisible.Should().BeFalse();
        viewModel.IsOcoMode.Should().BeFalse();
        viewModel.IsOcoStopLimitVisible.Should().BeFalse();

        viewModel.SelectedTypeIndex = 3; // TrailingStop
        viewModel.IsPriceRequired.Should().BeFalse();
        viewModel.IsTriggerVisible.Should().BeFalse();
        viewModel.IsTrailVisible.Should().BeTrue();

        viewModel.SelectedTypeIndex = 4; // OCO pair mode
        viewModel.IsPriceRequired.Should().BeTrue();
        viewModel.IsTriggerVisible.Should().BeTrue();
        viewModel.IsTrailVisible.Should().BeFalse();
        viewModel.IsOcoMode.Should().BeTrue();
        viewModel.IsOcoStopLimitVisible.Should().BeTrue();
    }

    [Fact]
    public async Task PlaceOrderCommand_WithTrailingStopOrder_CarriesTheDerivedSeed()
    {
        // Arrange: the trailing stop derives its SEED from the newest
        // reference close — one trail below the sell market, never a
        // defaulted or unparsable guess.
        StubReferenceClose(50_000m);
        var viewModel = CreateViewModel();
        viewModel.SymbolInput = "BTC/USDT";
        viewModel.QuantityInput = "0.2";
        viewModel.SelectedSideIndex = 1; // Sell
        viewModel.SelectedTypeIndex = 3; // TrailingStop
        viewModel.TrailDistanceInput = "500";

        var capturedRequest = (OrderRequest?)null;
        _riskManager.CheckOrderAsync(Arg.Do<OrderRequest>(r => capturedRequest = r))
            .Returns(RiskCheckResult.Approved(requiresConfirmation: false));
        _confirmation.RequestConfirmationAsync(Arg.Any<ConfirmationRequest>()).Returns(true);
        var placed = (OrderRequest?)null;
        _trading.PlaceOrderAsync(Arg.Do<OrderRequest>(r => placed = r))
            .Returns(call => BuildFilledOrder(call.Arg<OrderRequest>()!));

        viewModel.CloseRequested.RegisterHandler(context =>
        {
            context.SetOutput(ReactiveUI.Primitives.RxVoid.Default);
            return Task.CompletedTask;
        });

        // Act
        await viewModel.PlaceOrderCommand.Execute(ReactiveUI.Primitives.RxVoid.Default);

        // Assert: the request rides the full pipeline with the derived seed.
        placed.Should().Be(capturedRequest);
        placed.Should().NotBeNull();
        placed!.Type.Should().Be(OrderType.TrailingStop, "combo index 3 maps through the type table");
        placed.StopPrice!.Value.Value.Should().Be(49_500m, "the sell seed sits one trail below the market");
        placed.TrailDistance!.Value.Value.Should().Be(500m);
        placed.Price.Should().BeNull("a trailing order never carries a limit price");
    }

    [Fact]
    public async Task PlaceOrderCommand_WithUnparsableTrail_ErrorsWithoutRiskCheck()
    {
        // Arrange: the unparsable trail distance is an error, never a default.
        StubReferenceClose(50_000m);
        var viewModel = CreateViewModel();
        viewModel.SymbolInput = "BTC/USDT";
        viewModel.QuantityInput = "0.2";
        viewModel.SelectedTypeIndex = 3; // TrailingStop
        viewModel.TrailDistanceInput = "abc";

        // Act
        await viewModel.PlaceOrderCommand.Execute(ReactiveUI.Primitives.RxVoid.Default);

        // Assert
        viewModel.ErrorMessage.Should().Be("[NewOrder.TrailNotANumber:abc]");
        await _riskManager.DidNotReceive().CheckOrderAsync(Arg.Any<OrderRequest>());
        await _trading.DidNotReceive().PlaceOrderAsync(Arg.Any<OrderRequest>());
    }

    [Fact]
    public async Task PlaceOrderCommand_WithSellTrailLargerThanTheMarket_Refuses()
    {
        // Arrange: a sell trail that eats the whole reference close would
        // seed a non-positive trigger — refused, never defaulted.
        StubReferenceClose(50_000m);
        var viewModel = CreateViewModel();
        viewModel.SymbolInput = "BTC/USDT";
        viewModel.QuantityInput = "0.2";
        viewModel.SelectedSideIndex = 1; // Sell
        viewModel.SelectedTypeIndex = 3; // TrailingStop
        viewModel.TrailDistanceInput = "60000";

        // Act
        await viewModel.PlaceOrderCommand.Execute(ReactiveUI.Primitives.RxVoid.Default);

        // Assert
        viewModel.ErrorMessage.Should().Be("[NewOrder.TrailTooLarge:60000|50000]");
        await _trading.DidNotReceive().PlaceOrderAsync(Arg.Any<OrderRequest>());
    }

    [Fact]
    public async Task PlaceOrderCommand_WithValidStopLimitOrder_CarriesTriggerAndLimit()
    {
        // Arrange: a sell stop-limit arms strictly below the market.
        StubReferenceClose(50_000m);
        var viewModel = CreateViewModel();
        viewModel.SymbolInput = "BTC/USDT";
        viewModel.QuantityInput = "0.2";
        viewModel.SelectedSideIndex = 1; // Sell
        viewModel.SelectedTypeIndex = 2; // StopLimit
        viewModel.PriceInput = "48900";
        viewModel.StopTriggerInput = "49000";

        var placed = (OrderRequest?)null;
        _riskManager.CheckOrderAsync(Arg.Any<OrderRequest>())
            .Returns(RiskCheckResult.Approved(requiresConfirmation: false));
        _confirmation.RequestConfirmationAsync(Arg.Any<ConfirmationRequest>()).Returns(true);
        _trading.PlaceOrderAsync(Arg.Do<OrderRequest>(r => placed = r))
            .Returns(call => BuildFilledOrder(call.Arg<OrderRequest>()!));

        viewModel.CloseRequested.RegisterHandler(context =>
        {
            context.SetOutput(ReactiveUI.Primitives.RxVoid.Default);
            return Task.CompletedTask;
        });

        // Act
        await viewModel.PlaceOrderCommand.Execute(ReactiveUI.Primitives.RxVoid.Default);

        // Assert
        placed.Should().NotBeNull();
        placed!.Type.Should().Be(OrderType.StopLimit);
        placed.StopPrice!.Value.Value.Should().Be(49_000m);
        placed.Price!.Value.Value.Should().Be(48_900m);
        viewModel.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task PlaceOrderCommand_WithSellStopTriggerAboveTheMarket_Refuses()
    {
        // Arrange: a sell stop at or above the market would trigger
        // immediately — the dialog refuses it instead of the adapter.
        StubReferenceClose(50_000m);
        var viewModel = CreateViewModel();
        viewModel.SymbolInput = "BTC/USDT";
        viewModel.QuantityInput = "0.2";
        viewModel.SelectedSideIndex = 1; // Sell
        viewModel.SelectedTypeIndex = 2; // StopLimit
        viewModel.PriceInput = "48900";
        viewModel.StopTriggerInput = "50500";

        // Act
        await viewModel.PlaceOrderCommand.Execute(ReactiveUI.Primitives.RxVoid.Default);

        // Assert
        viewModel.ErrorMessage.Should().Be("[NewOrder.SellTriggerMustBeBelowMarket:50500|50000]");
        await _trading.DidNotReceive().PlaceOrderAsync(Arg.Any<OrderRequest>());
    }

    [Fact]
    public async Task PlaceOrderCommand_WithOcoPair_ConfirmsPlacesBothLegsAndClosesOnce()
    {
        // Arrange: the OCO pair places a take-profit limit first, then the
        // stop-loss stop-limit — both sharing one either-or group id.
        StubReferenceClose(50_000m);
        var viewModel = CreateViewModel();
        viewModel.SymbolInput = "BTC/USDT";
        viewModel.QuantityInput = "0.2";
        viewModel.SelectedSideIndex = 1; // Sell
        viewModel.SelectedTypeIndex = 4; // OCO
        viewModel.PriceInput = "51000"; // take-profit limit price
        viewModel.StopTriggerInput = "49000"; // stop-loss trigger
        viewModel.OcoStopLimitInput = "48900"; // stop-loss limit price

        var placedRequests = new List<OrderRequest>();
        var legIds = new[] { "paper-tp", "paper-sl" };
        _trading.PlaceOrderAsync(Arg.Do<OrderRequest>(placedRequests.Add))
            .Returns(call =>
            {
                var request = call.Arg<OrderRequest>()!;
                return Task.FromResult(BuildFilledOrder(request, legIds[placedRequests.Count - 1]));
            });
        _riskManager.CheckOrderAsync(Arg.Any<OrderRequest>())
            .Returns(RiskCheckResult.Approved(requiresConfirmation: false));
        _confirmation.RequestConfirmationAsync(Arg.Any<ConfirmationRequest>()).Returns(true);

        var closes = 0;
        viewModel.CloseRequested.RegisterHandler(context =>
        {
            Interlocked.Increment(ref closes);
            context.SetOutput(ReactiveUI.Primitives.RxVoid.Default);
            return Task.CompletedTask;
        });

        // Act
        await viewModel.PlaceOrderCommand.Execute(ReactiveUI.Primitives.RxVoid.Default);

        // Assert: two legs of ONE group, placed through two full pipelines.
        placedRequests.Should().HaveCount(2);
        var groupId = placedRequests[0].OcoGroupId;
        groupId.Should().NotBeNullOrEmpty();
        placedRequests[1].OcoGroupId.Should().Be(groupId);
        placedRequests[0].Type.Should().Be(OrderType.Limit);
        placedRequests[0].Price!.Value.Value.Should().Be(51_000m);
        placedRequests[1].Type.Should().Be(OrderType.StopLimit);
        placedRequests[1].StopPrice!.Value.Value.Should().Be(49_000m);
        placedRequests[1].Price!.Value.Value.Should().Be(48_900m);
        placedRequests[0].Quantity.Value.Should().Be(placedRequests[1].Quantity.Value);

        await _riskManager.Received(2).CheckOrderAsync(Arg.Any<OrderRequest>());
        await _confirmation.Received(2).RequestConfirmationAsync(Arg.Any<ConfirmationRequest>());
        closes.Should().Be(1);
        viewModel.ErrorMessage.Should().BeNull();
        viewModel.ResultMessage.Should().Be("[NewOrder.OcoPlaced:paper-tp|paper-sl]");
    }

    [Fact]
    public async Task PlaceOrderCommand_WhenTheOcoSecondLegFails_CancelsThePlacedLegAndKeepsTheDialogOpen()
    {
        // Arrange: the stop-loss leg's placement fails AFTER the take-profit
        // leg is already working — the placed half is cancelled best-effort,
        // the pair never goes half-working.
        StubReferenceClose(50_000m);
        var viewModel = CreateViewModel();
        viewModel.SymbolInput = "BTC/USDT";
        viewModel.QuantityInput = "0.2";
        viewModel.SelectedSideIndex = 1; // Sell
        viewModel.SelectedTypeIndex = 4; // OCO
        viewModel.PriceInput = "51000";
        viewModel.StopTriggerInput = "49000";
        viewModel.OcoStopLimitInput = "48900";

        var placedRequests = new List<OrderRequest>();
        _trading.PlaceOrderAsync(Arg.Do<OrderRequest>(placedRequests.Add))
            .Returns(call =>
            {
                var request = call.Arg<OrderRequest>()!;
                return placedRequests.Count == 1
                    ? Task.FromResult(BuildFilledOrder(request, "paper-tp"))
                    : Task.FromException<Order>(new InvalidOperationException("arming violated"));
            });
        _riskManager.CheckOrderAsync(Arg.Any<OrderRequest>())
            .Returns(RiskCheckResult.Approved(requiresConfirmation: false));
        _confirmation.RequestConfirmationAsync(Arg.Any<ConfirmationRequest>()).Returns(true);

        var closes = 0;
        viewModel.CloseRequested.RegisterHandler(context =>
        {
            Interlocked.Increment(ref closes);
            context.SetOutput(ReactiveUI.Primitives.RxVoid.Default);
            return Task.CompletedTask;
        });

        // Act
        await viewModel.PlaceOrderCommand.Execute(ReactiveUI.Primitives.RxVoid.Default);

        // Assert: the placed take-profit leg cancelled, the abort named, no close.
        placedRequests.Should().HaveCount(2);
        await _trading.Received(1).CancelOrderAsync(Arg.Any<Symbol>(), "paper-tp");
        viewModel.ErrorMessage.Should().Be("[NewOrder.OcoAborted]");
        closes.Should().Be(0, "a half pair aborted the dialog must stay open");
        _journal.GetRecent().Should().ContainSingle(order => order.Id == "paper-tp");
    }

    private static string FormatArgs(object[] args) => string.Join("|", args);

    /// <summary>The fake fetch of the disposal test: it parks forever on the caller's token.</summary>
    private static Task<IReadOnlyList<Symbol>> AwaitCancellationAsync(CancellationToken token) =>
        Task.FromCanceled<IReadOnlyList<Symbol>>(token);

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
            NewNamedTradingAdapter("binance", "Binance Spot"),
            NewNamedTradingAdapter("demo", "Demo"));

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
            NewNamedTradingAdapter("binance", "Binance Spot"),
            NewNamedTradingAdapter("demo", "Demo"));
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

    /// <summary>
    /// A schedule registry that publishes nothing anywhere: every fee
    /// surface must render the honest unknown.
    /// </summary>
    private static IFeeScheduleProvider UnknownFeeSchedules()
    {
        var provider = Substitute.For<IFeeScheduleProvider>();
        provider.GetSchedule(Arg.Any<string>()).Returns((FeeSchedule?)null);
        return provider;
    }

    #region The Ticket-Local Sizing Row (T-079)

    /// <summary>
    /// The sizing row only PROPOSES: its proposal lands in this dialog's
    /// own quantity field — the very field the submit pipeline parses with
    /// its invariant-culture parsing, validation and risk checks — so no
    /// sizing path can ever bypass the ticket's gates.
    /// </summary>
    [Fact]
    public async Task Sizer_TheProposalLandsInTheTicketQuantityField()
    {
        // Arrange: custody served once; the dialog parses the pair onto the row.
        _trading.GetBalancesAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, Money>
            {
                ["USDT"] = new(1_000m, "USDT"),
                ["BTC"] = new(0.8m, "BTC"),
            });
        using var viewModel = CreateViewModel();
        var capture = viewModel.Sizer.ContextCaptures.FirstAsync();

        viewModel.SymbolInput = "BTC/USDT";
        viewModel.SelectedSideIndex = (int)OrderSide.Sell;
        await capture;

        // Act: the 50 % preset proposes; the ticket's quantity field is the
        // only destination.
        await viewModel.Sizer.ApplyHalfCommand.Execute(ReactiveUI.Primitives.RxVoid.Default);

        // Assert
        viewModel.QuantityInput.Should().Be("0.4");
        _ = _trading.Received(1).GetBalancesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Sizer_TracksTheTicketSymbol_MetadataKnownPairsOnly()
    {
        using var viewModel = CreateViewModel();

        viewModel.SymbolInput = "BTC/USDT";
        viewModel.Sizer.IsRowVisible.Should().BeTrue();

        viewModel.SymbolInput = "AAPL";
        viewModel.Sizer.IsRowVisible.Should().BeFalse("the stock carries no round-size metadata");
        viewModel.Sizer.IsRiskModeAvailable.Should().BeFalse();
        viewModel.Sizer.IsSnapshotAvailable.Should().BeFalse("the hidden row owns no stale context");
    }

    [Fact]
    public void Sizer_RiskMode_RidesTheTicketShapeStopDistance()
    {
        using var viewModel = CreateViewModel();
        viewModel.SymbolInput = "BTC/USDT";

        // Act: a stop-limit whose |limit − trigger| is 1000 quote units.
        viewModel.SelectedTypeIndex = (int)OrderType.StopLimit;
        viewModel.PriceInput = "51000";
        viewModel.StopTriggerInput = "50000";

        // Assert
        viewModel.Sizer.IsRiskModeAvailable.Should().BeTrue("a stop-limit anchors |limit − trigger|");
        viewModel.Sizer.TicketStopDistance.Should().Be(1000m);
        viewModel.Sizer.TicketEntryPrice.Should().Be(51_000m);

        // Act: the same ticket without a stop shape.
        viewModel.SelectedTypeIndex = (int)OrderType.Market;

        // Assert
        viewModel.Sizer.IsRiskModeAvailable.Should().BeFalse("a market order carries no stop anchor");
        viewModel.Sizer.TicketStopDistance.Should().BeNull();
    }

    #endregion The Ticket-Local Sizing Row (T-079)

    /// <summary>
    /// A schedule registry resolved by adapter id: the paper adapter answers
    /// the shipped schedule, every other id stays unknown — the exchange
    /// switch's re-pricing golden rides it.
    /// </summary>
    private static IFeeScheduleProvider IdleSelectableFees()
    {
        var provider = Substitute.For<IFeeScheduleProvider>();
        // The any-id fallback is configured FIRST: the specific paper setup
        // below must be the most recent match (NSubstitute resolves the
        // last configured match), or it would answer null everywhere.
        provider.GetSchedule(Arg.Any<string>()).Returns((FeeSchedule?)null);
        provider
            .GetSchedule(Arg.Is(PaperTradingAdapter.ExchangeId))
            .Returns(new FeeSchedule(
                FeeScheduleService.PaperMakerRateBps,
                FeeScheduleService.PaperTakerRateBps,
                FeeScheduleService.PaperMinimumFee));
        return provider;
    }

    /// <summary>The combo index of the OCO pair mode (a pair builder, never an OrderType).</summary>
    private const int OcoModeIndex = 4;

    /// <summary>Named trading stubs for the facade behind the estimate tests.</summary>
    private static ITradingAdapter NewNamedTradingAdapter(string id, string name)
    {
        var adapter = Substitute.For<ITradingAdapter>();
        adapter.Id.Returns(id);
        adapter.Name.Returns(name);
        return adapter;
    }

    #region Fee Estimates (T-080)

    /// <summary>
    /// The two-adapter facade the switch test prices across: a paper-first
    /// registry over an id the tests control ("no persisted choice" so the
    /// restore path stays untouched).
    /// </summary>
    private static ActiveExchangeService NewPaperAndOtherFacade()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetAsync<string>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(string.Empty);
        return new ActiveExchangeService(
            [NewNamedTradingAdapter("paper", "Paper Trading"), NewNamedTradingAdapter("other", "Other")],
            settings,
            Substitute.For<Microsoft.Extensions.Logging.ILogger<ActiveExchangeService>>());
    }

    [Fact]
    public void FeeEstimates_PricedLimitTicket_NamesTheMakerRoleAndTheScheduleAmount()
    {
        using var viewModel = CreateViewModel();
        viewModel.SymbolInput = "BTC/USDT";
        viewModel.QuantityInput = "0.01";
        viewModel.SelectedTypeIndex = (int)OrderType.Limit;
        viewModel.PriceInput = "50000";

        // 0.01 × 50 000 = 500 notional at the shipped 10 bps maker rate
        // (500 × 10 / 10 000): the golden prices a half-unit commission.
        viewModel.FeeEstimates.Should().ContainSingle()
            .Which.Should().Be("[Fees.EstimateMakerFormat:0.50000000]");
    }

    [Fact]
    public void FeeEstimates_MarketTicket_NamesTheTakerRole_AndAnUnresolvedNotionalIsTheDash()
    {
        using var viewModel = CreateViewModel();
        viewModel.SymbolInput = "BTC/USDT";
        viewModel.QuantityInput = "0.01";
        viewModel.SelectedTypeIndex = (int)OrderType.Market;

        // The fake adapter serves no bars, so the market reference never
        // resolves within the test: the role stays visible and the amount
        // is the dash — a displayed unknown, never a fabricated zero.
        viewModel.FeeEstimates.Should().ContainSingle()
            .Which.Should().Be("[Fees.EstimateTakerFormat:[Fees.Unknown]]");
    }

    [Fact]
    public void FeeEstimates_UnknownSchedule_TheRoleStaysVisible_WithTheDashAmount()
    {
        using var viewModel = CreateViewModel(feeSchedules: UnknownFeeSchedules());
        viewModel.SymbolInput = "BTC/USDT";
        viewModel.QuantityInput = "0.01";
        viewModel.PriceInput = "50000";
        viewModel.SelectedTypeIndex = (int)OrderType.Limit;

        viewModel.FeeEstimates.Should().ContainSingle()
            .Which.Should().Be("[Fees.EstimateMakerFormat:[Fees.Unknown]]");

        viewModel.SelectedTypeIndex = (int)OrderType.Market;
        viewModel.FeeEstimates.Should().ContainSingle()
            .Which.Should().Be("[Fees.EstimateTakerFormat:[Fees.Unknown]]");
    }

    [Fact]
    public void FeeEstimates_OcoTicket_PricesBothLegsInTheirExpectedRoles()
    {
        using var viewModel = CreateViewModel();
        viewModel.SymbolInput = "BTC/USDT";
        viewModel.QuantityInput = "0.01";
        viewModel.SelectedTypeIndex = OcoModeIndex;
        viewModel.PriceInput = "50000"; // the take-profit limit leg
        viewModel.OcoStopLimitInput = "48000"; // the stop-loss leg's limit

        viewModel.FeeEstimates.Should().HaveCount(2);
        viewModel.FeeEstimates[0].Should().Be(
            "[Fees.EstimateMakerFormat:0.50000000]",
            "the take-profit limit leg is expected to rest as maker");
        viewModel.FeeEstimates[1].Should().Be(
            "[Fees.EstimateTakerFormat:0.96000000]",
            "the stop-loss leg crosses once it triggers (0.01 × 48 000 at the 20 bps taker rate)");
    }

    [Fact]
    public async Task FeeEstimates_ExchangeSwitch_RepricesOnTheSwitchedAdaptersSchedule()
    {
        // Arrange: the shared estimator resolves paper with the shipped
        // schedule and stays unknown for the "other" adapter.
        var facade = NewPaperAndOtherFacade();
        using var viewModel = CreateViewModel(
            exchanges: facade,
            feeSchedules: IdleSelectableFees());
        viewModel.SymbolInput = "BTC/USDT";
        viewModel.QuantityInput = "0.01";
        viewModel.PriceInput = "50000";
        viewModel.SelectedTypeIndex = (int)OrderType.Limit;
        viewModel.FeeEstimates.Should().ContainSingle()
            .Which.Should().Be("[Fees.EstimateMakerFormat:0.50000000]");

        // Act: the toolbar switch while the dialog stays open.
        await facade.SwitchToAsync("other");

        // Assert: the estimate flips to the honest dash without reopening.
        viewModel.FeeEstimates.Should().ContainSingle()
            .Which.Should().Be("[Fees.EstimateMakerFormat:[Fees.Unknown]]");
    }

    #endregion
}

