namespace ZettaTrade.UI.Tests.Charting;

using System;
using System.Collections.Generic;
using FluentAssertions;
using Xunit;
using ZettaTrade.Charting;
using ZettaTrade.Core.Entities;
using ZettaTrade.UI.Charting;

/// <summary>
/// The signal-marker placement goldens (T-112): the pure resolve pass maps
/// a marker onto the visible slice by THE CANDLE'S OWN open time — a hit
/// always names the slot that candle rode, with the marker's own close and
/// role — so a marker can never sit next to its signal's candle. A marker
/// whose candle is not in the slice (off-window either side) resolves to
/// nothing, and markers unridden by the window never disturb the others
/// (the two-pointer walk's own facts).
/// </summary>
public sealed class ChartSignalLayoutTests
{
    /// <summary>Base instant the crafted candles step through (UTC, offset zero).</summary>
    private static readonly DateTimeOffset Base = new(2026, 3, 2, 0, 0, 0, TimeSpan.Zero);

    /// <summary>One crafted bar at <paramref name="minute"/> with the given close.</summary>
    private static Bar Bar(int minute, decimal close) =>
        new(Base.AddMinutes(minute), close - 1m, close, close - 2m, close, 100m);

    /// <summary>One marker standing on the bar opened at <paramref name="minute"/>.</summary>
    private static SignalMarker Marker(int minute, decimal close, SignalMarkerRole role) =>
        new(Bar(minute, close), role);

    [Fact]
    public void Resolve_MatchingCandleTimes_AnchorExactlyOnTheirOwnSlots()
    {
        var bars = new[] { Bar(0, 10m), Bar(10, 11m), Bar(20, 12m), Bar(30, 13m), Bar(40, 14m) };
        var markers = new SignalMarker[] { Marker(10, 11m, SignalMarkerRole.Long), Marker(30, 13m, SignalMarkerRole.Flat) };

        var hits = ChartSignalLayout.Resolve(markers, bars);

        hits.Should().HaveCount(2);
        hits[0].Should().Be(new SignalMarkerHit(1, 11m, SignalMarkerRole.Long), "the first marker's own candle is the slice's second bar");
        hits[1].Should().Be(new SignalMarkerHit(3, 13m, SignalMarkerRole.Flat), "the second marker's own candle is the slice's fourth bar");
    }

    [Fact]
    public void Resolve_MarkerWhoseCandleIsOffTheWindow_ResolvesToNothing()
    {
        var bars = new[] { Bar(20, 12m), Bar(30, 13m), Bar(40, 14m) };
        var markers = new SignalMarker[]
        {
            Marker(0, 10m, SignalMarkerRole.Long), // before the window's left edge
            Marker(30, 13m, SignalMarkerRole.Long), // inside
            Marker(50, 15m, SignalMarkerRole.Flat), // beyond the window's right edge
        };

        var hits = ChartSignalLayout.Resolve(markers, bars);

        hits.Should().ContainSingle("only the marker whose candle the window displays anchors");
        hits[0].Slot.Should().Be(1);
        hits[0].Close.Should().Be(13m);
    }

    [Fact]
    public void Resolve_TwoMarkersOnOneCandle_TwoHitsOnTheSameSlot()
    {
        var bars = new[] { Bar(0, 10m), Bar(10, 11m), Bar(20, 12m) };
        var markers = new SignalMarker[]
        {
            Marker(10, 11m, SignalMarkerRole.Long),
            Marker(10, 11m, SignalMarkerRole.Flat),
        };

        var hits = ChartSignalLayout.Resolve(markers, bars);

        hits.Should().HaveCount(2);
        hits[0].Slot.Should().Be(1);
        hits[1].Slot.Should().Be(1);
    }

    [Fact]
    public void Resolve_EmptyInputs_Nothing()
    {
        var bars = new[] { Bar(0, 10m), Bar(10, 11m) };

        ChartSignalLayout.Resolve([], bars).Should().BeEmpty("no markers, no hits");
        ChartSignalLayout.Resolve(new SignalMarker[] { Marker(0, 10m, SignalMarkerRole.Long) }, [])
            .Should().BeEmpty("an empty slice anchors nothing");
    }

    [Fact]
    public void Resolve_MarkersAgainstTheSlice_KeepTheirComposerOrder()
    {
        var bars = new[] { Bar(0, 10m), Bar(10, 11m), Bar(20, 12m), Bar(30, 13m) };
        var markers = new SignalMarker[]
        {
            Marker(0, 10m, SignalMarkerRole.Long),
            Marker(20, 12m, SignalMarkerRole.Flat),
            Marker(30, 13m, SignalMarkerRole.Long),
        };

        var hits = ChartSignalLayout.Resolve(markers, bars);

        hits.Select(static hit => hit.Slot).Should().Equal(0, 2, 3);
        hits.Select(static hit => hit.Role).Should().Equal(
            SignalMarkerRole.Long,
            SignalMarkerRole.Flat,
            SignalMarkerRole.Long);
    }

    [Fact]
    public void Resolve_MarkerOrder_IsPreserved_OuterTypesExcused()
    {
        // The VM publishes IReadOnlyList<SignalMarker>; the slice rides an
        // array — same walk, interface-typed affinity pinned.
        IReadOnlyList<SignalMarker> markers = new List<SignalMarker> { Marker(10, 11m, SignalMarkerRole.Long) };
        IReadOnlyList<Bar> bars = new List<Bar> { Bar(0, 10m), Bar(10, 11m), Bar(20, 12m) };

        ChartSignalLayout.Resolve(markers, bars).Should().ContainSingle()
            .Which.Slot.Should().Be(1);
    }
}
