namespace ZettaTrade.App.Services;

/// <summary>
/// What crosses what in an indicator-cross alert (T-203): the comparison
/// source the cross condition reads. The board's two headline cases map
/// exactly — an oscillator crossing its canonical level (RSI 70/30) reads
/// the INDICATOR's own value against a fixed level, and a price cross of a
/// moving average reads the PRICE against the indicator's value (the
/// moving level the MA itself draws). Neither substitutes for the other:
/// the source is a named, stored fact, never a guess.
/// </summary>
public enum AlertCrossSource
{
    /// <summary>The indicator's own value crosses the stored fixed level (RSI crosses 70/30).</summary>
    IndicatorValue,

    /// <summary>The candle's close price crosses the indicator's value at the same close (the price crosses its MA).</summary>
    Price,
}
