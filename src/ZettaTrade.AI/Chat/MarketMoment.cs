namespace ZettaTrade.AI.Chat;

using ZettaTrade.Core.Entities;

/// <summary>
/// The workspace moment as the market-context preamble should see it: the
/// venue actually trading (with its paper/live classification handed over as
/// a fact, never re-derived here), the latest ticker shape, the top order
/// book levels and the enabled indicator setup. Every part is optional: a
/// fact that never arrived stays absent and the preamble degrades honestly
/// instead of inventing values. Pure data — the compaction is the preamble
/// composer's job (<see cref="MarketContextPreambleComposer"/>).
/// </summary>
public sealed record MarketMoment
{
    /// <summary>Whether the venue trading the workspace is the paper-trading
    /// simulator. The source decides (it owns the canonical comparison);
    /// unknown stays absent.</summary>
    public bool? IsPaperVenue { get; init; }

    /// <summary>Identifier of the venue actually trading (e.g. "paper" or an
    /// exchange id). Empty when unknown.</summary>
    public string VenueId { get; init; } = string.Empty;

    /// <summary>The latest ticker price (absent until a tick arrived).</summary>
    public decimal? LastPrice { get; init; }

    /// <summary>The ticker's 24h price change in percent (absent without a
    /// tick carrying it).</summary>
    public decimal? PriceChange24hPercent { get; init; }

    /// <summary>The top order book levels, best first (absent without a book
    /// snapshot; the side without levels renders nothing).</summary>
    public IReadOnlyList<PriceLevel>? Bids { get; init; }

    /// <summary>The top order book ask levels, best first (absent without a
    /// book snapshot).</summary>
    public IReadOnlyList<PriceLevel>? Asks { get; init; }

    /// <summary>The enabled indicator setup rows of the workspace chart
    /// (name and period — the configured facts; the computed per-bar values
    /// are the chart lane's data and stay out of the moment). Absent when the
    /// settings never arrived.</summary>
    public IReadOnlyList<MarketMomentIndicator>? Indicators { get; init; }
}

/// <summary>
/// One enabled indicator setup row inside a <see cref="MarketMoment"/>:
/// the indicator name and the period the workspace configured — the setup
/// itself, never a quoted live value (the chart computes values per bar;
/// the preamble names the setup it stands on).
/// </summary>
/// <param name="Name">The indicator's canonical name (e.g. "SMA").</param>
/// <param name="Period">The configured period of the row.</param>
public sealed record MarketMomentIndicator(string Name, int Period);
