namespace ZettaTrade.Strategies;

/// <summary>
/// The stance one strategy holds at the candle its evaluation ran on: the
/// single output vocabulary of the seam (T-111). <see cref="Neutral"/> is
/// an honest absence of an opinion (the warmup stretch, a tie), never a
/// default substituted for something the strategy could not compute.
/// Stances describe the human's reading only - nothing in this seam acts
/// on them: no order placement, no risk bypass, the human decides.
/// </summary>
public enum StrategyStance
{
    /// <summary>No opinion: the warmup is incomplete or the trend is undecided.</summary>
    Neutral = 0,

    /// <summary>Bullish stance at the evaluated candle.</summary>
    Long = 1,

    /// <summary>Stand-aside stance at the evaluated candle (the exit side of a long reading).</summary>
    Flat = 2,
}
