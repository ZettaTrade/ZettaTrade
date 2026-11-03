namespace ZettaTrade.App.Tests.Services;

using System;
using System.Linq;
using FluentAssertions;
using Xunit;
using ZettaTrade.App.Services;
using ZettaTrade.Core.Entities;

/// <summary>
/// Pins for the chart's older-history window merge: the dedupe against the
/// loaded head by open time, the empty result when nothing older arrived,
/// and the null guard on any strictly-ascending violation (the view stays
/// exactly as it stood — never a repaired substitute). Pure facts.
/// </summary>
public sealed class BarSeriesMergeTests
{
    /// <summary>The epoch the facts' open times grow from (minutes).</summary>
    private static readonly DateTimeOffset Base = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>A bar at <paramref name="minute"/> minutes past the base epoch.</summary>
    private static Bar At(int minute) => new(
        Base.AddMinutes(minute),
        100m,
        102m,
        99m,
        101m,
        7);

    #region Dedupe And Prepend

    [Fact]
    public void PrependOlder_DropsTheOverlappingHeadBar()
    {
        var existing = new[] { At(20), At(30), At(40) };
        var fetched = new[] { At(0), At(10), At(20) };

        var prepend = BarSeriesMerge.PrependOlder(existing, fetched);

        prepend.Should().BeEquivalentTo([At(0), At(10)], options => options.WithStrictOrdering());
    }

    [Fact]
    public void PrependOlder_AllOverlapsYieldTheEmptyResult()
    {
        var existing = new[] { At(20), At(30) };
        var fetched = new[] { At(20) };

        BarSeriesMerge.PrependOlder(existing, fetched).Should().BeEmpty(
            "the boundary bar both sides requested contributes nothing to the merge");
    }

    [Fact]
    public void PrependOlder_KeepsTheFetchedPageAscending()
    {
        var existing = new[] { At(60) };
        var fetched = new[] { At(0), At(20), At(40) };

        var prepend = BarSeriesMerge.PrependOlder(existing, fetched);

        prepend.Should().BeEquivalentTo(fetched);
        prepend.Should().BeInAscendingOrder(bar => bar.Time);
    }

    #endregion

    #region Exhaustion Guard

    [Fact]
    public void PrependOlder_AnEmptyFetchedPageIsNothingOlder()
    {
        var existing = new[] { At(20) };

        BarSeriesMerge.PrependOlder(existing, []).Should().BeEmpty(
            "an empty page means deeper history is exhausted at this anchor");
    }

    [Fact]
    public void PrependOlder_AnEmptyLoadedSeriesHasNoHeadToDedupeAgainst()
    {
        BarSeriesMerge.PrependOlder([], [At(20)]).Should().BeEmpty(
            "the degenerate series has no head — nothing to prepend against");
    }

    #endregion

    #region Ascending Guard

    [Fact]
    public void PrependOlder_NonAscendingLoadedSeriesTripsTheGuard()
    {
        var existing = new[] { At(30), At(20) };

        BarSeriesMerge.PrependOlder(existing, [At(10)]).Should().BeNull(
            "the loaded series is the T-023 contract — a violation keeps the view untouched");
    }

    [Fact]
    public void PrependOlder_NonAscendingFetchedPageTripsTheGuard()
    {
        var existing = new[] { At(30) };
        var fetched = new[] { At(20), At(20), At(10) };

        BarSeriesMerge.PrependOlder(existing, fetched).Should().BeNull(
            "a broken page is rejected, never repaired or defaulted");
    }

    [Fact]
    public void PrependOlder_EqualAdjacentOpensInBothSidesTripsTheGuard()
    {
        var existing = new[] { At(10), At(20) };
        var fetched = new[] { At(5), At(5) };

        BarSeriesMerge.PrependOlder(existing, fetched).Should().BeNull(
            "same-minute candles are not a valid ascending series");
    }

    #endregion

    [Fact]
    public void PrependOlder_SkipsFetchedBarsNewerThanTheHead()
    {
        var existing = new[] { At(40), At(50) };
        var fetched = new[] { At(0), At(20), At(50) };

        var prepend = BarSeriesMerge.PrependOlder(existing, fetched);

        prepend.Should().BeEquivalentTo([At(0), At(20)], options => options.WithStrictOrdering());
        prepend!.Should().NotContain(bar => bar.Time == Base.AddMinutes(50));
    }

    [Fact]
    public void PrependOlder_RejectsNullArguments()
    {
        var act = () => BarSeriesMerge.PrependOlder(null!, []);

        act.Should().Throw<ArgumentNullException>();
        var actFetched = () => BarSeriesMerge.PrependOlder([At(0)], null!);

        actFetched.Should().Throw<ArgumentNullException>();
    }
}
