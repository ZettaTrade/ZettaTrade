namespace ZettaTrade.App.Tests.Services;

using System;
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
using ZettaTrade.Infrastructure.Fees;
using ZettaTrade.Infrastructure.Persistence;

/// <summary>
/// Tests for the paper reset service (T-192): the ONE confirmation naming
/// the scope (the chosen balance plus the working-order and open-position
/// counts the fresh plan read counted), the declined confirmation touching
/// nothing, the confirmed reset wiping the paper venue's scope only (the
/// adapter's surface emptied; the journal and the persisted history
/// commanded for the paper exchange alone; the chosen balance persisted),
/// and the starting-balance read (the persisted choice, the shipped default
/// before one exists). The wipe facts ride the REAL paper adapter; the
/// journal, the history and the settings ride substitutes.
/// </summary>
public sealed class PaperResetServiceTests
{
    #region Test Constants

    private static readonly Symbol BtcUsdt = Symbol.Parse("BTC/USDT");

    /// <summary>Close of the fake reference bar (the market anchor and the paper fill price).</summary>
    private const decimal ReferenceClose = 50_000m;

    private static readonly DateTimeOffset ReferenceBarTime =
        new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    #endregion Test Constants

    #region Test Fields

    private readonly IConfirmationService _confirmation = Substitute.For<IConfirmationService>();
    private readonly ILocalizationManager _localization = Substitute.For<ILocalizationManager>();
    private readonly IOrderHistoryStore _history = Substitute.For<IOrderHistoryStore>();
    private readonly IOrderJournal _journal = Substitute.For<IOrderJournal>();
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();

    #endregion Test Fields

    public PaperResetServiceTests()
    {
        // The fake resolves dynamic messages in a predictable, test-visible
        // form: the key plus the arguments, e.g. "[Trading.PaperResetDoneFormat:25000|0]".
        _localization.GetString(Arg.Any<string>()).Returns(call =>
            $"[{call.ArgAt<string>(0)}]");
        _localization.GetString(Arg.Any<string>(), Arg.Any<object[]>()).Returns(call =>
            $"[{call.ArgAt<string>(0)}:{FormatArgs(call.ArgAt<object[]>(1))}]");
    }

    #region Declined

