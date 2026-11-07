namespace ZettaTrade.App.Tests.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Xunit;
using ZettaTrade.App.Services;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// The palette's fuzzy ranking (T-095): the tier order is absolute (a better
/// shape always outranks a better small bonus inside a worse tier), the
/// word-start and coverage bonuses rank inside a tier, the gap and the
/// single-typo penalties cost inside the subsequence tier only, ties keep
/// the input order, and a blank query pages the natural order untouched.
/// The tier facts assert against the matcher's own named vocabulary — no
/// re-derived magic numbers.
/// </summary>
public sealed class FuzzySymbolMatcherTests
{
    #region Helpers

    private static PaletteInstrument Instrument(string normalized) => new(
        Symbol.Parse(normalized),
        "binance",
        "Binance",
        normalized.ToUpperInvariant());

    private static IReadOnlyList<PaletteInstrument> Instruments(params string[] names) =>
        names.Select(static name => Instrument(name)).ToList();

    private static string[] RankedNames(
        IReadOnlyList<PaletteInstrument> candidates,
        string query,
        int maxResults) => FuzzySymbolMatcher
            .Match(candidates, query, maxResults)
            .Select(static ranked => ranked.Symbol.Normalized)
            .ToArray();

    /// <summary>
    /// A tier score recomposed from the matcher's own constants: the
    /// assertions read the matcher's vocabulary, never re-derived literals.
    /// </summary>
    private static int TierScore(
        int tierBase,
        int covered,
        bool wordStart,
        int gap = 0,
        bool typo = false) =>
        tierBase +
        (FuzzySymbolMatcher.CoverageBonusPerCharacter * covered) +
        (wordStart ? FuzzySymbolMatcher.WordBoundaryBonus : 0) -
        (FuzzySymbolMatcher.GapPenaltyPerCharacter * gap) -
        (typo ? FuzzySymbolMatcher.TypoPenalty : 0);

    #endregion Helpers

    #region Tier order

    [Fact]
    public void TheTiers_RankInDeclaredOrder_InsideOneKeyFamily()
    {
        var key = "BTC/USDT";

        var exact = FuzzySymbolMatcher.ScoreCandidate(key.ToUpperInvariant(), "BTC");
        var wordStart = FuzzySymbolMatcher.ScoreCandidate(key.ToUpperInvariant(), "USDT");
        var substring = FuzzySymbolMatcher.ScoreCandidate(key.ToUpperInvariant(), "C/U");
        var subsequence = FuzzySymbolMatcher.ScoreCandidate(key.ToUpperInvariant(), "BTUSDT");
        // "BTC//USDT": the doubled slash cannot align clean (no second slash
        // in the key) and aligns once the extra is skipped — the typo tier.
        var typo = FuzzySymbolMatcher.ScoreCandidate(key.ToUpperInvariant(), "BTC//USDT");

        exact.Should().BeGreaterThan(wordStart, "the exact prefix tier sits above the word-start one");
        wordStart.Should().BeGreaterThan(substring, "the word-start tier sits above the anywhere hit");
        substring.Should().BeGreaterThan(subsequence, "a contiguous hit sits above the in-order one");
        subsequence.Should().BeGreaterThan(typo, "the clean alignment outranks the typo escape");
    }

    [Fact]
    public void TheExactPrefix_CarriesTheTopTierAndTheHeadBonus()
    {
        var score = FuzzySymbolMatcher.ScoreCandidate("BTC/USDT".ToUpperInvariant(), "BTC");
        score.Should().Be(TierScore(
            FuzzySymbolMatcher.ExactPrefixTierBase,
            covered: 3,
            wordStart: true));
    }

    [Fact]
    public void USDT_OnAPair_IsAWordStartHit_WithTheBoundaryBonus()
    {
        var score = FuzzySymbolMatcher.ScoreCandidate("BTC/USDT".ToUpperInvariant(), "USDT");
        score.Should().Be(TierScore(
            FuzzySymbolMatcher.WordStartTierBase,
            covered: 4,
            wordStart: true));
    }

