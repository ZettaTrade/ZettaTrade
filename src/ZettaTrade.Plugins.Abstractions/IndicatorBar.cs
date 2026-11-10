namespace ZettaTrade.Plugins.Abstractions;

/// <summary>
/// One OHLC candle the indicator contract receives (contract 2.0.0, T-163):
/// the SDK-owned shape out-of-tree plugins compute against, independent of
/// the application's internal candle entity. Positional, immutable and
/// allocation-free - an indicator reads the four prices and never mutates
/// the series. The ascending open-time order the host guarantees carries
/// the time axis; the bar itself stays time-free, plugins that need the
/// timestamp own their own clock bookkeeping from the bar index.
/// </summary>
public readonly record struct IndicatorBar(decimal Open, decimal High, decimal Low, decimal Close);
