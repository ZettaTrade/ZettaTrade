namespace ZettaTrade.App.Tests.Services;

using System;
using System.Collections.Generic;
using FluentAssertions;
using Xunit;
using ZettaTrade.App.Services;
using ZettaTrade.Core.Entities;
using ZettaTrade.Infrastructure.Exchange;

/// <summary>
/// The OI pane's aligner goldens (T-241): a bar before the venue's first
/// snapshot reads the honest gap (null), the last snapshot at or before the
/// bar's open governs the bar (the interest the venue had settled), one
/// snapshot governs every bar inside its bucket, and empty inputs answer
/// the honest empties — never an interpolated or zero-filled stretch.
/// </summary>
public sealed class OISeriesAlignmentTests
{
    private static readonly DateTimeOffset Base = new(2026, 3, 2, 12, 0, 0, TimeSpan.Zero);

    private static Bar Bar(DateTimeOffset open) => new(open, 100m, 101m, 99m, 100m, 1000m);

    private static OpenInterestPoint Point(DateTimeOffset time, decimal size) => new(time, size);

    /// <summary>A bar whose open predates the venue's first snapshot reads the honest gap.</summary>
    [Fact]
    public void ABarsBeforeTheFirstSnapshot_ReadTheHonestGap()
    {
        var bars = new[] { Bar(Base), Bar(Base.AddHours(1)) };
        var points = new[] { Point(Base.AddHours(2), 316131537.00000000m) };

        var values = OISeriesAlignment.Align(bars, points);

        values.Should().HaveCount(2);
        values[0].Should().BeNull("no snapshot had governed the first bar yet");
        values[1].Should().BeNull();
    }

    /// <summary>The last snapshot AT or BEFORE the bar's open governs it — the arriving snapshot's own bar already reads it.</summary>
    [Fact]
    public void TheLastSnapshotAtOrBeforeTheOpen_GovernsTheBar()
    {
        var bars = new[] { Bar(Base), Bar(Base.AddHours(1)) };
        var points = new[] { Point(Base, 314851002.00000000m), Point(Base.AddHours(1), 316131537.50000000m) };

        var values = OISeriesAlignment.Align(bars, points);

        values[0].Should().Be(314851002.00000000m, "the snapshot stamped at the bar's open is the governing one");
        values[1].Should().Be(316131537.50000000m, "the next bar's own snapshot took over");
    }

    /// <summary>One snapshot governs every bar inside its bucket (the daily snapshot over the hourly bars).</summary>
    [Fact]
    public void OneSnapshot_GovernsEveryBarInItsBucket()
    {
        var bars = new[]
        {
            Bar(Base),
            Bar(Base.AddMinutes(15)),
            Bar(Base.AddMinutes(30)),
            Bar(Base.AddMinutes(45)),
        };
        var points = new[] { Point(Base, 314851002.00000000m), Point(Base.AddDays(1), 316131537.00000000m) };

        var values = OISeriesAlignment.Align(bars, points);

        values.Should().Equal(
            (IEnumerable<decimal?>)[314851002.00000000m, 314851002.00000000m, 314851002.00000000m, 314851002.00000000m],
            "all four bars rode the first snapshot; the next day's one governs nothing in this window");
    }

    /// <summary>Empty inputs answer the honest empties: no bars is an empty pane, no snapshots is an all-gap pane.</summary>
    [Fact]
    public void TheEmptyInputs_AnswerTheHonestEmpties()
    {
        var bars = new[] { Bar(Base), Bar(Base.AddHours(1)) };

        OISeriesAlignment.Align([], []).Should().BeEmpty();
        OISeriesAlignment.Align(bars, []).Should().Equal(
            (IEnumerable<decimal?>)[null, null],
            "a snapshotless window is an all-gap pane, never a fabricated stretch");
    }
}
