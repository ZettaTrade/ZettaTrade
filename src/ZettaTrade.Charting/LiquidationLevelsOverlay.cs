namespace ZettaTrade.Charting;

using ZettaTrade.Core.Enums;

/// <summary>
/// The honesty states the estimated liquidation-levels overlay reports
/// (T-243), so the chart presents the honest state instead of fabricating
/// levels.
/// </summary>
public enum LiquidationLevelsOverlayState
{
    /// <summary>At least one bucket produced an estimated level: the bands
    /// stand and every one of them is an ESTIMATE (the marking rides each
    /// band — the overlay never presents a level as venue data).</summary>
    Estimated,

    /// <summary>The open-interest series stood but no bucket produced a
    /// level: flat open interest, no directional buckets, or every band
    /// degenerate. Nothing draws except the state's own note.</summary>
    NoDeltas,

    /// <summary>The venue serves no leverage-bracket table (or none for the
    /// symbol): the maintenance-margin/leverage half of the estimate is
    /// missing, so no level is computable — the explicit absence stands as
    /// its own note, never a fabricated table.</summary>
    NoBrackets,
}

/// <summary>
/// One estimated liquidation level (T-243): the price a bracket-scaled
/// position entered in the bucket would force-close at, and the bucket's
/// delta notional — the band's weight. The SIDE is which position side
/// would liquidate there (longs force-close below their entry, shorts
/// above), so the renderer hues the band by the side the tape already
/// carries. Every band is an estimate by construction; the price is exact
/// arithmetic over the venue's public bracket table and the local OI
/// deltas, never a venue-served map.
/// </summary>
/// <param name="Price">The estimated liquidation price (the band's center).</param>
/// <param name="Notional">The bucket's delta notional in the quote currency (the band's weight).</param>
/// <param name="Side">The position side that would liquidate at the price.</param>
public sealed record LiquidationLevelBand(
    decimal Price,
    decimal Notional,
    LiquidationSide Side);

/// <summary>
/// The estimated liquidation-levels overlay (T-243): the level bands the
/// local estimator computed from the chart's own OI deltas and the venue's
/// public leverage brackets, published against the loaded series the same
/// way the VWAP overlay is. Static price geometry — the bands are
/// horizontal levels, not a per-bar series, so the chart draws them across
/// the whole visible window without a slice.
/// </summary>
/// <param name="State">The honesty state the estimate stands in.</param>
/// <param name="Bands">The estimated bands, ascending by price (largest notional first at a tie); bounded by the estimator's band cap.</param>
public sealed record LiquidationLevelsOverlay(
    LiquidationLevelsOverlayState State,
    IReadOnlyList<LiquidationLevelBand> Bands);