    [Fact]
    public void AnInteriorHit_IsASubstringTier_WithoutTheBoundaryBonus()
    {
        // "C/U" starts mid-word (after T, not after a separator): the
        // anywhere-contiguous tier stands alone.
        var score = FuzzySymbolMatcher.ScoreCandidate("BTC/USDT".ToUpperInvariant(), "C/U");
        score.Should().Be(TierScore(
            FuzzySymbolMatcher.SubstringTierBase,
            covered: 3,
            wordStart: false));
    }

    [Fact]
    public void ASubsequenceHit_CarriesItsTierAndTheGapCost()
    {
        // "BTUSDT" over "BTC/USDT": B, T, then U starts after C and / — two
        // characters skipped between two matched ones (the span before the
        // first match and after the last stays outside the match).
        var score = FuzzySymbolMatcher.ScoreCandidate("BTC/USDT".ToUpperInvariant(), "BTUSDT");
        score.Should().Be(TierScore(
            FuzzySymbolMatcher.SubsequenceTierBase,
            covered: 6,
            wordStart: true,
            gap: 2));
    }

    [Fact]
    public void SubsequenceTies_KeepTheTighterAlignment()
    {
        // Both keys hold B…T…U…S…D…T in order; the slash-free key skips one
        // character between T and U, the kept one two.
        var tighter = FuzzySymbolMatcher.ScoreCandidate("BTCUSDT".ToUpperInvariant(), "BTUSDT");
        var looser = FuzzySymbolMatcher.ScoreCandidate("BTC/USDT".ToUpperInvariant(), "BTUSDT");

        tighter.Should().Be(TierScore(
            FuzzySymbolMatcher.SubsequenceTierBase,
            covered: 6,
            wordStart: true,
            gap: 1));
        looser.Should().Be(TierScore(
            FuzzySymbolMatcher.SubsequenceTierBase,
            covered: 6,
            wordStart: true,
            gap: 2));
        tighter.Should().BeGreaterThan(looser, "fewer skipped characters between two matches ranks first");
    }

    #endregion Tier order

    #region The typo escape

    [Fact]
    public void ADoubledKeystroke_IsTheTypoEscape_AndItCosts()
    {
        // "BTC/USDTT": the final extra T is the user's doubled tap — every
        // other query character aligns in order once the extra is skipped.
        // No clean alignment can absorb nine characters into an eight-long
        // key, so the typo tier stands alone with its penalty.
        var score = FuzzySymbolMatcher.ScoreCandidate("BTC/USDT".ToUpperInvariant(), "BTC/USDTT");
        score.Should().Be(TierScore(
            FuzzySymbolMatcher.TypoSubsequenceTierBase,
            covered: 8,
            wordStart: true,
            gap: 0,
            typo: true));
    }

    [Fact]
    public void AStrayQueryCharacterAbsentFromTheKey_IsTheTypoEscapeToo()
    {
        // "BA/USDT" over "BTC/USDT": the A is foreign to the key — the typo
        // path skips the query index, the rest aligns, the escape costs.
        var score = FuzzySymbolMatcher.ScoreCandidate("BTC/USDT".ToUpperInvariant(), "BA/USDT");
        score.Should().Be(TierScore(
            FuzzySymbolMatcher.TypoSubsequenceTierBase,
            covered: 6,
            wordStart: true,
            gap: 2,
            typo: true));
    }

    [Fact]
    public void TwoStrayQueryCharacters_AreNoMatch()
    {
        // "BTCXY/USDT": two foreign characters — a single skipped query
        // index cannot absorb both, so the query reads as noise.
        var score = FuzzySymbolMatcher.ScoreCandidate("BTC/USDT".ToUpperInvariant(), "BTCXY/USDT");
        score.Should().Be(FuzzySymbolMatcher.NoMatchScore);
    }

    [Fact]
    public void ASingleCharacterQuery_NeverTakesTheTypoPath()
    {
        // A one-character query has nothing to skip over: the subsequence
        // tier either matches or not.
        var score = FuzzySymbolMatcher.ScoreCandidate("BTC/USDT".ToUpperInvariant(), "X");
        score.Should().Be(FuzzySymbolMatcher.NoMatchScore);
    }

