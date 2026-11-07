namespace ZettaTrade.App.Services;

using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// The symbol palette's ranking scorer: one pure, dependency-free
/// implementation over the palette's prepared uppercase match keys
/// (<see cref="PaletteInstrument.MatchKey"/>). The comparisons never touch
/// culture (fixed at the source and per query, ordinal scans only) and the
/// ordering is deterministic — candidates that tie keep their input order.
/// </summary>
/// <remarks>
/// A candidate is scored in tiers so a better shape always outranks a
/// better small bonus inside a worse tier: the tier bases (100 000 apart)
/// dwarf the bonuses and penalties (hundreds at most). The tiers, best to
/// worst:
/// <list type="number">
/// <item>ExtraPrefix-equivalent — the exact (leading) prefix: the query
/// already names the symbol from its head.</item>
/// <item>WordStart — a contiguous hit starting at a word boundary (the
/// key's start or right after a separator).</item>
/// <item>Substring — a contiguous hit anywhere.</item>
/// <item>Subsequence — every query character appears in order.</item>
/// <item>TypoSubsequence — the same alignment with exactly one query
/// character skipped (the user's typo).</item>
/// </list>
/// Inside a tier the score prefers the query's coverage (a flat bonus per
/// covered query character), a first match character that lands on a word
/// boundary, and penalizes target characters skipped BETWEEN two matched
/// characters plus the typo escape itself. The tolerance is deliberately
/// one skipped query character at most: beyond it the instrument noise
/// dominates and the result reads as misses (negative scores are
/// excluded).
/// </remarks>
public static class FuzzySymbolMatcher
{
    #region Scoring vocabulary

    /// <summary>The score of a candidate that does not match: excluded from every ranking.</summary>
    internal const int NoMatchScore = -1;

    /// <summary>Tier base of the exact (leading) prefix shape.</summary>
    internal const int ExactPrefixTierBase = 1_000_000;

    /// <summary>Tier base of the word-start contiguous shape.</summary>
    internal const int WordStartTierBase = 900_000;

    /// <summary>Tier base of the anywhere-contiguous shape.</summary>
    internal const int SubstringTierBase = 800_000;

    /// <summary>Tier base of the in-order (subsequence) shape.</summary>
    internal const int SubsequenceTierBase = 600_000;

    /// <summary>Tier base of the one-typo-tolerant in-order shape.</summary>
    internal const int TypoSubsequenceTierBase = 400_000;

    /// <summary>Flat bonus per query character the match actually covers (internal for the scoring pins).</summary>
    internal const int CoverageBonusPerCharacter = 10;

    /// <summary>Bonus when the match's first character sits on a word boundary (internal for the scoring pins).</summary>
    internal const int WordBoundaryBonus = 150;

    /// <summary>Penalty per target character skipped between two matched characters (internal for the scoring pins).</summary>
    internal const int GapPenaltyPerCharacter = 2;

    /// <summary>Penalty for the one query character the typo path skips (internal for the scoring pins).</summary>
    internal const int TypoPenalty = 100;

    /// <summary>
    /// The characters a word boundary may follow (besides the key's own
    /// start): the pair separator and the instrument punctuation the
    /// normalized symbol shapes use.
    /// </summary>
    private const char SlashSeparator = '/';

    private const char DotSeparator = '.';

    private const char DashSeparator = '-';

    private const char UnderscoreSeparator = '_';

    #endregion Scoring vocabulary

