namespace ZettaTrade.App.Services;

using ZettaTrade.AI.Chat;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;

/// <summary>
/// The workspace as the assistant should see it: which exchange serves the
/// view, what symbol and time frame the trader is looking at, and the latest
/// price the ticker reported — plus the market-moment facts the chat can
/// inject when the user consented (the 24h change, the top book levels, the
/// enabled indicator setup and the paper/live venue classification). A part
/// that is not (yet) known is simply absent — the prompt degrades honestly
/// instead of inventing values.
/// </summary>
public sealed record ChatContext
{
    /// <summary>Display name of the active exchange.</summary>
    public string ExchangeName { get; init; } = string.Empty;

    /// <summary>The workspace symbol, once one is selected.</summary>
    public string? Symbol { get; init; }

    /// <summary>The workspace time frame (only meaningful with a symbol).</summary>
    public TimeFrame? TimeFrame { get; init; }

    /// <summary>The latest ticker price (only meaningful with a symbol).</summary>
    public decimal? LastPrice { get; init; }

    /// <summary>The ticker's 24h price change in percent, as the newest tick
    /// carried it (only meaningful when a ticker arrived).</summary>
    public decimal? PriceChange24hPercent { get; init; }

    /// <summary>The top order book bid levels, best first (only meaningful
    /// with a symbol whose book streamed at least once; absent until then,
    /// an explicit empty side renders as its market's withdrawal).</summary>
    public IReadOnlyList<PriceLevel>? BestBids { get; init; }

    /// <summary>The top order book ask levels, best first (as the bids).</summary>
    public IReadOnlyList<PriceLevel>? BestAsks { get; init; }

    /// <summary>The workspace chart's enabled indicator setup rows (name and
    /// period — the configured facts; the computed values stay the chart
    /// lane's data). Absent until the settings carried at least one.</summary>
    public IReadOnlyList<ChatIndicatorRow>? Indicators { get; init; }

    /// <summary>Whether the venue actually trading the workspace is the
    /// paper-trading simulator (the canonical comparison lives with the
    /// source that owns the venue). Absent when no venue stands.</summary>
    public bool? IsPaperVenue { get; init; }

    /// <summary>Whether at least a symbol is pinned (the useful baseline).</summary>
    public bool HasSymbol => Symbol is not null;

    /// <summary>
    /// Project the snapshot onto the AI module's market moment (the shape
    /// the preamble composer consumes): a plain field-for-field pass, so
    /// the projection never re-derives or defaults a fact.
    /// </summary>
    /// <returns>The market moment carrying the same facts (missing parts
    /// stay missing — null for null on every line).</returns>
    public MarketMoment ToMarketMoment() => new()
    {
        IsPaperVenue = IsPaperVenue,
        VenueId = VenueId,
        LastPrice = LastPrice,
        PriceChange24hPercent = PriceChange24hPercent,
        Bids = BestBids,
        Asks = BestAsks,
        Indicators = Indicators is null
            ? null
            : [.. Indicators.Select(row => new MarketMomentIndicator(row.Name, row.Period))],
    };

    /// <summary>Identifier of the venue actually trading (e.g. "paper").
    /// Empty when unknown.</summary>
    public string VenueId { get; init; } = string.Empty;
}

/// <summary>
/// One enabled indicator row of the workspace chart, as the workspace
/// moment carries it: the canonical indicator name and the configured
/// period (the setup itself — the computed per-bar values stay the chart
/// lane's data).
/// </summary>
/// <param name="Name">The indicator's canonical name (e.g. "SMA").</param>
/// <param name="Period">The configured period of the row.</param>
public sealed record ChatIndicatorRow(string Name, int Period);
