namespace ZettaTrade.UI.Tests.Charting;

using System;
using System.Linq;
using FluentAssertions;
using Xunit;
using ZettaTrade.Charting;
using ZettaTrade.Core.Entities;

/// <summary>
/// Pins for the naked-POC track (T-248): the per-session POC over crafted
/// bars, the revisit lifecycle (a later bar's touch clears the POC, a
/// no-touch session lets it persist), the own-session immunity (a session
/// never answers to its own POC), the weekend anchor and the honest
/// absences. Pure facts — the canvas itself is UIA-invisible.
/// </summary>
public sealed class NakedPocTrackTests
{
    /// <summary>The Monday the goldens build their sessions on (UTC, offset zero).</summary>
    private static readonly DateTimeOffset Monday = new(2026, 3, 2, 0, 0, 0, TimeSpan.Zero);

    /// <summary>A flat bar that prints exactly one price all through
    /// (open, high, low and close carry it) — its volume rides the day
    /// profile's own single bucket, so the session POC IS the price.</summary>
    private static Bar FlatBar(DateTimeOffset time, decimal price, decimal volume) =>
        new(time, price, price, price, price, volume);

    /// <summary>One session's worth of flat bars at one price: the day
    /// profile's spread is the single price, so the POC answers it
    /// exactly.</summary>
    private static Bar[] FlatDay(DateTimeOffset day, decimal price, decimal volume, int barCount = 2)
    {
        var bars = new Bar[barCount];
        for (var i = 0; i < barCount; i++)
        {
            bars[i] = FlatBar(day.AddHours(8 + i), price, volume);
        }

        return bars;
    }

    /// <summary>
    /// The lifecycle golden: a later session's touch (the first bar whose
    /// range covers the POC price) clears the earlier session's POC; the
    /// touching session's own POC is the track's only survivor — naked by
    /// construction (no later session exists).
    /// </summary>
    [Fact]
    public void Compute_ALaterTouch_ClearsTheEarlierSessionPoint()
    {
        var bars = FlatDay(Monday, 100m, 5m)
            .Concat(FlatDay(Monday.AddDays(1), 100m, 1m, barCount: 1))
            .ToArray();

        var track = NakedPocTrack.Compute(bars);

        track.Items.Should().HaveCount(1, "Monday's POC (100) died at Tuesday's touch");
        track.Items[0].Session.Should().Be(Monday.AddDays(1));
        track.Items[0].Price.Should().Be(100m);
    }

    /// <summary>
    /// The no-touch persistence: a session that trades elsewhere lets the
    /// earlier POCs persist — the track carries both, in ascending session
    /// order.
    /// </summary>
    [Fact]
    public void Compute_AbsentTouches_KeepsEveryPointNaked()
    {
        var bars = FlatDay(Monday, 100m, 5m)
            .Concat(FlatDay(Monday.AddDays(1), 110m, 5m))
            .ToArray();

        var track = NakedPocTrack.Compute(bars);

        track.Items.Should().HaveCount(2);
        track.Items[0].Session.Should().Be(Monday, "the earlier session's POC survived");
        track.Items[0].Price.Should().Be(100m);
        track.Items[1].Session.Should().Be(Monday.AddDays(1));
        track.Items[1].Price.Should().Be(110m);
    }

    /// <summary>
    /// The own-session immunity: a session full of bars sitting exactly on
    /// its own POC never clears itself — the first golden's Monday POC (100)
    /// faces a whole day of 100-touching bars and survives them, answering
    /// only to the later days.
    /// </summary>
    [Fact]
    public void Compute_TheOwnSessionBars_NeverAnswerToTheirOwnPoint()
    {
        var bars = FlatDay(Monday, 100m, 5m, barCount: 4)
            .Concat(FlatDay(Monday.AddDays(1), 110m, 5m))
            .ToArray();

        var track = NakedPocTrack.Compute(bars);

        track.Items.Should().HaveCount(2);
        track.Items[0].Session.Should().Be(Monday, "Monday's own bars never cleared Monday's POC");
        track.Items[0].Price.Should().Be(100m);
    }

    /// <summary>
    /// A three-day shape pins the ordering and the middle survivor: the
    /// first day's POC dies at the third day's touch, the second survives,
    /// the third is naked by construction.
    /// </summary>
    [Fact]
    public void Compute_TheThreeDayShape_ClearsExactlyTheTouchedPoint()
    {
        var bars = FlatDay(Monday, 100m, 5m)
            .Concat(FlatDay(Monday.AddDays(1), 105m, 5m))
            .Concat(FlatDay(Monday.AddDays(2), 100m, 5m))
            .ToArray();

        var track = NakedPocTrack.Compute(bars);

        track.Items.Should().HaveCount(2);
        track.Items[0].Session.Should().Be(Monday.AddDays(1), "the second day's POC (105) was never touched");
        track.Items[0].Price.Should().Be(105m);
        track.Items[1].Session.Should().Be(Monday.AddDays(2));
        track.Items[1].Price.Should().Be(100m);
    }

    /// <summary>
    /// The weekend anchor: a stock shape's Friday and Monday sessions exist
    /// (the quiet days carry no bars, no weekend sessions appear), and the
    /// Monday touch clears the Friday POC exactly the same way.
    /// </summary>
    [Fact]
    public void Compute_TheStockWeekend_RidesTheUtcDayAnchor()
    {
        var friday = new DateTimeOffset(2026, 3, 6, 0, 0, 0, TimeSpan.Zero);
        var nextMonday = new DateTimeOffset(2026, 3, 9, 0, 0, 0, TimeSpan.Zero);
        var bars = FlatDay(friday, 100m, 5m)
            .Concat(FlatDay(nextMonday, 100m, 5m))
            .ToArray();

        var track = NakedPocTrack.Compute(bars);

        track.Items.Should().HaveCount(1, "Monday's touch cleared Friday's POC");
        track.Items[0].Session.Should().Be(nextMonday);
        track.Items.Should().NotContain(
            poc => poc.Session.DayOfWeek == DayOfWeek.Saturday || poc.Session.DayOfWeek == DayOfWeek.Sunday,
            "the quiet days carry no sessions at all");
    }

    /// <summary>
    /// The honest absences: an empty series carries no track; a session with
    /// no volume joins no POC.
    /// </summary>
    [Fact]
    public void Compute_TheHonestAbsences_CarryNoPoints()
    {
        NakedPocTrack.Compute([]).Items.Should().BeEmpty();

        var noVolume = NakedPocTrack.Compute(FlatDay(Monday, 100m, 0m));
        noVolume.Items.Should().BeEmpty();

        var mixed = NakedPocTrack.Compute(
            FlatDay(Monday, 100m, 0m)
                .Concat(FlatDay(Monday.AddDays(1), 110m, 5m))
                .ToArray());
        mixed.Items.Should().HaveCount(1, "the volume-less Monday joined no POC");
        mixed.Items[0].Session.Should().Be(Monday.AddDays(1));
    }
}
