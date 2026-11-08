namespace ZettaTrade.UI.Tests.Charting;

using System;
using FluentAssertions;
using Xunit;
using ZettaTrade.Charting;
using ZettaTrade.Core.Entities;

/// <summary>
/// Pins for the VWAP overlay's session anchor (T-093): the series' first bar
/// always anchors, same-day bars keep the session and the UTC calendar-day
/// boundary breaks it — the acceptance's day-boundary rule, pinned as pure
/// facts.
/// </summary>
public sealed class VwapSessionAnchorTests
{
    /// <summary>The day the anchor facts build on (UTC, offset zero).</summary>
    private static readonly DateTimeOffset DayStart = new(2026, 3, 2, 0, 0, 0, TimeSpan.Zero);

    private static Bar At(DateTimeOffset time) => new(time, 10m, 12m, 8m, 11m, 5m);

    [Fact]
    public void TheSeriesFirstBar_AlwaysOpensANewSession()
    {
        VwapSessionAnchor.OpensNewSession(null, At(DayStart.AddHours(9)))
            .Should().BeTrue("the series head has no previous bar to hold a session open");
    }

    [Fact]
    public void BarsOfOneCalendarDay_StayInTheSameSession()
    {
        var previous = At(DayStart.AddHours(9));
        var current = At(DayStart.AddHours(23));

        VwapSessionAnchor.OpensNewSession(previous, current)
            .Should().BeFalse("both opens fall on the same UTC date");
    }

    [Fact]
    public void ABarOpeningAtMidnight_BreaksTheSession()
    {
        var previous = At(DayStart.AddHours(23));
        var current = At(DayStart.AddDays(1));

        VwapSessionAnchor.OpensNewSession(previous, current)
            .Should().BeTrue("the UTC calendar day rolled between the two opens");
    }

    [Fact]
    public void EqualInstantsInDifferentOffsets_StayInTheSameSession()
    {
        // The same instant expressed in two offsets (18:00 UTC = 21:00 +03):
        // the anchor rides UTC instants, not the bars' expressed offsets.
        var previous = At(DayStart.AddHours(18).ToOffset(TimeSpan.FromHours(3)));
        var current = At(DayStart.AddHours(18));

        VwapSessionAnchor.OpensNewSession(previous, current)
            .Should().BeFalse("one UTC instant is one session's instant regardless of the offset it is expressed in");
    }
}
