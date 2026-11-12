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

    /// <summary>True for a positive 24h change: the up tint's state class.</summary>
    public required bool IsChangeUp { get; init; }

    /// <summary>
    /// True for a negative 24h change: the down tint's state class. A flat
    /// change carries neither flag — the honest plain ink.
    /// </summary>
    public required bool IsChangeDown { get; init; }
}
