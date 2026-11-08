namespace ZettaTrade.Charting;

/// <summary>
/// The price-chart rendering type the chart panel offers (T-089): every
/// variant renders from the same candle source — the four extra types are
/// renderer variants on the existing candle pipeline, never a second data
/// path. The choice is the selector state the chart persists per workspace
/// symbol; <see cref="Candlesticks"/> is the type every session had before
/// the selector existed, so it is also the restore default.
/// </summary>
public enum ChartSeriesType
{
    /// <summary>The classic OHLC candlesticks: body between the open and the close, wicks to the high and the low.</summary>
    Candlesticks,

    /// <summary>A close-price polyline over the bar slots — the intraday standard read.</summary>
    Line,

    /// <summary>The close-price polyline with a translucent fill down to the plot's bottom edge.</summary>
    Area,

    /// <summary>Classic OHLC bars: a high–low stem with a left open tick and a right close tick.</summary>
    Bars,

    /// <summary>Heikin-Ashi candles (T-089): the client-side transform over the loaded OHLC series — see <see cref="HeikinAshi.Compute(IReadOnlyList{ZettaTrade.Core.Entities.Bar})"/>.</summary>
    HeikinAshi,
}
