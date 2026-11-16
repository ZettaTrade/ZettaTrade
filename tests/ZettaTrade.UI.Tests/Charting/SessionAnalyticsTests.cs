namespace ZettaTrade.UI.Tests.Charting;

using System;
using FluentAssertions;
using Xunit;
using ZettaTrade.Charting;
using ZettaTrade.Core.Entities;

/// <summary>
/// Pins for the session analytics compute (T-247): the exact golden numbers
/// on crafted bar sets — the daily open's reset at the UTC calendar-date
/// boundary (the <see cref="VwapSessionAnchor"/> rule), the Monday-anchored
/// weekly open (the T-037 calendar's own weekly anchor), the weekend and DST
/// fixtures, the opening-range window's weigh and its honest sparse-day
/// nulls, the fixed UTC session partition and the slice alignment. Pure
/// facts — the canvas itself is UIA-invisible.
/// </summary>
public sealed class SessionAnalyticsTests
{
    /// <summary>The Monday the goldens build their week on (UTC, offset zero).</summary>
    private static readonly DateTimeOffset WeekStart = new(2026, 3, 2, 0, 0, 0, TimeSpan.Zero);

    /// <summary>A bar with the open, high and low spelled explicitly (the
    /// opening-range goldens read the range's two edges).</summary>
    private static Bar Bar(DateTimeOffset time, decimal open, decimal high, decimal low) =>
        new(time, open, high, low, open + 1m, 1m);

    /// <summary>A flat bar whose open is the only value the goldens read.</summary>
    private static Bar FlatBar(DateTimeOffset time, decimal open) =>
        new(time, open, open + 2m, open - 1m, open + 1m, 1m);

    #region The Day Anchor

    /// <summary>
    /// The daily open resets exactly where the VWAP session resets — the UTC
    /// calendar-date boundary — and rides every bar of the owning day.
    /// </summary>
    [Fact]
    public void Compute_CrossingTheUtcDayBoundary_OpensTheNewDayOnItsFirstBar()
    {
        var bars = new[]
        {
            FlatBar(WeekStart.AddHours(22), 10m),
            FlatBar(WeekStart.AddHours(23), 14m),
            FlatBar(WeekStart.AddDays(1), 20m),
            FlatBar(WeekStart.AddDays(1).AddHours(1), 12m),
        };

        var analytics = SessionAnalytics.Compute(bars, SessionAnalytics.DefaultOpeningRangeMinutes);

        analytics.State.Should().Be(SessionAnalyticsState.Computed);
        analytics.DailyOpens.Should().Equal([10m, 10m, 20m, 20m]);
        analytics.WeeklyOpens.Should().Equal([10m, 10m, 10m, 10m],
            "Monday and Tuesday share one Monday-anchored week: the weekly open never breaks mid-week");
    }

    /// <summary>
    /// The weekend fixture (T-037): a stock series' Friday→Monday weekend
    /// skip carries no bars, so the weekly open lands on the Monday bar —
    /// the T-037 weekly anchor's own first session day.
    /// </summary>
    [Fact]
    public void Compute_AStockWeekendSkip_LandsTheWeeklyOpenOnTheMondayBar()
    {
        var bars = new[]
        {
            FlatBar(WeekStart.AddDays(4).AddHours(21), 30m),
            FlatBar(WeekStart.AddDays(4).AddHours(22), 31m),
            FlatBar(WeekStart.AddDays(7), 40m),
            FlatBar(WeekStart.AddDays(7).AddHours(1), 41m),
        };

        var analytics = SessionAnalytics.Compute(bars, SessionAnalytics.DefaultOpeningRangeMinutes);

        analytics.DailyOpens.Should().Equal([30m, 30m, 40m, 40m]);
        analytics.WeeklyOpens.Should().Equal([30m, 30m, 40m, 40m],
            "Friday sits in the week of Monday the 2nd; the next bar IS the next week's Monday open");
    }

    /// <summary>
    /// A series that starts mid-week anchors its partial week at its own
    /// first bar — nothing outside the loaded window is ever assumed.
    /// </summary>
    [Fact]
    public void Compute_AMidWeekSeriesHead_AnchorsItsOwnPartialWeek()
    {
        var bars = new[]
        {
            FlatBar(WeekStart.AddDays(2).AddHours(10), 70m),
            FlatBar(WeekStart.AddDays(2).AddHours(11), 71m),
            FlatBar(WeekStart.AddDays(7), 80m),
        };

        var analytics = SessionAnalytics.Compute(bars, SessionAnalytics.DefaultOpeningRangeMinutes);

        analytics.WeeklyOpens.Should().Equal([70m, 70m, 80m],
            "the Wednesday head opens the partial week; the next Monday opens the next one");
        analytics.DailyOpens.Should().Equal([70m, 70m, 80m]);
    }

