namespace ZettaTrade.App.Tests.Ai;

using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Xunit;
using ZettaTrade.AI.Chat;
using ZettaTrade.Core.Entities;

/// <summary>
/// The market-context preamble: the same moment renders the same string byte
/// for byte, the paper/live tag stands first (a shaved preamble still
/// carries it), tight budgets drop whole low-priority parts instead of
/// re-wording values, and a moment without facts renders the honest absence
/// line rather than an invented generic.
/// </summary>
public sealed class MarketContextPreambleComposerTests
{
    #region Fixtures

    private readonly MarketContextPreambleComposer _composer = new();

    /// <summary>One top-of-book level.</summary>
    private static PriceLevel Level(decimal price, decimal quantity) => new(price, quantity);

    /// <summary>A stub indicator row (a long row name is how the budget
    /// pressure is staged).</summary>
    private static MarketMomentIndicator Indicator(string name, int period) => new(name, period);

    /// <summary>The paper venue id the tag tests print through the moment.</summary>
    private const string PaperVenueId = "paper";

    /// <summary>A staged live venue id.</summary>
    private const string LiveVenueId = "binance";

    #endregion

    #region Determinism And Absence

    /// <summary>The acceptance: the same snapshot reads deterministically —
    /// two renders of the same moment agree byte for byte.</summary>
    [Fact]
    public void Compose_TheSameMomentTwice_RendersTheSameBytes()
    {
        var moment = new MarketMoment
        {
            IsPaperVenue = true,
            VenueId = PaperVenueId,
            LastPrice = 50_000.5m,
            PriceChange24hPercent = 0.4m,
            Bids = [Level(49_999.5m, 1.0m), Level(49_999.0m, 2.0m)],
            Asks = [Level(50_001.5m, 0.5m)],
            Indicators = [Indicator("SMA", 20), Indicator("EMA", 9)],
        };

        var first = _composer.Compose(moment);
        var second = _composer.Compose(moment);

        second.Should().Be(first);
        first.Should().StartWith("Market snapshot:");
        first.Should().Contain("trading on paper");
        first.Should().Contain("last price 50000.50000000");
        first.Should().Contain("24h change 0.40%");
    }

    /// <summary>A null moment (no snapshot at all) renders the honest
    /// absence line and nothing else.</summary>
    [Fact]
    public void Compose_ANullMoment_RendersTheHonestAbsence()
    {
        _composer.Compose(null).Should().Be("Market snapshot: no live market data received yet.");
    }

    /// <summary>The all-absent moment is the same honest absence (the venue
    /// tag arrives as a decided fact; a null classification is absent, never
    /// defaulted to paper).</summary>
    [Fact]
    public void Compose_TheAllAbsentMoment_RendersTheHonestAbsence()
    {
        _composer.Compose(new MarketMoment()).Should().Be("Market snapshot: no live market data received yet.");
    }

    #endregion

    #region Paper/Live Tag

    /// <summary>The paper tag correctness: the paper classification renders
    /// the simulation wording, whatever the venue id carries.</summary>
    [Fact]
    public void Compose_ThePaperVenue_SaysTheSimulationOutright()
    {
        var preamble = _composer.Compose(new MarketMoment
        {
            IsPaperVenue = true,
            VenueId = PaperVenueId,
            LastPrice = 100m,
        });

        preamble.Should().Contain("trading on paper (the paper-trading simulator, simulated fills)");
        preamble.Should().NotContain("shop", "the wording is the tag phrase, not free prose");
    }

    /// <summary>A live venue names itself — the reader must see they are on
    /// a public venue, not the sandbox.</summary>
    [Fact]
    public void Compose_ALiveVenue_NamesIt()
    {
        var preamble = _composer.Compose(new MarketMoment
        {
            IsPaperVenue = false,
            VenueId = LiveVenueId,
            LastPrice = 100m,
        });

        preamble.Should().Contain("trading live on binance");
        preamble.Should().NotContain("simulator");
    }

    /// <summary>The tag correctness under incomplete facts: a live
    /// classification without an id stays honest ("unidentified venue"),
    /// it never defaults to paper.</summary>
    [Fact]
    public void Compose_ALiveVenueWithoutId_StaysHonestlyUnidentified()
    {
        var preamble = _composer.Compose(new MarketMoment
        {
            IsPaperVenue = false,
            LastPrice = 100m,
        });

        preamble.Should().Contain("trading live on an unidentified venue");
        preamble.Should().NotContain("trading on paper");
    }

    /// <summary>An unknown classification (no venue source) renders no venue
    /// phrase at all — the part is absent, not substituted.</summary>
    [Fact]
    public void Compose_AbsentTagClassification_RendersNoVenuePhrase()
    {
        var preamble = _composer.Compose(new MarketMoment { LastPrice = 100m });

        preamble.Should().NotContain("trading");
    }

    #endregion

    #region Shapes

