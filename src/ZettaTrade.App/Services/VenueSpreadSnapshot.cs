namespace ZettaTrade.App.Services;

using System.Collections.Generic;

/// <summary>
/// One venue-spread pass's published table (T-253): the symbol the pass
/// sampled (the normalized text), one row per registered venue — the
/// live quote (null = the venue did not answer in time, the honest
/// dash) and the per-venue perp-spot basis series (empty = the venue's
/// cache cannot judge the pair). Immutable: the window's panel state
/// moves by whole snapshots, never by patching rows.
/// </summary>
/// <param name="SymbolText">The sampled symbol's normalized text.</param>
/// <param name="Quotes">The venue rows, in the fleet's registration order.</param>
/// <param name="Bases">The per-venue basis series, in the same order.</param>
public sealed record VenueSpreadSnapshot(
    string SymbolText,
    IReadOnlyList<VenueQuote> Quotes,
    IReadOnlyList<VenueBasis> Bases);

/// <summary>
/// One venue's live quote row (T-253). A null member is the honest
/// absence (the venue never answered, or the wire served a
/// non-positive price — not a price, the dash); the delta rides the
/// answering set's median (null when fewer than two venues answered —
/// a spread needs a cross-venue reference, one venue has none).
/// </summary>
/// <param name="Venue">The venue's display name (the adapter's own).</param>
/// <param name="Last">The venue's last price.</param>
/// <param name="Bid">The venue's best bid.</param>
/// <param name="Ask">The venue's best ask.</param>
/// <param name="LastDeltaBps">The last price's delta against the answering set's median, in basis points.</param>
public sealed record VenueQuote(
    string Venue,
    decimal? Last,
    decimal? Bid,
    decimal? Ask,
    double? LastDeltaBps);

/// <summary>
/// One venue's perp-spot basis series (T-253): the (time, bps) samples
/// over the venue's own cache, ascending by time — empty when the
/// venue's cache cannot judge the pair (either category missing or
/// thin: the honest no-series, never a fabricated stretch).
/// </summary>
/// <param name="Venue">The venue's display name (the adapter's own).</param>
/// <param name="Samples">The basis samples, ascending by time.</param>
public sealed record VenueBasis(
    string Venue,
    IReadOnlyList<(System.DateTimeOffset Time, double Bps)> Samples);
