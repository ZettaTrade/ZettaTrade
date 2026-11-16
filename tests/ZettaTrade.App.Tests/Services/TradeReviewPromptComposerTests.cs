namespace ZettaTrade.App.Tests.Services;

using FluentAssertions;
using Xunit;
using ZettaTrade.App.Services;
using ZettaTrade.App.ViewModels;

/// <summary>
/// The trade-review prompt's pins (T-255): every journal fact rides its
/// own labeled line, the side reads off the structural flags (the
/// display side is a localized surface — the prompt is not), the
/// verdict names win/loss/scratch honestly, an absent free-text fact
/// omits its line (never a fabricated "none"), and one long journal
/// note cannot unbound the prompt (the deterministic cap). The composer
/// is pure — a row in, text out; the read-only guarantee is the
/// signature's.
/// </summary>
public sealed class TradeReviewPromptComposerTests
{
    [Fact]
    public void AFullRow_AnswersEveryFactOnItsOwnLine()
    {
        var row = Row(
            note: "scaled out at the range high",
            tags: "trend, h1");

        var prompt = TradeReviewPromptComposer.Compose(row);

        prompt.Should().StartWith("Review the following closed trade from my trading journal.");
        prompt.Should().Contain("Closed at: 2026-11-16 10:00");
        prompt.Should().Contain("Exchange: Binance");
        prompt.Should().Contain("Symbol: BTC/USDT");
        prompt.Should().Contain("Side: BUY", "the side reads off the structural flag, not the localized display text");
        prompt.Should().Contain("Quantity: 0.5");
        prompt.Should().Contain("Entry price: 65,000.00");
        prompt.Should().Contain("Exit price: 65,500.00");
        prompt.Should().Contain("Realized PnL: +250.00 (a win)");
        prompt.Should().Contain("Adverse excursion (MAE): -120.00");
        prompt.Should().Contain("Favorable excursion (MFE): +310.00");
        prompt.Should().Contain("Journal note: scaled out at the range high");
        prompt.Should().Contain("Tags: trend, h1");
        prompt.Should().EndWith("the journal facts alone.");
    }

    [Fact]
    public void AnAbsentNoteAndTags_OmitTheirLines_NeverAFabricatedNone()
    {
        var prompt = TradeReviewPromptComposer.Compose(Row());

        prompt.Should().NotContain("Journal note");
        prompt.Should().NotContain("Tags:");
    }

    [Fact]
    public void TheVerdict_NamesWinLossAndScratch()
    {
        TradeReviewPromptComposer.Compose(Row(isWinning: true, isLosing: false))
            .Should().Contain("(a win)");
        TradeReviewPromptComposer.Compose(Row(isWinning: false, isLosing: true))
            .Should().Contain("(a loss)");
        TradeReviewPromptComposer.Compose(Row(isWinning: false, isLosing: false))
            .Should().Contain("(scratch)", "a flat close is neither — the honest verdict");
    }

    [Fact]
    public void TheSellSide_ReadsOffTheStructuralFlag()
    {
        var row = Row();
        var sell = new TradeRealizedRowViewModel
        {
            ExchangeId = row.ExchangeId,
            OrderId = row.OrderId,
            ClosedAt = row.ClosedAt,
            Exchange = row.Exchange,
            Symbol = row.Symbol,
            Side = "Sell",
            IsBuySide = false,
            IsSellSide = true,
            Quantity = row.Quantity,
            EntryPrice = row.EntryPrice,
            ExitPrice = row.ExitPrice,
            Pnl = row.Pnl,
            IsWinning = row.IsWinning,
            IsLosing = row.IsLosing,
            Mae = row.Mae,
            Mfe = row.Mfe,
            Note = row.Note,
            Tags = row.Tags,
        };

        TradeReviewPromptComposer.Compose(sell).Should().Contain("Side: SELL");
    }

    [Fact]
    public void AStreetOfANote_RidesTheDeterministicCap()
    {
        var longNote = new string('x', TradeReviewPromptComposer.MaxFreeTextCharacters + 500);

        var prompt = TradeReviewPromptComposer.Compose(Row(note: longNote));

        var noteStart = prompt.IndexOf("Journal note: ", System.StringComparison.Ordinal);
        noteStart.Should().BeGreaterThanOrEqualTo(0);
        var noteStartAfterLabel = noteStart + "Journal note: ".Length;
        var noteEnd = prompt.IndexOf('\n', noteStartAfterLabel);
        noteEnd.Should().BeGreaterThan(noteStartAfterLabel, "the note line ends before the closing ask");
        var note = prompt.Substring(noteStartAfterLabel, noteEnd - noteStartAfterLabel);
        note.Length.Should().BeLessThanOrEqualTo(
            TradeReviewPromptComposer.MaxFreeTextCharacters + "...".Length,
            "the note rides the cap plus the truncation marker");
        note.Should().EndWith("...", "a shaved field says so");
    }

    [Fact]
    public void ANullRow_Throws()
    {
        var compose = () => TradeReviewPromptComposer.Compose(null!);

        compose.Should().Throw<ArgumentNullException>();
    }

    private static TradeRealizedRowViewModel Row(
        string? note = null,
        string? tags = null,
        bool isWinning = true,
        bool isLosing = false) => new()
    {
        ExchangeId = "binance",
        OrderId = "ord-1",
        ClosedAt = "2026-11-16 10:00",
        Exchange = "Binance",
        Symbol = "BTC/USDT",
        Side = "Buy",
        IsBuySide = true,
        IsSellSide = false,
        Quantity = "0.5",
        EntryPrice = "65,000.00",
        ExitPrice = "65,500.00",
        Pnl = "+250.00",
        IsWinning = isWinning,
        IsLosing = isLosing,
        Mae = "-120.00",
        Mfe = "+310.00",
        Note = note,
        Tags = tags,
    };
}
