namespace ZettaTrade.Strategies.Tests.Harness;

using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// The seeded candles of the seam fact suites (T-111): closes only - OHLC
/// pinned to the close, one unit of volume per bar, M1-spaced open times
/// from the epoch. Deterministic fuel, nothing clever: the golden suits
/// number their walks by hand.
/// </summary>
public static class TestBars
{
    /// <summary>The trading symbol every fact addresses (one identity, pinned everywhere).</summary>
    public static Symbol Symbol { get; } = Symbol.Parse("BTC/USDT");

    /// <summary>
    /// Build an ascending M1 closed-candle series from the closes: close
    /// <c>i</c> becomes the open, high, low and close of bar <c>i</c>, the
    /// open times step one minute from the epoch.
    /// </summary>
    /// <param name="closes">The closes of the series, oldest first.</param>
    public static Bar[] Candles(params decimal[] closes)
    {
        var bars = new Bar[closes.Length];
        for (var i = 0; i < closes.Length; i++)
        {
            bars[i] = new Bar(
                DateTimeOffset.FromUnixTimeSeconds(i * SecondsPerBar),
                closes[i],
                closes[i],
                closes[i],
                closes[i],
                1m);
        }

        return bars;
    }

    /// <summary>
    /// The seed of the golden walks (T-111): twelve closes in a straight
    /// ramp (1..12), then the fall (12, 10, 8, 6, 4, 2), then the recovery
    /// (10, 12, 14). Every crossing of the demo's fast/slow averages lands
    /// on a hand-countable index: the stance per closed candle walks
    /// Neutral(5) > Long(9) > Flat(5) > Long(2) - one stance per candle,
    /// twenty-one candles, all three stances exercised.
    /// </summary>
    public static Bar[] GoldenCandles() =>
        Candles(
            1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12,
            12, 10, 8, 6, 4, 2,
            10, 12, 14);

    /// <summary>
    /// The golden the demo's slow average (six) yields: the per-candle
    /// stance over the window-so-far, candle by candle.
    /// </summary>
    public static StrategyStance[] ExpectedGoldenStances { get; } =
    [
        StrategyStance.Neutral, StrategyStance.Neutral, StrategyStance.Neutral,
        StrategyStance.Neutral, StrategyStance.Neutral,
        StrategyStance.Long, StrategyStance.Long, StrategyStance.Long,
        StrategyStance.Long, StrategyStance.Long, StrategyStance.Long,
        StrategyStance.Long, StrategyStance.Long, StrategyStance.Long,
        StrategyStance.Flat, StrategyStance.Flat, StrategyStance.Flat,
        StrategyStance.Flat, StrategyStance.Flat,
        StrategyStance.Long, StrategyStance.Long,
    ];

    /// <summary>The M1 candle spacing (Unix seconds between consecutive open times).</summary>
    private const int SecondsPerBar = 60;
}
