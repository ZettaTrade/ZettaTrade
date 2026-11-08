namespace ZettaTrade.UI.Tests.Charting;

using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Xunit;
using ZettaTrade.Charting;
using ZettaTrade.Core.Entities;

/// <summary>
/// Pins for the client-side Heikin-Ashi transform (T-089): the exact golden
/// numbers on crafted candles (the canonical formula, the first bar's
/// seeding, the chain's previous-bar dependence), the pass-through of open
/// times and volumes (index alignment), the empty input, and the containment
/// invariant that keeps the HA spread superset to the raw one — the overlays
/// never get displaced by the renderer variant. Pure facts — the canvas
/// itself is UIA-invisible.
/// </summary>
public sealed class HeikinAshiTests
{
    /// <summary>Base instant the goldens step through (UTC, offset zero).</summary>
    private static readonly DateTimeOffset Base = new(2026, 3, 2, 0, 0, 0, TimeSpan.Zero);

    /// <summary>One crafted raw bar.</summary>
    private static Bar Bar(int minute, decimal open, decimal high, decimal low, decimal close, decimal volume) =>
        new(Base.AddMinutes(minute), open, high, low, close, volume);

    /// <summary>
    /// The exact golden: three crafted bars keep every intermediate rational
    /// inside decimal's exact range, so each HA value reads back as its
    /// written fraction — the canonical formula (HA-Close, the seeded HA-Open,
    /// the previous-bar chain), the high/low envelopes, and the bar whose
    /// HA-Open exceeds its own raw high (the HA-High envelope wins).
    /// </summary>
    [Fact]
    public void Compute_CraftedKeepsEveryRationalExact_TheCanonicalFormulaHolds()
    {
        var bars = new[]
        {
            Bar(0, 10m, 12m, 8m, 9m, 100m),
            Bar(1, 9m, 11m, 7m, 8m, 120m),
            Bar(2, 8.5m, 9m, 7.5m, 8.8m, 140m),
        };

        var ha = HeikinAshi.Compute(bars);

        ha.Should().HaveCount(3);

        // Bar 1: seeded HA-Open (O + C) / 2 = 9.5; HA-Close (O+H+L+C)/4 = 9.75.
        ha[0].Open.Should().Be(9.5m, "the first bar seeds (O + C) / 2");
        ha[0].Close.Should().Be(9.75m, "(10 + 12 + 8 + 9) / 4");
        ha[0].High.Should().Be(12m, "the raw high dominates the envelope");
        ha[0].Low.Should().Be(8m);
        ha[0].Time.Should().Be(bars[0].Time, "open times pass through");
        ha[0].Volume.Should().Be(100m, "volumes pass through");

        // Bar 2: the chain — HA-Open = (prevHA-Open + prevHA-Close) / 2 = (9.5 + 9.75) / 2 = 9.625.
        ha[1].Open.Should().Be(9.625m, "(prevHA-Open + prevHA-Close) / 2 — the chain, never the raw open");
        ha[1].Close.Should().Be(8.75m, "(9 + 11 + 7 + 8) / 4");
        ha[1].High.Should().Be(11m);
        ha[1].Low.Should().Be(7m);
        ha[1].Volume.Should().Be(120m);

        // Bar 3: HA-Open = (9.625 + 8.75) / 2 = 9.1875 — ABOVE the raw high 9:
        // the HA envelope takes it instead.
        ha[2].Open.Should().Be(9.1875m);
        ha[2].Close.Should().Be(8.45m, "(8.5 + 9 + 7.5 + 8.8) / 4");
        ha[2].High.Should().Be(9.1875m, "max(H, HA-Open, HA-Close) even above the raw high");
        ha[2].Low.Should().Be(7.5m);
    }

    /// <summary>A single bar seeds from itself and keeps its raw envelope.</summary>
    [Fact]
    public void Compute_ASingleBar_SeedsItsOwnOpen()
    {
        var ha = HeikinAshi.Compute([Bar(0, 100m, 110m, 90m, 104m, 5m)]);

        ha.Should().HaveCount(1);
        ha[0].Open.Should().Be(102m, "(O + C) / 2 = (100 + 104) / 2");
        ha[0].Close.Should().Be(101m, "(100 + 110 + 90 + 104) / 4");
        ha[0].High.Should().Be(110m);
        ha[0].Low.Should().Be(90m);
    }

