namespace ZettaTrade.Infrastructure.Exchange;

/// <summary>
/// One settled open-interest snapshot of a perpetual contract (T-241): the
/// venue-reported open interest at the snapshot's timestamp, ascending by
/// time across a history series.
/// </summary>
/// <param name="Time">The UTC timestamp the snapshot applied at (the venue's bucket time).</param>
/// <param name="Size">The open interest as a signed decimal size in the venue's contracts (never negative on the venue's wire; the sign carries through unparsed).</param>
public readonly record struct OpenInterestPoint(DateTimeOffset Time, decimal Size);