    /// <summary>
    /// Rank the candidates against the query: the best first (higher tier,
    /// then higher bonus/penalty score inside the tier, then the input
    /// order for ties), at most <c>maxResults</c> of them.
    /// </summary>
    /// <param name="candidates">The palette's prepared instruments, in
    /// their natural (registration) order.</param>
    /// <param name="query">The user's query, any case.</param>
    /// <param name="maxResults">The requested page size (a non-positive
    /// size pages nothing).</param>
    /// <returns>The ranked page as a fresh list; the candidates list is
    /// never mutated. A blank query pages the natural order unchanged —
    /// the caller's continuity guard, not a ranking opinion.</returns>
    public static IReadOnlyList<PaletteInstrument> Match(
        IReadOnlyList<PaletteInstrument> candidates,
        string query,
        int maxResults)
    {
        if (maxResults <= 0 || candidates.Count == 0)
        {
            return [];
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            return candidates.Take(maxResults).ToList();
        }

        // Case is fixed once per query; the scans stay ordinal.
        var warmQuery = query.ToUpperInvariant();

        // Matches remember their input position beside the score, so ties
        // stay stable without a second sort pass (the ordering is stable).
        List<(int Score, PaletteInstrument Instrument)> matches = new();
        for (var order = 0; order < candidates.Count; order++)
        {
            var score = ScoreCandidate(candidates[order].MatchKey, warmQuery);
            if (score != NoMatchScore)
            {
                matches.Add((score, candidates[order]));
            }
        }

        return matches
            .OrderByDescending(static ranked => ranked.Score)
            .Take(maxResults)
            .Select(static ranked => ranked.Instrument)
            .ToList();
    }

    /// <summary>
    /// Score ONE candidate's match key against a warmed (uppercased)
    /// query: the cheapest shape that expresses the match wins, in tier
    /// order — prefix, word-start, anywhere-contiguous, subsequence, and
    /// only then the one-typo retry.
    /// </summary>
    /// <param name="matchKey">The candidate's uppercase match key.</param>
    /// <param name="warmQuery">The uppercased query.</param>
    /// <returns>The candidate's score, or <see cref="NoMatchScore"/>.</returns>
    internal static int ScoreCandidate(string matchKey, string warmQuery)
    {
        if (matchKey.Length == 0 || warmQuery.Length == 0)
        {
            return NoMatchScore;
        }

        // Tier 1: the strongest shape — the query names the symbol from
        // its head. The match's first character IS the key's start, so the
        // boundary bonus rides along (0 is a boundary).
        if (matchKey.StartsWith(warmQuery, StringComparison.Ordinal))
        {
            return ExactPrefixTierBase
                + ScoreBonus(warmQuery.Length, wordStart: true);
        }

        // Tier 2: the earliest contiguous hit that starts at a word
        // boundary — "USDT" on "BTC/USDT" is a word-start hit even though
        // the symbol does not begin with it. The scan stops at the first
        // qualifying boundary: a later hit would carry the identical
        // tier, coverage and bonus score.
        for (var boundary = FindNextWordBoundary(matchKey, start: 1);
             boundary >= 0;
             boundary = FindNextWordBoundary(matchKey, boundary + 1))
        {
            if (matchKey.AsSpan(boundary).StartsWith(warmQuery.AsSpan(), StringComparison.Ordinal))
            {
                return WordStartTierBase
                    + ScoreBonus(warmQuery.Length, wordStart: true);
            }
        }

        // Tier 3: a contiguous hit anywhere (the first IndexOf hit's score
        // equals a later hit's, so one call does the whole decision).
        if (matchKey.Contains(warmQuery, StringComparison.Ordinal))
        {
            return SubstringTierBase + ScoreBonus(warmQuery.Length, wordStart: false);
        }

        // Tier 4: every query character in order over the key.
        var subsequence = ScoreSubsequence(matchKey, warmQuery, typoSkip: null);
        if (subsequence != NoMatchScore)
        {
            return subsequence;
        }

        // Tier 5: one query character skipped (the user's typo). A
        // single-character query has nothing to skip over.
        if (warmQuery.Length < 2)
        {
            return NoMatchScore;
        }

        return ScoreBestTypoSubsequence(matchKey, warmQuery);
    }

    #region Subsequence alignment

