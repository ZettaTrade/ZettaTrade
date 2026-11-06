namespace ZettaTrade.App.Services;

using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// The order dialogs' market reference rate: the latest H1 close resolved
/// through the wrapped market data adapter. It exists in the presentation
/// layer because the dialog derives user-input aggregates from the market —
/// the trailing stop's trigger seed, the market-relative sanity checks for
/// triggers and take-profit levels — and the same newest-by-BAR-TIME selection
/// contract the paper trading adapter uses for fills must hold here too.
/// </summary>
public static class MarketReference
{
    /// <summary>Time frame of the reference bars.</summary>
    private const TimeFrame ReferenceTimeFrame = TimeFrame.H1;

    /// <summary>Hours of lookback for the reference window.</summary>
    private const int ReferenceBarLookbackHours = 24;

    /// <summary>
    /// Bars fetched per request: one per hour of the lookback plus one spare,
    /// so a backend that caps by count still covers the whole window.
    /// </summary>
    private const int ReferenceBarFetchLimit = ReferenceBarLookbackHours + 1;

    /// <summary>
    /// The latest H1 close: the whole lookback window is fetched and the
    /// newest bar is selected BY ITS TIME, never by list position — backends
    /// page bars differently (Binance klines are oldest-first, Bybit and the
    /// demo adapter serve the latest first), so a position-based pick on a
    /// fixed limit silently reads a ~24-hour-old close on ascending backends
    /// (BUG-006). Fails loudly when no reference data is available: an
    /// order-building flow without a market price must not guess.
    /// </summary>
    public static async Task<Price> GetLatestCloseAsync(
        IMarketDataAdapter marketData,
        Symbol symbol,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(marketData);

        var bars = await marketData.GetBarsAsync(
            symbol,
            ReferenceTimeFrame,
            DateTimeOffset.UtcNow.AddHours(-ReferenceBarLookbackHours),
            limit: ReferenceBarFetchLimit,
            ct: ct);

        if (bars.Count == 0)
        {
            throw new InvalidOperationException(
                $"No reference bar available for {symbol.Normalized}; " +
                "the order's market-dependent parameters cannot be derived.");
        }

        var newest = bars.Aggregate((latest, candidate) =>
            candidate.Time > latest.Time ? candidate : latest);
        return new Price(newest.Close);
    }
}
