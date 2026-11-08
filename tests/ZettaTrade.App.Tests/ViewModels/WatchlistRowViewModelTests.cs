namespace ZettaTrade.App.Tests.ViewModels;

using System.Globalization;
using FluentAssertions;
using Xunit;
using ZettaTrade.App.Services;
using ZettaTrade.App.ViewModels;

/// <summary>
/// The watchlist row ViewModel's facts (T-120): an unpriced row binds empty
/// texts and an empty sparkline, a priced quote formats through the pure
/// helpers at the active culture and maps its ring onto the canvas.
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
    }
}
