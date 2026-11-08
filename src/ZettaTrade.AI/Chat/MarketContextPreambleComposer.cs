namespace ZettaTrade.AI.Chat;

using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Level = ZettaTrade.Core.Entities.PriceLevel;

/// <summary>
/// Compacts a <see cref="MarketMoment"/> into the token-bounded preamble the
/// chat injects per message when the user consented. Pure and deterministic:
/// the same moment renders the same string byte for byte, the parts stand in
/// a fixed priority order, and a moment that would not fit is shaved by
/// dropping whole low-priority parts (indicators first, then the book sides,
/// then the 24h change) — never by re-wording, never by inventing or
/// rounding a value it lacks. The paper/live tag is the high-priority part:
/// under any budget the tag survives, because sand-boxed vs public venue is
/// the fact every answer should read first. English output is by design:
/// like the system prompt, it is model-facing instruction, not a
/// user-facing string.
/// </summary>
public sealed class MarketContextPreambleComposer
{
    #region Constants

    /// <summary>The preamble's head (the marker the chat texts append
    /// behind).</summary>
    private const string HeadText = "Market snapshot:";

    /// <summary>The tag line for the paper venue: the simulation is THE fact
    /// every reading should carry.</summary>
    private const string PaperVenueText = "trading on paper (the paper-trading simulator, simulated fills)";

    /// <summary>The tag line for a live venue (the venue id rides inside).</summary>
    private const string LiveVenueText = "trading live on ";

    /// <summary>The honest tag line when a live venue carries no id.</summary>
    private const string UnidentifiedVenueText = LiveVenueText + "an unidentified venue";

    /// <summary>The one-line mark for a moment without any market fact: the
    /// honest absence, not an invented generic.</summary>
    private const string EmptyText = "Market snapshot: no live market data received yet.";

    /// <summary>The price/change segments (InvariantCulture numbers, the
    /// canonical decimal spelling the system prompt prints). A price the
    /// snapshot lacks renders nothing — the part degrades per fact, never a
    /// placeholder paragraph.</summary>
    private const string PriceSegment = " last price ";

    private const string ChangeSegment = ", 24h change ";

    private const string ChangeSuffix = "%";

    /// <summary>The book segments: the top levels only, best first. The
    /// sides stand apart with a level mark of their own, so a level's
    /// price/quantity pair never reads as the next side.</summary>
    private const string BookHead = " book ";

    private const string BidHead = "bids ";

    private const string AskHead = " / asks ";

    /// <summary>The levels' price/quantity split mark.</summary>
    private const string LevelSeparator = ":";

    /// <summary>The mark between two levels of one side.</summary>
    private const string LevelJoin = ", ";

    /// <summary>The indicators segment: the configured setup, never quoted
    /// per-bar values.</summary>
    private const string IndicatorsHead = ", indicators ";

    private const string IndicatorSeparator = ", ";

    /// <summary>The shape of an over-long indicator setup's tail.</summary>
    private const string IndicatorsTailPrefix = "+";

    private const string IndicatorsTailSuffix = " more";

    /// <summary>Arguments the numeric spelling rides on — the canonical
    /// decimal formatting every market surface of the app prints.</summary>
    private const string PriceFormat = "F8";

    private const string LevelPriceFormat = "F8";

    private const string LevelQuantityFormat = "G6";

    private const string ChangeFormat = "F2";

    /// <summary>Token budget the preamble must fit (the acceptance says a
    /// tight one).</summary>
    public const int MaxTokens = 220;

    /// <summary>Characters per token the budget converts with (the rough
    /// English-prose quotient this renderer's deterministic trim uses — a
    /// lenient upper bound is fine, the preamble only ever shrinks).</summary>
    public const int CharactersPerToken = 4;

    /// <summary>The preamble's character budget, converted once.</summary>
    public const int MaxCharacters = MaxTokens * CharactersPerToken;

    /// <summary>How many order book levels per side the preamble carries at
    /// the top of its book part (tighter budgets drop whole parts instead;
    /// the cap only bounds the normal shape).</summary>
    public const int BookTopLevelCount = 3;

