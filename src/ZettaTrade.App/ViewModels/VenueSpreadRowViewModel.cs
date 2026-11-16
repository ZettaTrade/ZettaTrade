namespace ZettaTrade.App.ViewModels;

using Avalonia;

/// <summary>
/// One display row of the venue-spread window (T-253). The parent view
/// model precomputes the display strings at apply time — the row is
/// immutable display data, not a localization surface (the screener
/// row's own discipline). A null-source fact renders the honest dash;
/// the delta and the basis carry their own tint flags (the candle
/// state colors, the same inks the chart speaks).
/// </summary>
public sealed record VenueSpreadRowViewModel
{
    /// <summary>The venue's display name (the adapter's own) — also the row header's caption.</summary>
    public required string Venue { get; init; }

    /// <summary>The last price's display text (the watchlist's price formatter) or the dash.</summary>
    public required string LastText { get; init; }

    /// <summary>The best bid's display text or the dash.</summary>
    public required string BidText { get; init; }

    /// <summary>The best ask's display text or the dash.</summary>
    public required string AskText { get; init; }

    /// <summary>The last price's cross-venue delta in basis points (signed, one decimal) or the dash.</summary>
    public required string DeltaText { get; init; }

    /// <summary>True for a strictly positive delta: the up tint's state class.</summary>
    public required bool IsDeltaPositive { get; init; }

    /// <summary>True for a strictly negative delta: the down tint's state class.</summary>
    public required bool IsDeltaNegative { get; init; }

    /// <summary>The basis series' last sample in basis points (signed) or the dash.</summary>
    public required string BasisText { get; init; }

    /// <summary>True for a strictly positive basis last: the up tint's state class.</summary>
    public required bool IsBasisPositive { get; init; }

    /// <summary>True for a strictly negative basis last: the down tint's state class.</summary>
    public required bool IsBasisNegative { get; init; }

    /// <summary>The basis series' sparkline points (the watchlist mapper's canvas shape); empty when the venue carries no series.</summary>
    public required IList<Point> BasisPoints { get; init; }

    /// <summary>The honest dash: an absent fact renders quiet, never a fabricated level.</summary>
    public const string DashText = "\u2014";
}
