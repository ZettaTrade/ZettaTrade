namespace ZettaTrade.UI.Tests.Charting;

using System;
using FluentAssertions;
using Xunit;
using ZettaTrade.Core.Entities;
using ZettaTrade.UI.Charting;

/// <summary>
/// Pins for the drawn-series price spread (T-089): the per-type axis feed —
/// candles, OHLC bars and Heikin-Ashi spread over every bar's low–high
/// range while the line and area variants spread over the closes only (the
/// wicks are not the drawn geometry, the terminal close-line scale). Pure
/// facts — the canvas itself is UIA-invisible.
/// </summary>
public sealed class ChartSeriesSpreadTests
{
    /// <summary>Base instant the windows step through (UTC, offset zero).</summary>
    private static readonly DateTimeOffset Base = new(2026, 3, 2, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// A window whose wicks reach far past the closes: the OHLC-family spread
    /// covers them both ways — the drawn wick geometry never clips the plot.
    /// </summary>
    [Fact]
    public void Of_OhlcFamilySpreadsOverTheWicks()
    {
        Bar[] bars =
        [
            new(Base, 10m, 20m, 5m, 11m, 1m),
            new(Base.AddMinutes(1), 8m, 9m, 6m, 8.5m, 1m),
            new(Base.AddMinutes(2), 11m, 12m, 7m, 11.9m, 1m),
        ];

        var (low, high) = ChartSeriesSpread.Of(bars, closesOnly: false);

        low.Should().Be(5m, "the lowest drawn wick");
        high.Should().Be(20m, "the highest drawn wick");
    }

    /// <summary>
    /// The same window on a close-line variant: the spread is the closes'
    /// range — the wicks are not part of the drawn geometry, so the scale
    /// keeps the line readable (the terminal close-line scale).
    /// </summary>
    [Fact]
    public void Of_CloseLineSpreadsOverTheClosesOnly()
    {
        Bar[] bars =
        [
            new(Base, 10m, 20m, 5m, 11m, 1m),
            new(Base.AddMinutes(1), 8m, 9m, 6m, 8.5m, 1m),
            new(Base.AddMinutes(2), 11m, 12m, 7m, 11.9m, 1m),
        ];

        var (low, high) = ChartSeriesSpread.Of(bars, closesOnly: true);

        low.Should().Be(8.5m, "the smallest drawn close");
        high.Should().Be(11.9m, "the largest drawn close");
    }

    /// <summary>
    /// The Heikin-Ashi share of the contract: the drawn slice already carries
    /// the envelope values, so the OHLC-family spread over the transformed
    /// slice is the padded scale's feed on a Heikin-Ashi frame — and it stays
    /// superset to the raw window's spread (the overlays never slide).
    /// </summary>
    [Fact]
    public void Of_HeikinAshiSliceFeedsThePaddedScale_SupersetToTheRawSpread()
    {
        Bar[] raw =
        [
            new(Base, 10m, 12m, 8m, 9m, 1m),
            new(Base.AddMinutes(1), 9m, 11m, 7m, 8m, 1m),
            new(Base.AddMinutes(2), 8.5m, 9m, 7.5m, 8.8m, 1m),
        ];

        var ha = ZettaTrade.Charting.HeikinAshi.Compute(raw);

        var (rawLow, rawHigh) = ChartSeriesSpread.Of(raw, closesOnly: false);
        var (haLow, haHigh) = ChartSeriesSpread.Of(ha, closesOnly: false);

        haLow.Should().BeLessThanOrEqualTo(rawLow, "the HA envelope contains the raw spread");
        haHigh.Should().BeGreaterThanOrEqualTo(rawHigh);
    }

    /// <summary>A single-bar window degenerates into its own two extremes.</summary>
    [Fact]
    public void Of_ASingleBar_SpansItsOwnExtremes()
    {
        Bar single = new(Base, 10m, 20m, 5m, 11m, 1m);

        var (ohlcLow, ohlcHigh) = ChartSeriesSpread.Of([single], closesOnly: false);
        var (closeLow, closeHigh) = ChartSeriesSpread.Of([single], closesOnly: true);

        ohlcLow.Should().Be(5m);
        ohlcHigh.Should().Be(20m);
        closeLow.Should().Be(11m);
        closeHigh.Should().Be(11m);
    }
}
