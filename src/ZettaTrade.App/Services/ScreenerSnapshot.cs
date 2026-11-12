namespace ZettaTrade.App.Services;

using System.Collections.Generic;

/// <summary>
/// One screener row's raw market numbers (T-195): the ticked candidate's
/// last price, 24h volume and 24h change exactly as the ticker served them.
/// The feed ships raw data — the panel formats the display text. The
/// funding rate rides the derivatives wires only (T-197): a spot row keeps
/// the honest null, never a fabricated zero.
/// </summary>
/// <param name="SymbolText">The candidate's normalized symbol text.</param>
/// <param name="LastPrice">The ticker's last price.</param>
/// <param name="Volume24h">The ticker's 24h volume.</param>
/// <param name="Change24hPercent">The ticker's 24h change in percent units.</param>
/// <param name="FundingRate">The ticker's funding rate as the wire serves it (a fraction), or null off the derivatives wires.</param>
public sealed record ScreenerRow(
    string SymbolText,
    decimal LastPrice,
    decimal Volume24h,
    decimal Change24hPercent,
    decimal? FundingRate = null);

/// <summary>
/// The screener page's honest state (T-195): live rows ride the table, the
/// armed-but-unpriced window waits for ticker data, and a missing or failed
/// catalog owns the reason line — never a broken table and never a
/// fabricated row.
/// </summary>
public enum ScreenerFeedState
{
    /// <summary>At least one candidate ticked: the page carries rows.</summary>
    Live,

    /// <summary>The catalog is served and the candidate window is armed, but no ticker arrived yet.</summary>
    NoTicksYet,

    /// <summary>
    /// The universe fetch failed, or the adapter serves no symbol catalog:
    /// the reason line replaces the table until a re-fetch composes.
    /// </summary>
    CatalogUnavailable,
}

/// <summary>
/// One coalesced screener page (T-195): the rows and the state move as ONE
/// panel state — never a table half-applied over a stale reason line.
/// </summary>
/// <param name="Rows">The ticked candidates in the adapter's own catalog order (the panel owns sorting).</param>
/// <param name="State">The page's honest state.</param>
public sealed record ScreenerSnapshot(
    IReadOnlyList<ScreenerRow> Rows,
    ScreenerFeedState State);
