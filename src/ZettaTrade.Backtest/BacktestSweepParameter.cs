namespace ZettaTrade.Backtest;

/// <summary>
/// The numeric run knobs a sweep grid varies (T-212): the two outcome-shaping
/// execution-policy parameters the backtest request declares. Bars, seed and
/// the initial balance stay the run's identity and scale — a different bar
/// target is a different replay window, a different seed only reshuffles the
/// slippage draws, and the starting balance scales every curve linearly — so
/// they are not swept here. The strategy seam's contract carries no
/// per-strategy parameter declaration (the parameterless-constructor
/// convention is its only documented construction shape), so the sweep
/// varies the request's own declared knobs.
/// </summary>
public enum BacktestSweepParameter
{
    /// <summary>The long-side sizing fraction: the equity share each long entry converts into notional.</summary>
    LongNotionalFraction = 0,

    /// <summary>The seeded adverse slippage magnitude: the execution cost every fill pays up to.</summary>
    SlippageValue = 1,
}