    [Fact]
    public async Task Reset_DeclinedConfirmation_TouchesNothing()
    {
        // Arrange: a live long and a working exit; the confirmation refuses.
        var paper = CreatePaperAdapter();
        await paper.PlaceOrderAsync(MarketBuy());
        await paper.PlaceOrderAsync(RestingSell());
        _confirmation.RequestConfirmationAsync(Arg.Any<ConfirmationRequest>(), Arg.Any<CancellationToken>())
            .Returns(false);
        var service = CreateService(paper);

        // Act
        var outcome = await service.ResetAsync(25_000m);

        // Assert: the declined outcome is silent, and the whole paper scope
        // stands exactly as the confirmation named it.
        outcome.Done.Should().BeFalse();
        outcome.Summary.Should().BeNull("a declined reset reports nothing");
        (await paper.GetOpenOrdersAsync()).Should().ContainSingle("the refusal left the working order standing");
        (await paper.GetPositionsAsync()).Should().ContainSingle("the refusal left the position standing");
        _journal.DidNotReceive().RemoveVenue(Arg.Any<string>());
        await _history.DidNotReceive().DeleteExchangeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _settings.DidNotReceive().SetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal>());
    }

    #endregion

    #region Confirmed

    [Fact]
    public async Task Reset_Confirmed_WipesThePaperScopeAndCommandsOnlyThePaperJournal()
    {
        // Arrange: a live long and a working exit; the confirmation approves.
        var paper = CreatePaperAdapter();
        await paper.PlaceOrderAsync(MarketBuy());
        await paper.PlaceOrderAsync(RestingSell());
        _confirmation.RequestConfirmationAsync(Arg.Any<ConfirmationRequest>(), Arg.Any<CancellationToken>())
            .Returns(true);
        var service = CreateService(paper);

        // Act
        var outcome = await service.ResetAsync(25_000m);

        // Assert: the simulation restarted at the chosen balance, the journal
        // wipes named ONLY the paper exchange (live and testnet rows are not
        // the reset's to touch), and the finished summary rode back.
        outcome.Done.Should().BeTrue();
        outcome.Summary.Should().StartWith("[Trading.PaperResetDoneFormat:");
        (await paper.GetOpenOrdersAsync()).Should().BeEmpty();
        (await paper.GetPositionsAsync()).Should().BeEmpty();
        (await paper.GetBalancesAsync())[PaperTradingAdapter.QuoteCurrency].Amount.Should().Be(25_000m);
        _journal.Received(1).RemoveVenue(PaperTradingAdapter.ExchangeId);
        await _history.Received(1).DeleteExchangeAsync(PaperTradingAdapter.ExchangeId, Arg.Any<CancellationToken>());
        await _history.DidNotReceive().DeleteExchangeAsync(
            Arg.Is<string>(exchangeId => exchangeId != PaperTradingAdapter.ExchangeId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Reset_TheOneConfirmationNamesTheBalanceAndTheScope()
    {
        // Arrange: one working order and one open position are the scope the
        // fresh plan read counted; the confirmation's answer is irrelevant
        // to the summary check.
        var paper = CreatePaperAdapter();
        await paper.PlaceOrderAsync(MarketBuy());
        await paper.PlaceOrderAsync(RestingSell());
        _confirmation.RequestConfirmationAsync(Arg.Any<ConfirmationRequest>(), Arg.Any<CancellationToken>())
            .Returns(false);
        var service = CreateService(paper);

        // Act
        await service.ResetAsync(25_000m);

        // Assert: exactly ONE confirmation carrying the key, the chosen
        // balance and the two counts, in the template's argument order.
        await _confirmation.Received(1).RequestConfirmationAsync(
            Arg.Is<ConfirmationRequest>(request =>
                request.MessageKey == "Trading.ConfirmPaperResetFormat"
                && request.Arguments.Length == 3
                && Equals(request.Arguments[0], "25000")
                && Equals(request.Arguments[1], "1")
                && Equals(request.Arguments[2], "1")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Reset_TheChosenBalancePersists()
    {
        // Arrange
        var paper = CreatePaperAdapter();
        _confirmation.RequestConfirmationAsync(Arg.Any<ConfirmationRequest>(), Arg.Any<CancellationToken>())
            .Returns(true);
        var service = CreateService(paper);

        // Act
        var outcome = await service.ResetAsync(25_000m);

        // Assert: the choice rides the Paper settings section for the next
        // session and the next reset's prefill.
        outcome.Done.Should().BeTrue();
        await _settings.Received(1).SetAsync(
            UiSettingsKeys.PaperSection,
            UiSettingsKeys.PaperStartingBalance,
            25_000m);
    }

    #endregion

    #region The Prefill Read

    [Fact]
    public async Task GetStartingBalance_ReadsThePersistedChoice_AndShipsTheDefault()
    {
        // Arrange: a persisted install reads its own stored choice.
        var persistedSettings = Substitute.For<ISettingsService>();
        persistedSettings
            .GetAsync<decimal>(UiSettingsKeys.PaperSection, UiSettingsKeys.PaperStartingBalance, Arg.Any<decimal>())
            .Returns(12_345m);
        var persisted = CreateService(settings: persistedSettings);

        // A fresh install has no stored value: the default argument flows
        // (an unstubbed read never silently substitutes zero).
        var freshSettings = Substitute.For<ISettingsService>();
        freshSettings
            .GetAsync<decimal>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal>())
            .Returns(call => call.ArgAt<decimal>(2));
        var fresh = CreateService(settings: freshSettings);

        // Act + Assert
        (await persisted.GetStartingBalanceAsync()).Should().Be(12_345m);
        (await fresh.GetStartingBalanceAsync()).Should().Be(UiSettingsKeys.DefaultPaperStartingBalance);
    }

    #endregion

    #region Test Helpers

    private PaperResetService CreateService(
        PaperTradingAdapter? paper = null,
        ISettingsService? settings = null) => new(
        paper ?? CreatePaperAdapter(),
        settings ?? _settings,
        _confirmation,
        new OrderConfirmationComposer(_localization),
        _journal,
        _history,
        _localization,
        NullLogger<PaperResetService>.Instance);

    /// <summary>The real paper adapter over a market-data stub: one reference
    /// H1 bar at the fake close, one shared test-controlled ticker subject.</summary>
    private static PaperTradingAdapter CreatePaperAdapter()
    {
        var marketData = Substitute.For<IMarketDataAdapter>();
        marketData.GetBarsAsync(
                Arg.Any<Symbol>(),
                Arg.Any<TimeFrame>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(new[] { new Bar(
                ReferenceBarTime,
                49_500m,
                50_500m,
                49_000m,
                ReferenceClose,
                1_000m) });
        marketData.SubscribeTicker(Arg.Any<Symbol>()).Returns(new Subject<Ticker>());
        return new PaperTradingAdapter(
            marketData,
            PositionActionsServiceTests.CreateFeeSchedules(),
            NullLogger<PaperTradingAdapter>.Instance);
    }

    private static OrderRequest MarketBuy() => new()
    {
        Symbol = BtcUsdt,
        Side = OrderSide.Buy,
        Type = OrderType.Market,
        Quantity = new Quantity(0.01m),
    };

    /// <summary>A reduce-only sell limit resting above the market (the exit
    /// shape the protective pair places).</summary>
    private static OrderRequest RestingSell() => new()
    {
        Symbol = BtcUsdt,
        Side = OrderSide.Sell,
        Type = OrderType.Limit,
        Quantity = new Quantity(0.01m),
        Price = new Price(60_000m),
        ReduceOnly = true,
    };

    private static string FormatArgs(object[] args) => string.Join("|", args);

    #endregion
}
