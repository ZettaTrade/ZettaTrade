namespace ZettaTrade.App.Tests.Services;

using System;
using System.Collections.Generic;
using FluentAssertions;
using Xunit;
using ZettaTrade.App.Services;
using ZettaTrade.Core.Entities;
using ZettaTrade.Infrastructure.Exchange;

/// <summary>
/// The funding pane's aligner goldens (T-240): a bar before the venue's
/// first funding event reads the honest gap (null), the last event at or
/// before the bar's open governs the bar (the rate the interval rode), one
/// event governs every bar inside its interval, and empty inputs answer
/// the honest empties — never an interpolated or zero-filled stretch.
/// </summary>
public sealed class FundingSeriesAlignmentTests
{
    private static readonly DateTimeOffset Base = new(2026, 3, 2, 12, 0, 0, TimeSpan.Zero);

    private static Bar Bar(DateTimeOffset open) => new(open, 100m, 101m, 99m, 100m, 1000m);

    private static FundingRatePoint Point(DateTimeOffset time, decimal rate) => new(time, rate);

    /// <summary>A bar whose open predates the venue's first event reads the honest gap.</summary>
    [Fact]
    public void ABarsBeforeTheFirstEvent_ReadTheHonestGap()
    {
        var bars = new[] { Bar(Base), Bar(Base.AddHours(1)) };
        var points = new[] { Point(Base.AddHours(2), 0.0001m) };

        var values = FundingSeriesAlignment.Align(bars, points);

        values.Should().HaveCount(2);
        values[0].Should().BeNull("no event had governed the first bar yet");
        values[1].Should().BeNull();
    }

    /// <summary>The last event AT or BEFORE the bar's open governs it — the arriving event's own bar already reads it.</summary>
    [Fact]
    public void TheLastEventAtOrBeforeTheOpen_GovernsTheBar()
    {
        var bars = new[] { Bar(Base), Bar(Base.AddHours(1)) };
        var points = new[] { Point(Base, 0.0001m), Point(Base.AddHours(1), -0.0002m) };

        var values = FundingSeriesAlignment.Align(bars, points);

        values[0].Should().Be(0.0001m, "the event stamped at the bar's open is the governing one");
        values[1].Should().Be(-0.0002m, "the next bar's own event took over");
    }

    /// <summary>One event governs every bar inside its interval (the 8-hour funding event over the 15-minute bars).</summary>
    [Fact]
    public void OneEvent_GovernsEveryBarInItsInterval()
    {
        var bars = new[]
        {
            Bar(Base),
            Bar(Base.AddMinutes(15)),
            Bar(Base.AddMinutes(30)),
            Bar(Base.AddMinutes(45)),
        };
        var points = new[] { Point(Base, 0.0001m), Point(Base.AddHours(8), 0.0002m) };

        var values = FundingSeriesAlignment.Align(bars, points);

        values.Should().Equal(
            (IEnumerable<decimal?>)[0.0001m, 0.0001m, 0.0001m, 0.0001m],
            "all four bars rode the first event; the 8-hour one governs nothing in this window");
    }

    /// <summary>Empty inputs answer the honest empties: no bars is an empty pane, no events is an all-gap pane.</summary>
    [Fact]
    public void TheEmptyInputs_AnswerTheHonestEmpties()
    {
        var bars = new[] { Bar(Base), Bar(Base.AddHours(1)) };

        FundingSeriesAlignment.Align([], []).Should().BeEmpty();
        FundingSeriesAlignment.Align(bars, []).Should().Equal(
            (IEnumerable<decimal?>)[null, null],
            "an eventless window is an all-gap pane, never a fabricated stretch");
    }
}
