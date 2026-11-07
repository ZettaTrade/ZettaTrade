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

    #region Repair Union

    /// <summary>
    /// The set-union merge the repair lane rides (T-086): every displayed
    /// bar survives, an equal open keeps the displayed side, the additions
    /// only ever ADD what is absent — no depth change, no duplicate.
    /// </summary>
    [Fact]
    public void UnionAscending_MergesSetsKeepingEveryDisplayedBar()
    {
        var existing = new[] { At(20), At(30) };
        var incoming = new[] { At(10), At(20), At(40) };

        var merged = BarSeriesMerge.UnionAscending(existing, incoming);

        merged.Should().HaveCount(4);
        merged.Should().BeEquivalentTo(
            [At(10), At(20), At(30), At(40)],
            options => options.WithStrictOrdering());
        merged![1].Close.Should().Be(
            existing[1].Close,
            "the equal open keeps the displayed side — the incoming duplicate is skipped");
    }

    [Fact]
    public void UnionAscending_SkipsTheIncomingDuplicateEntirely()
    {
        var existing = new[] { At(40), At(50) };
        var incoming = new[] { At(20), At(40), At(60) };

        var merged = BarSeriesMerge.UnionAscending(existing, incoming);

        merged.Should().HaveCount(4, "one open appears once, whatever side it came from");
        merged![3].Time.Should().Be(Base.AddMinutes(60), "the incoming bar after the display's head is kept");
    }

    [Fact]
    public void UnionAscending_EitherSideEmptyYieldsTheOtherVerbatim()
    {
        var existing = new[] { At(20), At(30) };

        BarSeriesMerge.UnionAscending(existing, []).Should().BeSameAs(existing);
        BarSeriesMerge.UnionAscending([], existing).Should().BeSameAs(existing);
        BarSeriesMerge.UnionAscending([], []).Should().BeEmpty();
    }

    [Fact]
    public void UnionAscending_TheNonAscendingSideTripsTheGuard()
    {
        var existing = new[] { At(20), At(10) };

        BarSeriesMerge.UnionAscending(existing, [At(5)]).Should().BeNull(
            "the displayed series' violation must not be healed into a fabricated order");

        var incomingBroken = new[] { At(5), At(5) };
        BarSeriesMerge.UnionAscending([At(20)], incomingBroken).Should().BeNull(
            "a broken additions page is rejected, never repaired or defaulted");
    }

    [Fact]
    public void UnionAscending_RejectsNullArguments()
    {
        var act = () => BarSeriesMerge.UnionAscending(null!, [At(0)]);

        act.Should().Throw<ArgumentNullException>();
        var actIncoming = () => BarSeriesMerge.UnionAscending([At(0)], null!);

        actIncoming.Should().Throw<ArgumentNullException>();
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
