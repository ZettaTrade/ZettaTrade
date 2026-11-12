namespace ZettaTrade.App.Services;

using System.Collections.Generic;

/// <summary>
/// One watchlist row's live state as the feed serves it (T-120). The last
/// price, the 24h change and the volume are the adapter's ticker fields as
/// they come (no derivation, no defaults); the sparkline is the ring of the
/// last seen prices, up to the feed's capacity. A quote with null fields is
/// a row that exists but has not ticked yet. The funding rate rides the
/// derivatives wires only (T-197): a spot row keeps the honest null — the
/// absent fact is never a fabricated zero.
/// </summary>
public sealed record WatchlistQuote(
    string SymbolText,
    decimal? LastPrice,
    decimal? Change24hPercent,
    decimal? Volume24h,
    IReadOnlyList<decimal> Sparkline,
    decimal? FundingRate = null);
