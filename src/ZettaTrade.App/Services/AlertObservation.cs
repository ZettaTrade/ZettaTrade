namespace ZettaTrade.App.Services;

/// <summary>
/// One factual observation the engine publishes per evaluated tick (T-096):
/// the manager's price column rides it live, and the stream-driven design
/// leaves exactly one source — an arriving ticker tick. Nothing in the
/// alerts module fabricates observations: no timer, no fetch, no poll.
/// </summary>
/// <param name="SymbolName">The normalized symbol the tick belongs to.</param>
/// <param name="OccurredUtc">The UTC moment the tick carries (the venue's own stamp).</param>
/// <param name="Price">The tick's last price.</param>
/// <param name="FundingRate">The tick's funding rate as a wire fraction (0.0001 = 0.01%) when
/// the venue serves one on the stream (a perp wire), nothing otherwise (T-240); the
/// funding-cross evaluation reads it, the other kinds never touch it.</param>
public sealed record AlertObservation(string SymbolName, DateTimeOffset OccurredUtc, decimal Price)
{
    /// <summary>The tick's funding rate as a wire fraction (0.0001 = 0.01%), or nothing when the venue serves none.</summary>
    public decimal? FundingRate { get; init; } = null;
}