    /// <summary>
    /// The greedy leftmost subsequence alignment: every query character
    /// matches the earliest target position at or after the previous
    /// match. A per-character reflow cannot turn a miss into a hit, and
    /// the leftmost runs minimize the between-match gaps, so one forward
    /// scan scores the whole key.
    /// </summary>
    /// <param name="matchKey">The candidate's uppercase key.</param>
    /// <param name="warmQuery">The uppercase query.</param>
    /// <param name="typoSkip">The query index skipped as the user's typo,
    /// or null for the zero-typo alignment.</param>
    /// <returns>The scored alignment, or <see cref="NoMatchScore"/>.</returns>
    private static int ScoreSubsequence(string matchKey, string warmQuery, int? typoSkip)
    {
        var previousMatch = -1;
        var firstMatch = -1;
        var gap = 0;
        var covered = 0;
        for (var queryIndex = 0; queryIndex < warmQuery.Length; queryIndex++)
        {
            if (queryIndex == typoSkip)
            {
                continue;
            }

            var match = matchKey.IndexOf(warmQuery[queryIndex], previousMatch + 1);
            if (match < 0)
            {
                return NoMatchScore;
            }

            if (previousMatch >= 0)
            {
                // Only skipped characters BETWEEN two matched characters
                // count: the span before the first match (and after the
                // last) is outside the match.
                gap += match - previousMatch - 1;
            }
            else
            {
                firstMatch = match;
            }

            previousMatch = match;
            covered++;
        }

        var tierBase = typoSkip is null ? SubsequenceTierBase : TypoSubsequenceTierBase;
        var boundary = firstMatch == 0 || IsWordBoundary(matchKey, firstMatch);
        return tierBase
            + ScoreBonus(covered, wordStart: boundary)
            - GapPenaltyPerCharacter * gap
            - (typoSkip is null ? 0 : TypoPenalty);
    }

    /// <summary>
    /// The one-typo retry driver: for every query index, score the
    /// alignment that skips THAT query character, and keep the best. With
    /// m query characters and n key characters this is O(m·n) over the
    /// handful of characters a symbol's name has.
    /// </summary>
    /// <param name="matchKey">The candidate's uppercase key.</param>
    /// <param name="warmQuery">The uppercase query (at least two
    /// characters — the caller guards the shorter ones).</param>
    /// <returns>The best typo-tolerant score, or <see cref="NoMatchScore"/>.</returns>
    private static int ScoreBestTypoSubsequence(string matchKey, string warmQuery)
    {
        var best = NoMatchScore;
        for (var skipped = 0; skipped < warmQuery.Length; skipped++)
        {
            var score = ScoreSubsequence(matchKey, warmQuery, typoSkip: skipped);
            if (score > best)
            {
                best = score;
            }
        }

        return best;
    }

    /// <summary>The tier's shared tail: the coverage bonus plus the word-boundary bonus.</summary>
    /// <param name="coveredCharacters">How many query characters the match covered.</param>
    /// <param name="wordStart">Whether the match's first character sits on a word boundary.</param>
    /// <returns>The bonus sum the tier base gets on top.</returns>
    private static int ScoreBonus(int coveredCharacters, bool wordStart) =>
        (CoverageBonusPerCharacter * coveredCharacters) + (wordStart ? WordBoundaryBonus : 0);

    #endregion Subsequence alignment

    /// <summary>
    /// Find the next word boundary: the smallest index at or after
    /// <c>start</c> that starts a word (the key's start or a position
    /// right after a separator).
    /// </summary>
    /// <param name="matchKey">The candidate's uppercase key.</param>
    /// <param name="start">The first index the scan may return.</param>
    /// <returns>The boundary index, or −1 when none remains.</returns>
    private static int FindNextWordBoundary(string matchKey, int start)
    {
        for (var index = Math.Max(start, 0); index < matchKey.Length; index++)
        {
            if (IsWordBoundary(matchKey, index))
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>
    /// Whether the index at <c>position</c> starts a word: the key's start
    /// always does, and a position does once the character before it is a
    /// separator.
    /// </summary>
    private static bool IsWordBoundary(string matchKey, int position)
    {
        if (position == 0)
        {
            return true;
        }

        var previous = matchKey[position - 1];
        return previous is SlashSeparator or DotSeparator or DashSeparator or UnderscoreSeparator;
    }
}