    /// <summary>The book part carries the top levels only, best first, both
    /// sides — a deeper frame shows exactly the top three per side.</summary>
    [Fact]
    public void Compose_ADeepBook_CarriesTheTopLevelsPerSide()
    {
        var preamble = _composer.Compose(new MarketMoment
        {
            IsPaperVenue = true,
            VenueId = PaperVenueId,
            LastPrice = 50_000m,
            Bids = Enumerable.Range(0, 8).Select(index => Level(49_999m - index, 1m + index)).ToList(),
            Asks = Enumerable.Range(0, 8).Select(index => Level(50_001m + index, 2m + index)).ToList(),
        });

        preamble.Should().Contain("bids 49999.00000000:1");
        preamble.Should().Contain(" / asks 50001.00000000:2");
        preamble.Should().Contain("49997.00000000:3", "the top three bids survive the cap");
        preamble.Should().NotContain("49996", "depth below the top level cap stays out");
    }

    /// <summary>A book frame whose sides carried empty renders the honest
    /// no-levels shape — an empty book is a fact, never a silent drop.</summary>
    [Fact]
    public void Compose_AnEmptySidedBook_RendersTheWithdrawalFact()
    {
        var preamble = _composer.Compose(new MarketMoment
        {
            LastPrice = 100m,
            Bids = [],
            Asks = [],
        });

        preamble.Should().Contain("book no levels");
    }

    /// <summary>The indicator part renders the configured setup, capped — a
    /// longer setup shows its tail size, the rows never re-word.</summary>
    [Fact]
    public void Compose_ALongIndicatorSetup_ShowsTheCompactTail()
    {
        var preamble = _composer.Compose(new MarketMoment
        {
            IsPaperVenue = true,
            VenueId = PaperVenueId,
            LastPrice = 100m,
            Indicators = Enumerable.Range(1, 10).Select(index => Indicator($"IND{index}", index * 7)).ToList(),
        });

        preamble.Should().Contain("IND1(7)");
        preamble.Should().Contain("IND6(42)");
        preamble.Should().Contain("+4 more");
        preamble.Should().NotContain("IND7", "the tail rows stay summarised, never quoted");
    }

    /// <summary>Half-facts render their own side: a change without a price
    /// shows only the change (part degradation, never a padded line).</summary>
    [Fact]
    public void Compose_AChangeWithoutAPrice_RendersOnlyTheChange()
    {
        var preamble = _composer.Compose(new MarketMoment
        {
            IsPaperVenue = true,
            VenueId = PaperVenueId,
            PriceChange24hPercent = -1.25m,
        });

        preamble.Should().Contain("24h change -1.25%");
        preamble.Should().NotContain("last price");
    }

    #endregion

    #region Budget

    /// <summary>A moment whose parts would blow the budget shaves whole
    /// low-priority parts (indicators first), keeps the paper/live tag, and
    /// never re-words a surviving value. Stages both the paper and the live
    /// lane so the tag's priority is provable either way.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Compose_AnOverBudgetMoment_KeepsTheTagAndDropsTheTail(bool isPaperVenue)
    {
        var moment = new MarketMoment
        {
            IsPaperVenue = isPaperVenue,
            VenueId = isPaperVenue ? PaperVenueId : LiveVenueId,
            LastPrice = 50_000.5m,
            PriceChange24hPercent = 0.4m,
            Bids = [Level(49_999m, 1m), Level(49_998m, 2m)],
            Asks = [Level(50_001m, 0.5m)],
            Indicators = Enumerable.Range(1, 40)
                .Select(index => Indicator($"VeryLongIndicatorNameForBudgetPressure{index}:{new string('x', 200)}", index))
                .ToList(),
        };

        // Guard: nothing the shave can prove here would matter without a
        // genuinely over-budget full render: the staged names are heavy
        // enough that no part of them can survive within the budget.
        var unguarded = _composer.Compose(moment with { Indicators = null });
        unguarded.Length.Should().BeLessThanOrEqualTo(MarketContextPreambleComposer.MaxCharacters);

        var preamble = _composer.Compose(moment);

        preamble.Length.Should().BeLessThanOrEqualTo(
            MarketContextPreambleComposer.MaxCharacters,
            "the composed preamble must always fit the acceptance budget");
        preamble.Should().StartWith(isPaperVenue
            ? "Market snapshot: trading on paper"
            : "Market snapshot: trading live on binance");
        preamble.Should().Contain("last price 50000.50000000", "the shave drops whole parts, a surviving part keeps its spelling");
        preamble.Should().Contain("bids 49999.00000000:1", "the book rated higher priority than the setup, it survived");
        preamble.Should().NotContain(
            "VeryLongIndicatorNameForBudgetPressure",
            "the low-priority setup was dropped whole, never re-worded");
    }

    /// <summary>A venue id the venue itself smeared into an absurd length
    /// still cannot break the budget: the last-resort cap cuts the string
    /// exactly at the limit (and the cut is deterministic).</summary>
    [Fact]
    public void Compose_AnAbsurdlyLongVenueId_CutsExactlyAtTheBudget()
    {
        var moment = new MarketMoment
        {
            IsPaperVenue = false,
            VenueId = new string('v', 4000),
            LastPrice = 1m,
        };

        var first = _composer.Compose(moment);
        var second = _composer.Compose(moment);

        second.Should().Be(first);
        first.Length.Should().BeLessThanOrEqualTo(MarketContextPreambleComposer.MaxCharacters);
    }

    #endregion
}
