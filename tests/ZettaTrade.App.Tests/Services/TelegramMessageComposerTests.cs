namespace ZettaTrade.App.Tests.Services;

using System;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;
using ZettaTrade.App.Services;
using ZettaTrade.Core.Enums;

using static ZettaTrade.App.Tests.Services.TelegramSinkFixtures;

/// <summary>
/// The Telegram trade message's composition goldens (T-041): the composed
/// line speaks the app's shipped catalog exactly — the English and the
/// Russian fill/cancel/rejection wordings ride the resx templates with the
/// journaled row's own facts (the side's localized word, the invariant F8
/// numbers, the exchange id, the lane's tag word). A fill whose event
/// carries no price fact and a kind the sink has no vocabulary for are
/// composition faults — never a fabricated line.
/// </summary>
public sealed class TelegramMessageComposerTests
{
    #region Tests: the fill goldens

    /// <summary>An English fill speaks the catalog's fill line with the row's facts.</summary>
    [Fact]
    public void AnEnglishFill_ComposesTheCatalogLine()
    {
        var message = ComposeEnglish(
            Notification.ForJournalOutcome(
                NotificationKind.OrderFill,
                new DateTimeOffset(2026, 11, 15, 9, 0, 5, TimeSpan.Zero),
                "BTC/USDT",
                OrderSide.Buy,
                0.5m,
                price: 67_000m,
                exchange: "bybit"),
            TradingLane.Paper);

        message.Should().Be("Buy 0.50000000 BTC/USDT filled at 67000.00000000 — bybit (Paper)");
    }

    /// <summary>A Russian fill speaks the Russian catalog line word for word.</summary>
    [Fact]
    public void ARussianFill_ComposesTheCatalogLine()
    {
        var message = Compose(
            "ru",
            Notification.ForJournalOutcome(
                NotificationKind.OrderFill,
                new DateTimeOffset(2026, 11, 15, 9, 0, 5, TimeSpan.Zero),
                "BTC/USDT",
                OrderSide.Buy,
                0.5m,
                price: 67_000m,
                exchange: "bybit"),
            TradingLane.Paper);

        message.Should().Be("Купить 0.50000000 BTC/USDT исполнено по 67000.00000000 — bybit (Симулятор)");
    }

    /// <summary>A sell fill carries the sell's own word, the F8-invariant numbers and the standing lane's tag.</summary>
    [Fact]
    public void ASellFill_CarriesTheSellWord_TheF8Numbers_AndTheLaneTag()
    {
        var message = ComposeEnglish(
            Notification.ForJournalOutcome(
                NotificationKind.OrderFill,
                new DateTimeOffset(2026, 11, 15, 9, 0, 5, TimeSpan.Zero),
                "ETH/USDT",
                OrderSide.Sell,
                1234.5m,
                price: 3400.5m,
                exchange: "paper"),
            TradingLane.Testnet);

        message.Should().Be("Sell 1234.50000000 ETH/USDT filled at 3400.50000000 — paper (Testnet)");
    }

    #endregion Tests: the fill goldens

    #region Tests: the cancel goldens

    /// <summary>A cancellation whose symbol the journal recovered carries the symbol line.</summary>
    [Fact]
    public void ACancelWithSymbol_ComposesTheSymbolLine()
    {
        var message = ComposeEnglish(
            Notification.ForJournalOutcome(
                NotificationKind.OrderCancel,
                new DateTimeOffset(2026, 11, 15, 9, 0, 5, TimeSpan.Zero),
                "BTC/USDT",
                OrderSide.Buy,
                0m,
                exchange: "bybit"),
            TradingLane.Paper);

        message.Should().Be("BTC/USDT order cancelled — bybit (Paper)");
    }

    /// <summary>The Russian cancellation line speaks the Russian catalog's own wording.</summary>
    [Fact]
    public void ARussianCancelWithSymbol_ComposesTheSymbolLine()
    {
        var message = Compose(
            "ru",
            Notification.ForJournalOutcome(
                NotificationKind.OrderCancel,
                new DateTimeOffset(2026, 11, 15, 9, 0, 5, TimeSpan.Zero),
                "BTC/USDT",
                OrderSide.Buy,
                0m,
                exchange: "bybit"),
            TradingLane.Paper);

        message.Should().Be("Ордер отменён: BTC/USDT — bybit (Симулятор)");
    }