    #endregion

    #region The DST Fixture

    /// <summary>
    /// The DST fixture (T-037, the 2026 GMT→BST switch on March 29th): a
    /// London venue's 08:00 open rides 08:00 UTC before the switch (the
    /// Europe window) and 07:00 UTC after it (the Asia window) — the session
    /// mix drifts honestly with the venue's wall clock, never a fabricated
    /// alignment. The same golden carries the week break across the switch
    /// night (Friday → the next Monday).
    /// </summary>
    [Fact]
    public void Compute_TheLondonDstSwitch_DriftsTheSessionMixHonest()
    {
        var bars = new[]
        {
            FlatBar(new(2026, 3, 27, 8, 0, 0, TimeSpan.Zero), 50m),
            FlatBar(new(2026, 3, 27, 9, 0, 0, TimeSpan.Zero), 51m),
            FlatBar(new(2026, 3, 30, 7, 0, 0, TimeSpan.Zero), 60m),
            FlatBar(new(2026, 3, 30, 8, 0, 0, TimeSpan.Zero), 61m),
        };

        var analytics = SessionAnalytics.Compute(bars, SessionAnalytics.DefaultOpeningRangeMinutes);

        analytics.Slots.Should().Equal(
            [SessionSlot.Europe, SessionSlot.Europe, SessionSlot.Asia, SessionSlot.Europe],
            "08:00 GMT opens the Europe window; 07:00 UTC (08:00 BST) is the Asia window's honest drift");
        analytics.WeeklyOpens.Should().Equal([50m, 50m, 60m, 60m],
            "Friday the 27th and Monday the 30th sit in different Monday-anchored weeks");
    }

    #endregion

    #region The Session Partition

    /// <summary>The fixed UTC partition: Asia [00:00, 08:00), Europe
    /// [08:00, 16:00), US [16:00, 24:00).</summary>
    [Theory]
    [InlineData(0, SessionSlot.Asia)]
    [InlineData(7, SessionSlot.Asia)]
    [InlineData(8, SessionSlot.Europe)]
    [InlineData(15, SessionSlot.Europe)]
    [InlineData(16, SessionSlot.Us)]
    [InlineData(23, SessionSlot.Us)]
    public void Compute_TheUtcTimeOfDay_RidesTheFixedPartition(int hour, SessionSlot slot)
    {
        var bars = new[] { FlatBar(WeekStart.AddHours(hour), 10m) };

        var analytics = SessionAnalytics.Compute(bars, SessionAnalytics.DefaultOpeningRangeMinutes);

        analytics.Slots.Should().Equal([slot]);
    }

    #endregion

    #region The Opening Range

    /// <summary>
    /// The exact range golden: the bars whose opens fall inside
    /// [dayStart, dayStart + 30 min) weigh the day's highest high and lowest
    /// low, and the values ride every bar of the owning day — a bar past the
    /// deadline carries the standing range instead of re-weighing it.
    /// </summary>
    [Fact]
    public void Compute_TheOpeningRangeWindow_WeighsTheFirstMinutesAndRidesTheDay()
    {
        var day = WeekStart.AddDays(1);
        var bars = new[]
        {
            Bar(day, 10m, 12m, 9m),
            Bar(day.AddMinutes(15), 12m, 14m, 11m),
            Bar(day.AddHours(1), 20m, 22m, 8m),
        };

        var analytics = SessionAnalytics.Compute(bars, 30);

        analytics.RangeHigh.Should().Equal([14m, 14m, 14m],
            "the 01:00 bar's high of 22 stays out: its open passed the window's deadline");
        analytics.RangeLow.Should().Equal([9m, 9m, 9m],
            "the 01:00 bar's low of 8 stays out the same way");
    }