    /// <summary>How many indicator rows the preamble carries (a longer setup
    /// shows a compact tail, not a re-worded dump).</summary>
    public const int IndicatorRowCount = 6;

    #endregion

    #region Public Methods

    /// <summary>
    /// Compose the market-context preamble for one instant: the paper/live
    /// tag first, then the market facts the moment actually carries, inside
    /// the token budget. Deterministic for the same moment; a moment without
    /// any fact renders the honest absence line.
    /// </summary>
    /// <param name="moment">The workspace moment at send time (null parts are
    /// honest absences, never substituted with a value).</param>
    /// <returns>The preamble text that rides behind the system prompt's
    /// workspace head.</returns>
    public string Compose(MarketMoment? moment)
    {
        if (moment is null)
        {
            return EmptyText;
        }

        if (!CarriesAnyFacts(moment))
        {
            return EmptyText;
        }

        var preamble = Render(moment);
        return preamble.Length <= MaxCharacters ? preamble : Shave(moment, preamble);
    }

    /// <summary>Whether any fact of the moment arrived: an all-absent moment
    /// renders the honest absence line, not a bare head.</summary>
    private static bool CarriesAnyFacts(MarketMoment moment) =>
        moment.IsPaperVenue is not null ||
        moment.VenueId.Length > 0 ||
        moment.LastPrice is not null ||
        moment.PriceChange24hPercent is not null ||
        moment.Bids is not null ||
        moment.Asks is not null ||
        moment.Indicators is not null;

    #endregion

    #region Rendering

    /// <summary>The full render walks the part composers in the fixed
    /// priority order: tag, price/change, book, indicators.</summary>
    private string Render(MarketMoment moment)
    {
        var builder = new StringBuilder();
        builder.Append(HeadText);
        AppendVenueTag(builder, moment);
        AppendPrice(builder, moment);
        AppendBook(builder, moment);
        AppendIndicators(builder, moment);
        return builder.ToString();
    }

    /// <summary>
    /// The paper/live tag: the classification arrives as a decided fact (the
    /// source owns the canonical comparison), so the composer never guesses —
    /// an absent tag renders nothing, a paper venue says so outright, a live
    /// venue names itself (or stays honestly unidentified when it has no id).
    /// </summary>
    private static void AppendVenueTag(StringBuilder builder, MarketMoment moment)
    {
        if (moment.IsPaperVenue is null && moment.VenueId.Length == 0)
        {
            return;
        }

        if (moment.IsPaperVenue is true)
        {
            builder.Append(' ').Append(PaperVenueText);
            return;
        }

        builder.Append(' ');
        builder.Append(moment.VenueId.Length > 0 ? LiveVenueText + moment.VenueId : UnidentifiedVenueText);
    }

    /// <summary>The price and the 24h change: only the facts a tick carried
    /// (a change without a price renders its side alone, and vice versa —
    /// no placeholder phrasing).</summary>
    private static void AppendPrice(StringBuilder builder, MarketMoment moment)
    {
        if (moment.LastPrice is null && moment.PriceChange24hPercent is null)
        {
            return;
        }

        if (moment.LastPrice is { } price)
        {
            builder.Append(PriceSegment);
            builder.Append(price.ToString(PriceFormat, CultureInfo.InvariantCulture));
        }

        if (moment.PriceChange24hPercent is { } change)
        {
            builder.Append(ChangeSegment);
            builder.Append(change.ToString(ChangeFormat, CultureInfo.InvariantCulture));
            builder.Append(ChangeSuffix);
        }
    }

    /// <summary>
    /// The top book levels, best first, both sides. A side the snapshot
    /// carried absent renders nothing; a side it explicitly carried empty
    /// also renders nothing — an empty side is the market's withdrawal, a
    /// fact the absence renders by itself (the composer never fills it).
    /// </summary>
    private static void AppendBook(StringBuilder builder, MarketMoment moment)
    {
        var bids = moment.Bids;
        var asks = moment.Asks;
        if (bids is null && asks is null)
        {
            return;
        }

        if (bids is not { Count: > 0 } && asks is not { Count: > 0 })
        {
            builder.Append(BookHead).Append("no levels");
            return;
        }

        builder.Append(BookHead);
        if (bids is { Count: > 0 })
        {
            builder.Append(BidHead);
            AppendLevels(builder, bids);
        }

        if (asks is { Count: > 0 })
        {
            builder.Append(AskHead);
            AppendLevels(builder, asks);
        }
    }

