namespace ZettaTrade.Charting;

using ZettaTrade.Core.Entities;

/// <summary>
/// The session anchor of the VWAP overlay (T-093): a session breaks wherever
/// the loaded bars' opens cross a calendar-day boundary. Crypto venues trade
/// around the clock, so the UTC day is the only venue-honest anchor their
/// series carries without per-venue session metadata; the stock-style venues
/// keep weekday sessions (the T-037 calendar knowledge: their day-scale
/// candles skip weekends), and those sessions break on the same rule
/// because a venue's quiet days carry no bars at all. Every calendar-day
/// -and-coarser bar therefore anchors its own session (a D1 bar's volume is
/// one session's volume), and a series that starts mid-session anchors the
/// accumulation at its own first bar — nothing outside the loaded window
/// is ever assumed.
/// </summary>
public static class VwapSessionAnchor
{
    /// <summary>
    /// Whether <paramref name="current"/> opens a NEW VWAP session relative
    /// to <paramref name="previous"/>: true when <paramref name="previous"/>
    /// is null (the series' first bar always anchors its session) or the two
    /// opens fall on different UTC calendar dates.
    /// </summary>
    /// <param name="previous">The bar before <paramref name="current"/> in
    /// the series, or null at the series head.</param>
    /// <param name="current">The bar the anchor decides for.</param>
    /// <returns>true when the bar must reset the VWAP accumulation.</returns>
    public static bool OpensNewSession(Bar? previous, Bar current) =>
        previous is null ||
        previous.Value.Time.UtcDateTime.Date != current.Time.UtcDateTime.Date;
}
