namespace ZettaTrade.App.Tests.ViewModels;

using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Xunit;
using ZettaTrade.App.ViewModels;
using ZettaTrade.Charting;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Strategies;

/// <summary>
/// The signal-marker compose goldens (T-112): a displayed bar whose open
/// the stance registry knows anchors exactly one marker on ITSELF (the
/// marker carries the bar's own candle), the stance mapping is Long ->
/// up-triangle / Flat -> down-triangle, and Neutral is an honest
/// non-reading that never markers. Pure facts — the publish pass rides
/// them unchanged.
/// </summary>
public sealed class SignalMarkerComposeTests
{
    /// <summary>Base instant the crafted candles step through (UTC, offset zero).</summary>
    private static readonly DateTimeOffset Base = new(2026, 3, 2, 0, 0, 0, TimeSpan.Zero);

    /// <summary>One crafted M5 bar at <paramref name="minute"/> with the given close.</summary>
    private static Bar Bar(int minute, decimal close) =>
        new(Base.AddMinutes(minute), close - 1m, close, close - 2m, close, 100m);

    [Fact]
    public void Compose_KnownOpens_AnchorOneMarkerOnTheirOwnBar()
    {
        var bars = new Bar[]
        {
            Bar(0, 10m),
            Bar(5, 11m),
            Bar(10, 12m),
            Bar(15, 13m),
            Bar(20, 14m),
        };
        var stances = new Dictionary<DateTimeOffset, StrategyStance>
        {
            [Base.AddMinutes(5)] = StrategyStance.Long,
            [Base.AddMinutes(10)] = StrategyStance.Neutral,
            [Base.AddMinutes(15)] = StrategyStance.Flat,
        };

        var markers = ChartViewModel.ComposeSignalMarkers(bars, stances);

        markers.Should().HaveCount(2, "the neutral reading is an absence and the unknow bar never markers");
        markers[0].Candle.Should().Be(bars[1], "the marker carries the bar's own candle");
        markers[0].Role.Should().Be(SignalMarkerRole.Long);
        markers[1].Candle.Should().Be(bars[3]);
        markers[1].Role.Should().Be(SignalMarkerRole.Flat);
    }

    [Fact]
    public void Compose_UnregisteredOrEmpty_InputStaysClean()
    {
        var bars = new Bar[] { Bar(0, 10m), Bar(5, 11m) };

        ChartViewModel.ComposeSignalMarkers(bars, new Dictionary<DateTimeOffset, StrategyStance>())
            .Should().BeEmpty("a registry without the candle's open anchors nothing");

        ChartViewModel.ComposeSignalMarkers(
                Array.Empty<Bar>(),
                new Dictionary<DateTimeOffset, StrategyStance> { [Base] = StrategyStance.Long })
            .Should().BeEmpty("a displayed series of nothing has nothing to anchor");
    }

    [Fact]
    public void Compose_NeutralOnlyNever_MarkersAnything()
    {
        var bars = new Bar[] { Bar(0, 10m), Bar(5, 11m) };
        var stances = new Dictionary<DateTimeOffset, StrategyStance>
        {
            [Base.AddMinutes(0)] = StrategyStance.Neutral,
            [Base.AddMinutes(5)] = StrategyStance.Neutral,
        };

        ChartViewModel.ComposeSignalMarkers(bars, stances).Should().BeEmpty();
    }
}
