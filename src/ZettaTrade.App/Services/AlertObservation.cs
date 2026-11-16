namespace ZettaTrade.App.Services;

/// <summary>
/// One factual observation the engine evaluates (T-096): the manager's price
/// column rides it live, and the stream-driven design leaves exactly two
/// sources — an arriving ticker tick, and (T-245) an arriving print of the
/// adapters' existing trade stream when a whale-print alert is armed. Nothing
/// in the alerts module fabricates observations: no timer, no fetch, no poll.
/// </summary>
/// <param name="SymbolName">The normalized symbol the observation belongs to.</param>
/// <param name="OccurredUtc">The UTC moment the fact carries (the venue's own stamp).</param>
/// <param name="Price">The tick's last price, or the print's served price.</param>
/// <param name="FundingRate">The tick's funding rate as a wire fraction (0.0001 = 0.01%) when
/// the venue serves one on the stream (a perp wire), nothing otherwise (T-240); the
/// funding-cross evaluation reads it, the other kinds never touch it.</param>
public sealed record AlertObservation(string SymbolName, DateTimeOffset OccurredUtc, decimal Price)
{
    /// <summary>The tick's funding rate as a wire fraction (0.0001 = 0.01%), or nothing when the venue serves none.</summary>
    public decimal? FundingRate { get; init; } = null;

    /// <summary>
    /// The print's served size (T-245), or nothing on the ticker route: the
    /// presence IS the route discriminator — a quantity-carrying observation
    /// rides the trade stream and evaluates ONLY the whale-print alerts, a
    /// quantity-less one rides the ticker stream and never reaches them. The
    /// whale evaluation reads it as the notional's second factor
    /// (<see cref="AlertObservation.Price"/> × Quantity).
    /// </summary>
    public decimal? Quantity { get; init; } = null;
}
