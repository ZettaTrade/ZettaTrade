namespace ZettaTrade.App.ViewModels;

/// <summary>
/// One display row of the screener window (T-195). The parent view model
/// precomputes the display strings at page time — the row itself is
/// immutable display data, not a localization surface. The change tint
/// flags ride the T-159 pair: a strictly positive 24h change is up, a
/// strictly negative one is down, and a flat change is neither (the plain
/// ink stays).
/// </summary>
public sealed record ScreenerRowViewModel
{
    /// <summary>Normalized symbol (BASE/QUOTE) — also the click route's text.</summary>
    public required string Symbol { get; init; }

    /// <summary>Last price, adaptively formatted (the shared watchlist helper).</summary>
    public required string Price { get; init; }

    /// <summary>The 24h change, signed percent (the explicit plus for gains).</summary>
    public required string Change { get; init; }

    /// <summary>The 24h volume, compactly formatted.</summary>
    public required string Volume { get; init; }

    /// <summary>
    /// The funding rate, signed percent (T-197), or empty off the
    /// derivatives wires — a spot row leaves the cell blank, never zero.
    /// </summary>
    public required string Funding { get; init; }

    /// <summary>True for a positive 24h change: the up tint's state class.</summary>
    public required bool IsChangeUp { get; init; }

    /// <summary>
    /// True for a negative 24h change: the down tint's state class. A flat
    /// change carries neither flag — the honest plain ink.
    /// </summary>
    public required bool IsChangeDown { get; init; }

    /// <summary>The 15-minute trend cell (T-239): the localized state word and its tint flags.</summary>
    public required ScreenerTrendCellViewModel TrendM15 { get; init; }

    /// <summary>The one-hour trend cell (T-239).</summary>
    public required ScreenerTrendCellViewModel TrendH1 { get; init; }

    /// <summary>The four-hour trend cell (T-239).</summary>
    public required ScreenerTrendCellViewModel TrendH4 { get; init; }

    /// <summary>The one-day trend cell (T-239).</summary>
    public required ScreenerTrendCellViewModel TrendD1 { get; init; }
}

/// <summary>
/// One trend-state cell of a screener row (T-239): the localized state
/// word plus the bull/bear tint flags. Warming and range carry neither
/// tint — the honest plain ink (the T-159 pair's discipline): no cached
/// depth is never dressed up as a verdict.
/// </summary>
/// <param name="Text">The localized state word (formatted at page time, the row's own discipline).</param>
/// <param name="IsBull">True for a bull state: the up tint's state class.</param>
/// <param name="IsBear">True for a bear state: the down tint's state class.</param>
public sealed record ScreenerTrendCellViewModel(string Text, bool IsBull, bool IsBear)
{
    /// <summary>The plain-ink cell: a warming or range state owns no tint flags.</summary>
    public static ScreenerTrendCellViewModel Plain(string text) => new(text, false, false);
}