    /// <summary>
    /// The honest sparse day: a day whose first bar opens past the range
    /// window carries the null pair — the band skips the day instead of
    /// fabricating a range from late bars.
    /// </summary>
    [Fact]
    public void Compute_ADayWithoutBarsInsideTheWindow_CarriesTheHonestNulls()
    {
        var day = WeekStart.AddDays(1);
        var bars = new[]
        {
            Bar(day, 10m, 12m, 9m),
            Bar(day.AddMinutes(15), 12m, 14m, 11m),
            Bar(day.AddDays(1).AddHours(2), 20m, 25m, 18m),
        };

        var analytics = SessionAnalytics.Compute(bars, 30);

        analytics.RangeHigh.Should().Equal([14m, 14m, null]);
        analytics.RangeLow.Should().Equal([9m, 9m, null]);
    }

    /// <summary>
    /// The minutes clamp into the legal bounds: zero minutes weighs the
    /// window's first bar only, an over-large window folds onto the whole
    /// UTC day.
    /// </summary>
    [Fact]
    public void Compute_TheMinutesClampIntoTheLegalBounds()
    {
        var day = WeekStart.AddDays(1);
        var bars = new[]
        {
            Bar(day, 10m, 12m, 9m),
            Bar(day.AddMinutes(15), 12m, 14m, 11m),
            Bar(day.AddHours(23), 20m, 30m, 8m),
        };

        var tight = SessionAnalytics.Compute(bars, 0);
        var wide = SessionAnalytics.Compute(bars, 99_999);

        tight.RangeHigh.Should().Equal([12m, 12m, 12m],
            "zero clamps to one minute: only the day's first bar weighs");
        tight.RangeLow.Should().Equal([9m, 9m, 9m]);
        wide.RangeHigh.Should().Equal([30m, 30m, 30m],
            "the clamp's ceiling is one full day: every bar of the day weighs");
        wide.RangeLow.Should().Equal([8m, 8m, 8m]);
    }

    /// <summary>The window's legal bounds and the default ride the calculator's constants.</summary>
    [Fact]
    public void TheWindowBounds_AreTheDocumentedConstants()
    {
        SessionAnalytics.DefaultOpeningRangeMinutes.Should().Be(30);
        SessionAnalytics.MinOpeningRangeMinutes.Should().Be(1);
        SessionAnalytics.MaxOpeningRangeMinutes.Should().Be(1440);
    }

    #endregion

    #region The Honest Empty State

    /// <summary>An empty series computes the empty state with no values at all.</summary>
    [Fact]
    public void Compute_AnEmptySeries_IsTheEmptyState()
    {
        var analytics = SessionAnalytics.Compute([], 30);

        analytics.State.Should().Be(SessionAnalyticsState.EmptySeries);
        analytics.DailyOpens.Should().BeEmpty();
        analytics.WeeklyOpens.Should().BeEmpty();
        analytics.RangeHigh.Should().BeEmpty();
        analytics.RangeLow.Should().BeEmpty();
        analytics.Slots.Should().BeEmpty();
    }

    #endregion

    #region Slice

    /// <summary>
    /// The chart slices the analytics with the SAME [start, end) window the
    /// bars slice uses: the arrays shrink together, so the drawn families
    /// stay index-aligned with the visible candles.
    /// </summary>
    [Fact]
    public void Slice_TheVisibleWindow_ShrinksTheArraysWithTheBars()
    {
        var bars = new[]
        {
            FlatBar(WeekStart.AddHours(22), 10m),
            FlatBar(WeekStart.AddHours(23), 14m),
            FlatBar(WeekStart.AddDays(1), 20m),
            FlatBar(WeekStart.AddDays(1).AddHours(1), 12m),
        };

        var analytics = SessionAnalytics.Compute(bars, 30).Slice(1, 4);

        analytics.DailyOpens.Should().Equal([10m, 20m, 20m]);
        analytics.WeeklyOpens.Should().Equal([10m, 10m, 10m]);
        analytics.Slots.Should().HaveCount(3);
        analytics.RangeHigh.Should().HaveCount(3);
        analytics.RangeLow.Should().HaveCount(3);
        analytics.State.Should().Be(SessionAnalyticsState.Computed);
    }

    /// <summary>A window out of the value range rejects instead of clamping.</summary>
    [Theory]
    [InlineData(-1, 2)]
    [InlineData(2, 1)]
    [InlineData(0, 17)]
    public void Slice_AWindowOutsideTheRange_Throws(int start, int end)
    {
        var bars = new[]
        {
            FlatBar(WeekStart, 10m),
            FlatBar(WeekStart.AddHours(1), 14m),
        };

        var analytics = SessionAnalytics.Compute(bars, 30);

        var act = () => analytics.Slice(start, end);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    #endregion
}