    /// <summary>A cancellation whose symbol could not be recovered composes the symbolless line.</summary>
    [Fact]
    public void ACancelWithoutSymbol_ComposesTheSymbollessLine()
    {
        var message = ComposeEnglish(
            Notification.ForJournalOutcome(
                NotificationKind.OrderCancel,
                new DateTimeOffset(2026, 11, 15, 9, 0, 5, TimeSpan.Zero),
                string.Empty,
                OrderSide.Buy,
                0m,
                exchange: "paper"),
            TradingLane.Paper);

        message.Should().Be("Order cancelled — paper (Paper)");
    }

    #endregion Tests: the cancel goldens

    #region Tests: the rejection golden

    /// <summary>A rejection carries the symbol, the adapter's own reason, the exchange and the lane tag.</summary>
    [Fact]
    public void AReject_ComposesTheCatalogLine()
    {
        var message = ComposeEnglish(
            Notification.ForJournalOutcome(
                NotificationKind.OrderReject,
                new DateTimeOffset(2026, 11, 15, 9, 0, 5, TimeSpan.Zero),
                "BTC/USDT",
                OrderSide.Buy,
                0.02m,
                reason: "the venue refused the price",
                exchange: "bybit"),
            TradingLane.Paper);

        message.Should().Be("Order rejected: BTC/USDT — the venue refused the price (bybit, Paper)");
    }

    /// <summary>The Russian rejection line speaks the Russian catalog's own wording.</summary>
    [Fact]
    public void ARussianReject_ComposesTheCatalogLine()
    {
        var message = Compose(
            "ru",
            Notification.ForJournalOutcome(
                NotificationKind.OrderReject,
                new DateTimeOffset(2026, 11, 15, 9, 0, 5, TimeSpan.Zero),
                "BTC/USDT",
                OrderSide.Buy,
                0.02m,
                reason: "отказ по цене",
                exchange: "bybit"),
            TradingLane.Paper);

        message.Should().Be("Ордер отклонён: BTC/USDT — отказ по цене (bybit, Симулятор)");
    }

    #endregion Tests: the rejection golden

    #region Tests: the composition faults

    /// <summary>A fill whose event carries no price fact is a surface-contract fault: never a fabricated zero.</summary>
    [Fact]
    public void AFillWithoutPriceFact_FaultsTheComposition()
    {
        var act = () => ComposeEnglish(
            Notification.ForJournalOutcome(
                NotificationKind.OrderFill,
                new DateTimeOffset(2026, 11, 15, 9, 0, 5, TimeSpan.Zero),
                "BTC/USDT",
                OrderSide.Buy,
                0.5m,
                exchange: "bybit"),
            TradingLane.Paper);

        act.Should().Throw<InvalidOperationException>(
            "the message would fabricate a price the event surface does not carry");
    }

    /// <summary>A kind the sink has no vocabulary for is a pipeline design fault, never a silently substituted line.</summary>
    [Fact]
    public void AnUncomposedKind_FaultsTheComposition()
    {
        var act = () => ComposeEnglish(
            Notification.ForAdapterState(
                "Paper",
                AdapterHealthState.Connected,
                new DateTimeOffset(2026, 11, 15, 9, 0, 5, TimeSpan.Zero)),
            TradingLane.Paper);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    #endregion Tests: the composition faults

    #region Helpers

    /// <summary>Compose under the English catalog (the shipped default language).</summary>
    private static string ComposeEnglish(Notification source, TradingLane lane) => Compose("en", source, lane);

    /// <summary>Compose under the seeded catalog language.</summary>
    private static string Compose(string cultureCode, Notification source, TradingLane lane) =>
        TelegramMessageComposer.Compose(
            source,
            lane,
            new ResxCatalogLocalization(cultureCode));

    #endregion Helpers
}
