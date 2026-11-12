namespace ZettaTrade.App.Tests.ViewModels;

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
/// The T-156 ticket redesign's view-model surface: the segmented Buy/Sell
/// pair rides <see cref="NewOrderDialogViewModel.SelectedSideIndex"/> (the
/// single source the prefill and the pipeline keep writing), the quantity
/// and price captions grow unit hints from the parsed symbol, and the
/// attempt's report routes by owner — a field-tagged parse refusal lands in
/// its field's inline error while <see cref="NewOrderDialogViewModel.ErrorMessage"/>
/// keeps the full report contract (the behavior-parity battery reads it
/// verbatim), the flow failures staying the summary line's residents.
/// </summary>
public sealed class TicketRedesignViewModelTests
{
    private readonly ITradingAdapter _trading = Substitute.For<ITradingAdapter>();
    private readonly IRiskManager _riskManager = Substitute.For<IRiskManager>();
    private readonly IConfirmationService _confirmation = Substitute.For<IConfirmationService>();
    private readonly ILocalizationManager _localization = Substitute.For<ILocalizationManager>();
    private readonly IOrderJournal _journal = new OrderJournal();
    private readonly IOrderHistoryStore _history = Substitute.For<IOrderHistoryStore>();
    private readonly Subject<SettingsChange> _settingsChanges = new();
    private readonly IDailyLossLimit _dailyLoss = Substitute.For<IDailyLossLimit>();

    public TicketRedesignViewModelTests()
    {
        // The same test-visible localization fake the behavior-parity
        // battery rides: the key plus the first argument.
        _localization.GetString(Arg.Any<string>()).Returns(call =>
            $"[{call.ArgAt<string>(0)}]");
        _localization.GetString(Arg.Any<string>(), Arg.Any<object[]>()).Returns(call =>
            $"[{call.ArgAt<string>(0)}:{string.Join("|", call.ArgAt<object[]>(1))}]");

        _trading.Id.Returns(PaperTradingAdapter.ExchangeId);

        // The daily loss governor answers its shipped OFF posture: the
        // banner stays hidden unless a test arms it.
        _dailyLoss.ReadStateAsync(Arg.Any<Symbol>(), Arg.Any<CancellationToken>())
            .Returns(new DailyLossState(false, false, 0m, 1000m, "USDT", "paper"));
    }

    private NewOrderDialogViewModel CreateViewModel()
    {
        var service = new ActiveExchangeService(
            [_trading],
            Substitute.For<ISettingsService>(),
            Substitute.For<Microsoft.Extensions.Logging.ILogger<ActiveExchangeService>>());
        var settings = Substitute.For<ISettingsService>();
        settings.GetAsync<double>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double>())
            .Returns(SizerSettingsKeys.DefaultRiskPercentValue);
        settings.GetAsync<bool>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns(false);
        settings.GetAsync(Arg.Any<string>(), ConfirmSettingsKeys.SkipBelowNotionalUsd, Arg.Any<decimal>())
            .Returns(0m);
        settings.Changes.Returns(_settingsChanges);