    /// <summary>An empty series transforms to an empty series (never a throw).</summary>
    [Fact]
    public void Compute_AnEmptySeries_YieldsAnEmptySeries()
    {
        HeikinAshi.Compute([]).Should().BeEmpty();
    }

    /// <summary>
    /// The index-alignment invariant: open times and volumes pass through
    /// untouched, so the HA series stays slot-aligned with its source — the
    /// chart slices it with the same window and the volume-backed consumers
    /// keep their semantics.
    /// </summary>
    [Fact]
    public void Compute_KeepsEveryOpenTimeAndVolume_IndexAligned()
    {
        Bar[] bars =
        [
            Bar(0, 10m, 12m, 8m, 9m, 7m),
            Bar(1, 9m, 11m, 7m, 8m, 9m),
            Bar(2, 8.5m, 9m, 7.5m, 8.8m, 11m),
            Bar(3, 8.8m, 10m, 8.2m, 9.9m, 13m),
        ];

        var ha = HeikinAshi.Compute(bars);

        ha.Should().HaveCount(bars.Length);
        for (var i = 0; i < bars.Length; i++)
        {
            ha[i].Time.Should().Be(bars[i].Time, $"bar {i} keeps its open time");
            ha[i].Volume.Should().Be(bars[i].Volume, $"bar {i} keeps its raw volume");
        }
    }

    /// <summary>
    /// The containment invariant: every HA-High >= its raw High and every
    /// HA-Low <= its raw Low (the per-bar envelope never shrinks), so the HA
    /// window's spread is always superset to the raw window's — the padded
    /// scale of a Heikin-Ashi frame covers every raw price, and the
    /// raw-aligned overlays never slide off the scale because the renderer
    /// switched.
    /// </summary>
    [Fact]
    public void Compute_EnvelopesContainTheRawSpread_EveryBar()
    {
        Bar[] bars =
        [
            Bar(0, 10m, 12m, 8m, 9m, 1m),
            Bar(1, 9m, 11m, 7m, 8m, 2m),
            Bar(2, 8.5m, 9m, 7.5m, 8.8m, 3m),
            Bar(3, 8.8m, 14m, 8.2m, 9.9m, 4m),
            Bar(4, 10m, 10.5m, 5m, 10.4m, 5m),
        ];

        var ha = HeikinAshi.Compute(bars);

        for (var i = 0; i < bars.Length; i++)
        {
            ha[i].High.Should().BeGreaterThanOrEqualTo(bars[i].High, $"bar {i} HA-High envelope");
            ha[i].Low.Should().BeLessThanOrEqualTo(bars[i].Low, $"bar {i} HA-Low envelope");
        }

        ha.Max(static bar => bar.High).Should().BeGreaterThanOrEqualTo(
            bars.Max(static bar => bar.High),
            "the HA window's top covers the raw window's top — the per-bar envelopes bound the extremes");
        ha.Min(static bar => bar.Low).Should().BeLessThanOrEqualTo(
            bars.Min(static bar => bar.Low),
            "the HA window's bottom covers the raw window's bottom — the per-bar envelopes bound the extremes");
    }

    /// <summary>
    /// The transform is pure: the input series is never mutated and a second
    /// compute over the same input is the same series (deterministic).
    /// </summary>
    [Fact]
    public void Compute_IsPureAndDeterministic()
    {
        var bars = new List<Bar>
        {
            Bar(0, 10m, 12m, 8m, 9m, 1m),
            Bar(1, 9m, 11m, 7m, 8m, 2m),
        };

        var first = HeikinAshi.Compute(bars);
        var second = HeikinAshi.Compute(bars);

        second.Should().Equal(first, "the same input transforms identically");
        bars[0].Should().Be(new Bar(Base, 10m, 12m, 8m, 9m, 1m), "the input is never mutated");
        bars.Select(static bar => bar.High).Should().ContainInOrder(12m, 11m);
    }
}