    /// <summary>The indicator setup rows, capped at the compact row count:
    /// a longer setup shows its tail size, never a re-worded dump.</summary>
    private static void AppendIndicators(StringBuilder builder, MarketMoment moment)
    {
        if (moment.Indicators is not { Count: > 0 } indicators)
        {
            return;
        }

        builder.Append(IndicatorsHead);
        var shown = Math.Min(indicators.Count, IndicatorRowCount);
        for (var index = 0; index < shown; index++)
        {
            if (index > 0)
            {
                builder.Append(IndicatorSeparator);
            }

            builder.Append(CultureInfo.InvariantCulture,
                $"{indicators[index].Name}({indicators[index].Period})");
        }

        if (indicators.Count > shown)
        {
            builder.Append(IndicatorSeparator);
            builder.Append(CultureInfo.InvariantCulture,
                $"{IndicatorsTailPrefix}{indicators.Count - shown}{IndicatorsTailSuffix}");
        }
    }

    /// <summary>One side's levels rendered as price:quantity pairs, best
    /// first, joined with a list mark and capped at the top level count;
    /// InvariantCulture keeps the rendering stable across cultures and
    /// sessions.</summary>
    private static void AppendLevels(StringBuilder builder, IReadOnlyList<Level> levels)
    {
        var count = Math.Min(levels.Count, BookTopLevelCount);
        for (var index = 0; index < count; index++)
        {
            if (index > 0)
            {
                builder.Append(LevelJoin);
            }

            builder.Append(CultureInfo.InvariantCulture,
                $"{levels[index].Price.ToString(LevelPriceFormat, CultureInfo.InvariantCulture)}{LevelSeparator}{levels[index].Quantity.ToString(LevelQuantityFormat, CultureInfo.InvariantCulture)}");
        }
    }

    #endregion

    #region Budget Shave

    /// <summary>
    /// The deterministic trim for a draft over budget: drop the low-priority
    /// parts one at a time (indicators, then book bids, then book asks, then
    /// the 24h change) and re-render through the same part composers, so the
    /// shaved spelling never diverges from the full one. The paper/live tag
    /// is not on the drop list — a shaved preamble still carries the venue
    /// fact. The final cap (no realistic input reaches it) cuts the string
    /// at the budget, so the acceptance bound never breaks.
    /// </summary>
    private string Shave(MarketMoment moment, string full)
    {
        foreach (var part in ShavePlan(moment))
        {
            moment = part switch
            {
                MomentPart.Indicators => moment with { Indicators = null },
                MomentPart.BidLevels => moment with { Bids = null },
                MomentPart.AskLevels => moment with { Asks = null },
                MomentPart.HourChange => moment with { PriceChange24hPercent = null },
                _ => moment,
            };

            var shaved = Render(moment);
            if (shaved.Length <= MaxCharacters)
            {
                return shaved;
            }
        }

        return full[..MaxCharacters];
    }

    /// <summary>The shave plan: the parts the moment carries, lowest
    /// priority first (the tag is never on the list).</summary>
    private static List<MomentPart> ShavePlan(MarketMoment moment)
    {
        List<MomentPart> plan = [];
        if (moment.Indicators is { Count: > 0 })
        {
            plan.Add(MomentPart.Indicators);
        }

        if (moment.Bids is { Count: > 0 })
        {
            plan.Add(MomentPart.BidLevels);
        }

        if (moment.Asks is { Count: > 0 })
        {
            plan.Add(MomentPart.AskLevels);
        }

        if (moment.PriceChange24hPercent != null)
        {
            plan.Add(MomentPart.HourChange);
        }

        return plan;
    }

    /// <summary>The parts the shave may drop, in its priority order.</summary>
    private enum MomentPart
    {
        Indicators,
        BidLevels,
        AskLevels,
        HourChange,
    }

    #endregion
}
