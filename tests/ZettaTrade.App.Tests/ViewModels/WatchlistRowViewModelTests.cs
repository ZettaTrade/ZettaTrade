namespace ZettaTrade.App.Tests.ViewModels;

using System.Globalization;
using FluentAssertions;
using Xunit;
using ZettaTrade.App.Services;
using ZettaTrade.App.ViewModels;

/// <summary>
/// The watchlist row ViewModel's facts (T-120, T-159): an unpriced row
/// binds empty texts and an empty sparkline with no color flags, a priced
/// quote formats through the pure helpers at the active culture and maps
/// its ring onto the canvas, the change text's tint follows the 24h sign,
/// the sparkline's tint follows the ring's own direction, and a re-pulsed
/// row with unchanged signs raises nothing (the stable-identity churn
/// rule holds through the color flags).
/// </summary>
public sealed class WatchlistRowViewModelTests
{
    [Fact]
    public void AnUnpricedRow_BindsEmptyShapes()
    {
        var row = new WatchlistRowViewModel("BTC/USDT");

        row.ApplyQuote(new WatchlistQuote("BTC/USDT", null, null, null, []));

        row.SymbolText.Should().Be("BTC/USDT");
        row.LastPriceText.Should().BeEmpty();
        row.Change24hText.Should().BeEmpty();
        row.Volume24hText.Should().BeEmpty();
        row.SparklinePoints.Should().BeEmpty();
        row.IsChangeUp.Should().BeFalse("no change, no tint — the plain ink is the no-signal state");
        row.IsChangeDown.Should().BeFalse();
        row.IsSparklineUp.Should().BeFalse("no ring, no direction tint");
        row.IsSparklineDown.Should().BeFalse();
    }

    [Fact]
    public void AQuote_FormatsAtTheActiveCulture_AndMapsItsRing()
    {
        var row = new WatchlistRowViewModel("BTC/USDT");
        var previousCulture = CultureInfo.CurrentCulture;

        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            row.ApplyQuote(new WatchlistQuote(
                "BTC/USDT",
                65432.10m,
                +1.25m,
                1_520_000m,
                new decimal[] { 10m, 0m }));
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }

        row.LastPriceText.Should().Be("65,432.10");
        row.Change24hText.Should().Be("+1.25%");
        row.Volume24hText.Should().Be("1.52M");
        row.SparklinePoints.Should().HaveCount(2);
        row.SparklinePoints[0].Y.Should().Be(0, "the highest price rides the top");
        row.SparklinePoints[1].Y.Should().Be(28, "the lowest price rides the bottom");
        row.IsChangeUp.Should().BeTrue("the +1.25% change tints the change text with the gain ink");
        row.IsChangeDown.Should().BeFalse();
        row.IsSparklineDown.Should().BeTrue("the {10 -> 0} ring falls oldest to newest — the sparkline speaks the ring's own direction");
        row.IsSparklineUp.Should().BeFalse();
    }

    /// <summary>
    /// The change-text tint follows the 24h change's SIGN (T-159): a gain
    /// raises the up flag, a loss the down flag, a flat change keeps neither
    /// (the plain ink is the no-signal state — a zero-day is not a gain).
    /// </summary>
    [Fact]
    public void TheChangeFlags_FollowThe24hSign()
    {
        var row = new WatchlistRowViewModel("BTC/USDT");

        row.ApplyQuote(new WatchlistQuote("BTC/USDT", 65432.10m, +1.25m, null, [10m, 11m]));

        row.IsChangeUp.Should().BeTrue();
        row.IsChangeDown.Should().BeFalse();

        row.ApplyQuote(new WatchlistQuote("BTC/USDT", 64100.00m, -0.50m, null, [11m, 10m]));

        row.IsChangeDown.Should().BeTrue("the loss tints the change text with the loss ink");
        row.IsChangeUp.Should().BeFalse("the sign flipped — the gain ink must not linger");

        row.ApplyQuote(new WatchlistQuote("BTC/USDT", 65432.10m, 0m, null, [10m, 10m]));

        row.IsChangeUp.Should().BeFalse("a flat day is no gain");
        row.IsChangeDown.Should().BeFalse("a flat day is no loss — the plain ink carries it");
    }

    /// <summary>
    /// The sparkline tint follows the price ring's oldest-to-newest
    /// direction (T-159) — the ring's own truth, deliberately independent
    /// of the 24h sign: the same pulse can carry a green day and a dipping
    /// ring, and the two inks tell the two truths apart. A short ring (fewer
    /// than two prices) has no direction to read and a flat ring maps to
    /// the midline — neither flag rises for either.
    /// </summary>
    [Fact]
    public void TheSparklineFlags_FollowTheRingDirection()
    {
        var row = new WatchlistRowViewModel("BTC/USDT");

        row.ApplyQuote(new WatchlistQuote("BTC/USDT", 65000m, +1m, null, [10m, 12m]));

        row.IsSparklineUp.Should().BeTrue("the ring rises 10 to 12");
        row.IsSparklineDown.Should().BeFalse();

        row.ApplyQuote(new WatchlistQuote("BTC/USDT", 64900m, +1m, null, [12m, 10m]));

        row.IsSparklineDown.Should().BeTrue("the ring falls 12 to 10 even though the day's sum stays green — the two truths are independent");
        row.IsSparklineUp.Should().BeFalse();

        row.ApplyQuote(new WatchlistQuote("BTC/USDT", 65000m, null, null, [10m, 10m]));

        row.IsSparklineUp.Should().BeFalse("a flat ring maps to the midline — no direction to color");
        row.IsSparklineDown.Should().BeFalse();

        row.ApplyQuote(new WatchlistQuote("BTC/USDT", 65000m, null, null, [10m]));

        row.IsSparklineUp.Should().BeFalse("a one-price ring draws nothing — no direction to color");
        row.IsSparklineDown.Should().BeFalse();
    }

    /// <summary>
    /// The churn discipline holds through the color flags too: a re-pulsed
    /// row with an unchanged sign raises nothing — the feed's pulse does not
    /// churn the row's bindings for facts that did not change (the T-120
    /// stable-identity rule the feed-paced update path rides).
    /// </summary>
    [Fact]
    public void RepulsingTheSameSigns_RaisesNoColorFlag()
    {
        var row = new WatchlistRowViewModel("BTC/USDT");
        var colorFlagRaises = 0;
        row.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(row.IsChangeUp) or nameof(row.IsChangeDown)
                or nameof(row.IsSparklineUp) or nameof(row.IsSparklineDown))
            {
                colorFlagRaises++;
            }
        };

        row.ApplyQuote(new WatchlistQuote("BTC/USDT", 65432.10m, +1.25m, null, [10m, 12m]));
        var afterFirstPulse = colorFlagRaises;
        afterFirstPulse.Should().Be(2, "the first priced pulse flips the up pair from their defaults");

        row.ApplyQuote(new WatchlistQuote("BTC/USDT", 65440.00m, +2.00m, null, [10m, 15m]));

        colorFlagRaises.Should().Be(afterFirstPulse, "the same signs re-pulsed raise nothing — RaiseAndSetIfChanged keeps the pulse churn to real flips");
    }
}
