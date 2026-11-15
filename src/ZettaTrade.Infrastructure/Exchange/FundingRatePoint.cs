namespace ZettaTrade.Infrastructure.Exchange;

/// <summary>
/// One settled funding-rate event of a perpetual contract (T-240): the
/// venue-applied rate as a wire fraction (0.0001 = 0.01%) at the funding
/// timestamp, ascending by time across a history series.
/// </summary>
/// <param name="Time">The UTC timestamp the rate applied at (the venue's funding event time).</param>
/// <param name="Rate">The funding rate as a signed fraction of the mark price (0.0001 = 0.01%).</param>
public readonly record struct FundingRatePoint(DateTimeOffset Time, decimal Rate);
