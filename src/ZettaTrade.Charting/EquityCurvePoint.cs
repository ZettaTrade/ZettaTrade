namespace ZettaTrade.Charting;

/// <summary>
/// One point of a trade-activity equity curve: the moment the point was
/// reached (a closing fill's timestamp) and the cumulative realized PnL
/// at that moment, in the account's quote units. The curve derives from
/// the same fill-pair math that feeds the summary statistics — one source
/// of truth for both the numbers and the chart.
/// </summary>
public sealed record EquityCurvePoint(DateTimeOffset Time, decimal Value);