    [Fact]
    public void AQueryCharacterMissingFromTheKey_IsNoMatch()
    {
        var score = FuzzySymbolMatcher.ScoreCandidate("BTC/USDT".ToUpperInvariant(), "XRP");
        score.Should().Be(FuzzySymbolMatcher.NoMatchScore);
    }

    [Fact]
    public void AnEmptyKeyOrQuery_ScoresNoMatch()
    {
        FuzzySymbolMatcher.ScoreCandidate(string.Empty, "BTC")
            .Should().Be(FuzzySymbolMatcher.NoMatchScore);
        FuzzySymbolMatcher.ScoreCandidate("BTC/USDT".ToUpperInvariant(), string.Empty)
            .Should().Be(FuzzySymbolMatcher.NoMatchScore);
    }

    #endregion The typo escape

    #region The Match contract

    [Fact]
    public void TheLadder_RanksTheRealCatalogShapes()
    {
        // The user-facing check: one query over a real, mixed catalog puts
        // the strongest shape on top of the page ("BTC/USDT" holds no E/H
        // relative to "ETH" and drops out entirely).
        var candidates = Instruments("BTC/USDT", "BTC/ETH", "ETH/BTC");

        // Both exact prefixes tie into their input order, the word-start
        // one follows below them.
        RankedNames(candidates, "BTC", 5)
            .Should().Equal("BTC/USDT", "BTC/ETH", "ETH/BTC");

        // The exact prefix leads, the word-start follows.
        RankedNames(candidates, "ETH", 5)
            .Should().Equal("ETH/BTC", "BTC/ETH");
    }

    [Fact]
    public void ABlankQuery_PagesTheNaturalOrderCapped()
    {
        var candidates = Instruments("BTC/USDT", "ETH/BTC", "SOL/USDC", "DOGE/USDT");

        RankedNames(candidates, string.Empty, 2)
            .Should().Equal("BTC/USDT", "ETH/BTC");
        RankedNames(candidates, "   ", 4)
            .Should().Equal("BTC/USDT", "ETH/BTC", "SOL/USDC", "DOGE/USDT");
    }

    [Fact]
    public void AQuery_RanksCaseInsensitively()
    {
        var ranked = RankedNames(Instruments("BTC/USDT", "ETH/BTC"), "btc", 5);
        ranked[0].Should().Be("BTC/USDT", "the query warms upper-case once, no culture rides along");
    }

    [Fact]
    public void MaxResults_CapsThePage_AndNothingMutates()
    {
        var candidates = Instruments("BTC/USDT", "ETH/BTC", "SOL/USDC");
        var before = candidates.Select(static item => item.Symbol.Normalized).ToArray();

        var ranked = FuzzySymbolMatcher.Match(candidates, string.Empty, 2);

        ranked.Should().HaveCount(2, "the page size caps the result");
        candidates.Select(static item => item.Symbol.Normalized)
            .Should().Equal(before, "the candidates list is never mutated");
    }

    [Fact]
    public void AZeroOrNegativePageSize_PagesNothing()
    {
        var candidates = Instruments("BTC/USDT");

        FuzzySymbolMatcher.Match(candidates, "BTC", 0).Should().BeEmpty();
        FuzzySymbolMatcher.Match(candidates, "BTC", -3).Should().BeEmpty();
    }

    [Fact]
    public void EmptyCandidates_MatchToNothing()
    {
        FuzzySymbolMatcher.Match([], "BTC", 10).Should().BeEmpty();
    }

    [Fact]
    public void NoMatchCandidates_AreExcludedNotZeroed()
    {
        var ranked = RankedNames(
            Instruments("BTC/USDT", "XRP/USDC", "ETH/BTC"),
            "BTC",
            5);

        // The miss drops from the page entirely.
        ranked.Should().Equal("BTC/USDT", "ETH/BTC");
    }

    [Fact]
    public void EqualScores_KeepTheInputOrder()
    {
        // Two identical-shape keys over one query: the registration order is
        // the ranking order — no secondary churn on ties.
        var ranked = RankedNames(Instruments("BTC/USDT", "BTC/USDC"), "BTC", 2);

        ranked.Should().Equal("BTC/USDT", "BTC/USDC");
    }

    #endregion The Match contract
}