        return new NewOrderDialogViewModel(
            _trading,
            new SymbolListProvider(
                service,
                Substitute.For<Microsoft.Extensions.Logging.ILogger<SymbolListProvider>>()),
            service,
            _riskManager,
            _confirmation,
            new OrderConfirmationComposer(_localization),
            new OrderConfirmPolicy(settings),
            _journal,
            _history,
            new FeeScheduleService(
                settings,
                Substitute.For<Microsoft.Extensions.Logging.ILogger<FeeScheduleService>>()),
            _localization,
            new PositionSizingService(
                new PaperInstrumentMetadataProvider(),
                _trading,
                Substitute.For<Microsoft.Extensions.Logging.ILogger<PositionSizingService>>()),
            settings,
            Substitute.For<Microsoft.Extensions.Logging.ILogger<NewOrderDialogViewModel>>(),
            Substitute.For<Microsoft.Extensions.Logging.ILogger<OrderSizerRowViewModel>>(),
            _dailyLoss);
    }

    private static void FillValidLimitOrder(NewOrderDialogViewModel viewModel)
    {
        viewModel.SymbolInput = "BTC/USDT";
        viewModel.QuantityInput = "0.01";
        viewModel.SelectedSideIndex = 0;
        viewModel.SelectedTypeIndex = 1; // Limit
        viewModel.PriceInput = "50000";
    }

    [Fact]
    public void SegmentedSide_TheBuySideStartsSelected()
    {
        var viewModel = CreateViewModel();

        viewModel.SelectedSideIndex.Should().Be(0);
        viewModel.IsBuySide.Should().BeTrue("the buy side is the ticket's default pick");
        viewModel.IsSellSide.Should().BeFalse();
    }

    [Fact]
    public async Task SegmentedSide_TheCommandsFlipThePickedSide()
    {
        var viewModel = CreateViewModel();

        await viewModel.SelectSellSideCommand.Execute(ReactiveUI.Primitives.RxVoid.Default);
        viewModel.SelectedSideIndex.Should().Be(1);
        viewModel.IsSellSide.Should().BeTrue();
        viewModel.IsBuySide.Should().BeFalse();

        await viewModel.SelectBuySideCommand.Execute(ReactiveUI.Primitives.RxVoid.Default);
        viewModel.SelectedSideIndex.Should().Be(0);
        viewModel.IsBuySide.Should().BeTrue();
        viewModel.IsSellSide.Should().BeFalse();
    }

    [Fact]
    public void SegmentedSide_ThePrefillWritesTheIndexAndTheSegmentsFollow()
    {
        var viewModel = CreateViewModel();

        viewModel.Prefill(new OrderPrefill(
            Symbol.Parse("BTC/USDT"),
            OrderSide.Sell,
            66993.30m));

        viewModel.SelectedSideIndex.Should().Be(1);
        viewModel.IsSellSide.Should().BeTrue("a clicked ask sells");
        viewModel.IsBuySide.Should().BeFalse();
    }

    [Fact]
    public void UnitHints_TheParsedSymbolNamesTheCaptionUnits()
    {
        var viewModel = CreateViewModel();
        viewModel.QuantityUnitHint.Should().BeEmpty("no symbol names no unit");
        viewModel.PriceUnitHint.Should().BeEmpty();

        viewModel.SymbolInput = "BTC/USDT";

        viewModel.QuantityUnitHint.Should().Be("[NewOrder.UnitHintFormat:BTC]");
        viewModel.PriceUnitHint.Should().Be("[NewOrder.UnitHintFormat:USDT]");
    }

    [Fact]
    public void UnitHints_AQuoteLessSymbolHintsTheBaseOnly()
    {
        var viewModel = CreateViewModel();
        viewModel.SymbolInput = "ETH";

        // Symbol.Parse is the symbol's source of truth: a quote-less asset
        // sizes a quantity but prices nothing.
        viewModel.QuantityUnitHint.Should().Be("[NewOrder.UnitHintFormat:ETH]");
        viewModel.PriceUnitHint.Should().BeEmpty("a quote-less price has no unit to name");

        viewModel.SymbolInput = "  ";

        viewModel.QuantityUnitHint.Should().BeEmpty();
        viewModel.PriceUnitHint.Should().BeEmpty();
    }

    [Fact]
    public async Task FieldErrors_AnUnparsableQuantityRendersNextToItsField()
    {
        var viewModel = CreateViewModel();
        viewModel.SymbolInput = "BTC/USDT";
        viewModel.QuantityInput = "0,0x1";
        viewModel.SelectedTypeIndex = 1; // Limit
        viewModel.PriceInput = "50000";

        await viewModel.PlaceOrderCommand.Execute(ReactiveUI.Primitives.RxVoid.Default);

        viewModel.ErrorMessage.Should().Contain("NewOrder.QuantityNotANumber")
            .And.Contain("0,0x1");
        viewModel.QuantityError.Should().Be(viewModel.ErrorMessage,
            "the quantity field owns the refusal and shows it inline");
        viewModel.HasQuantityError.Should().BeTrue();
        viewModel.HasFieldError.Should().BeTrue();
        viewModel.HasSummaryError.Should().BeFalse(
            "a field-tagged refusal is not the summary line's resident");
        viewModel.SymbolError.Should().BeNull();
        viewModel.PriceError.Should().BeNull();
    }

    [Fact]
    public async Task FieldErrors_AMissingSymbolRendersNextToItsField()
    {
        var viewModel = CreateViewModel();
        viewModel.QuantityInput = "0.01";

        await viewModel.PlaceOrderCommand.Execute(ReactiveUI.Primitives.RxVoid.Default);

        viewModel.ErrorMessage.Should().Be("[NewOrder.SymbolMissing]");
        viewModel.SymbolError.Should().Be(viewModel.ErrorMessage);
        viewModel.HasSymbolError.Should().BeTrue();
        viewModel.HasSummaryError.Should().BeFalse();
    }

    [Fact]
    public async Task FieldErrors_TheOcoStopLimitLegOwnsItsRefusal()
    {
        var viewModel = CreateViewModel();
        viewModel.SymbolInput = "BTC/USDT";
        viewModel.QuantityInput = "0.2";
        viewModel.SelectedTypeIndex = 4; // OCO
        viewModel.PriceInput = "51000";
        viewModel.StopTriggerInput = "49000";
        viewModel.OcoStopLimitInput = "  ";

        await viewModel.PlaceOrderCommand.Execute(ReactiveUI.Primitives.RxVoid.Default);

        viewModel.OcoStopLimitError.Should().Be("[NewOrder.PriceRequired]",
            "the stop-loss leg's limit field is its own inline surface");
        viewModel.HasOcoStopLimitError.Should().BeTrue();
        viewModel.PriceError.Should().BeNull("the take-profit leg parsed cleanly");
        viewModel.TriggerError.Should().BeNull();
    }

    [Fact]
    public async Task FlowFailures_ARiskRejectionStaysOnTheSummary()
    {
        var viewModel = CreateViewModel();
        FillValidLimitOrder(viewModel);
        _riskManager.CheckOrderAsync(Arg.Any<OrderRequest>())
            .Returns(RiskCheckResult.Rejected("too aggressive for this account"));

        await viewModel.PlaceOrderCommand.Execute(ReactiveUI.Primitives.RxVoid.Default);

        viewModel.ErrorMessage.Should().Contain("NewOrder.OrderRejected");
        viewModel.HasSummaryError.Should().BeTrue(
            "a risk rejection has no single owning field");
        viewModel.HasFieldError.Should().BeFalse();
        viewModel.QuantityError.Should().BeNull();
        viewModel.PriceError.Should().BeNull();
    }

    [Fact]
    public async Task FieldErrors_ClearOnTheNextAttempt()
    {
        var viewModel = CreateViewModel();
        viewModel.SymbolInput = "BTC/USDT";
        viewModel.QuantityInput = "0,0x1";
        viewModel.SelectedTypeIndex = 1;
        viewModel.PriceInput = "50000";
        await viewModel.PlaceOrderCommand.Execute(ReactiveUI.Primitives.RxVoid.Default);
        viewModel.HasQuantityError.Should().BeTrue();

        // The fixed attempt runs the full pipeline and clears the field's
        // inline line on the way.
        FillValidLimitOrder(viewModel);
        _riskManager.CheckOrderAsync(Arg.Any<OrderRequest>())
            .Returns(RiskCheckResult.Approved(requiresConfirmation: false));
        _trading.PlaceOrderAsync(Arg.Any<OrderRequest>())
            .Returns(call => new Order
            {
                Id = "paper-test",
                ExchangeId = "paper",
                Symbol = call.Arg<OrderRequest>().Symbol,
                Side = call.Arg<OrderRequest>().Side,
                Type = call.Arg<OrderRequest>().Type,
                Quantity = call.Arg<OrderRequest>().Quantity,
                Price = call.Arg<OrderRequest>().Price ?? Price.Zero,
                Status = OrderStatus.Filled,
                TimeInForce = call.Arg<OrderRequest>().TimeInForce,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        viewModel.CloseRequested.RegisterHandler(context =>
        {
            context.SetOutput(ReactiveUI.Primitives.RxVoid.Default);
            return Task.CompletedTask;
        });
        await viewModel.PlaceOrderCommand.Execute(ReactiveUI.Primitives.RxVoid.Default);

        viewModel.HasQuantityError.Should().BeFalse("a fresh attempt starts clean");
        viewModel.HasFieldError.Should().BeFalse();
        viewModel.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task FieldErrors_AnInlineLineHidesWithItsRow()
    {
        var viewModel = CreateViewModel();
        viewModel.SymbolInput = "BTC/USDT";
        viewModel.QuantityInput = "0.01";
        viewModel.SelectedTypeIndex = 1; // Limit
        viewModel.PriceInput = "   ";
        await viewModel.PlaceOrderCommand.Execute(ReactiveUI.Primitives.RxVoid.Default);
        viewModel.HasPriceError.Should().BeTrue();
        viewModel.IsPriceErrorVisible.Should().BeTrue("the price row is visible for a limit order");

        // Switching to a market order hides the price row: the inline line
        // must not dangle under a hidden field.
        viewModel.SelectedTypeIndex = 0;

        viewModel.HasPriceError.Should().BeTrue("the report itself stays until the next attempt");
        viewModel.IsPriceErrorVisible.Should().BeFalse("the line hides with its row");
    }
}
