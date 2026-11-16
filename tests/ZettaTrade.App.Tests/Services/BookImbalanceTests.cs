namespace ZettaTrade.App.Tests.Services;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using FluentAssertions;
using Xunit;
using ZettaTrade.App.Services;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// The book imbalance goldens (T-246): the ratio weighs the window's first
/// levels of each side and normalizes to [−1, +1] (a one-sided book reads
/// the full lean, an empty book is balanced, never a fabricated lean); the
/// window truncates at the served depth and a window below one level is the
/// honest neutral; the optional smoothing is the per-update EMA with the
/// fixed alpha (the first read passes raw); and the hot path pins —
/// allocation-free (the 60 fps / 1000-level target's own clause) and inside
/// the frame budget with the indicator live.
/// </summary>
public sealed class BookImbalanceTests
{
    private static OrderBook Book(IReadOnlyList<PriceLevel> bids, IReadOnlyList<PriceLevel> asks) =>
        new()
        {
            Symbol = Symbol.Parse("BTC/USDT"),
            Bids = bids,
            Asks = asks,
            Timestamp = DateTimeOffset.UtcNow,
        };

    private static IReadOnlyList<PriceLevel> Levels(params decimal[] quantities)
    {
        var levels = new PriceLevel[quantities.Length];
        for (var i = 0; i < quantities.Length; i++)
        {
            levels[i] = new PriceLevel(100m - i, quantities[i]);
        }

        return levels;
    }

    /// <summary>A 1000-level synthetic book (the perf pins' shape): unit quantity, descending prices.</summary>
    private static OrderBook ThousandLevelBook()
    {
        var bids = new PriceLevel[1000];
        var asks = new PriceLevel[1000];
        for (var i = 0; i < 1000; i++)
        {
            bids[i] = new PriceLevel(100m - i, 1m);
            asks[i] = new PriceLevel(100.01m + i, 1m);
        }

        return Book(bids, asks);
    }

    [Fact]
    public void TheRatio_SumsEachSide_AndNormalizes()
    {
        // bids 2+3 = 5, asks 1+2 = 3 → (5-3)/(5+3) = +0.25.
        var book = Book(Levels(2m, 3m), Levels(1m, 2m));

        BookImbalance.Ratio(book, 2).Should().Be(0.25m);
    }

    [Fact]
    public void TheWindow_TruncatesAtTheServedDepth()
    {
        // Five served levels, window 2: only the first two of each side weigh.
        var book = Book(Levels(2m, 3m, 9m, 9m, 9m), Levels(1m, 2m, 9m, 9m, 9m));

        BookImbalance.Ratio(book, 2).Should().Be(0.25m, "the deeper levels stay out of the window");
    }

    [Fact]
    public void AWindowPastTheServedDepth_SumsEverythingTheBookCarries()
    {
        // The feed caps the ladder at three levels; the wider window never
        // demands what the snapshot does not carry.
        var book = Book(Levels(1m, 1m, 1m), Levels(2m, 2m, 5m));

        BookImbalance.Ratio(book, 10).Should().Be(-0.5m);
    }

    [Fact]
    public void AWindowBelowOneLevel_ReadsNeutral()
    {
        var book = Book(Levels(5m, 5m), Levels(1m, 1m));

        BookImbalance.Ratio(book, 0).Should().Be(0m, "zero names no window — the honest off");
        BookImbalance.Ratio(book, -7).Should().Be(0m);
    }

    [Fact]
    public void AnEmptyBook_ReadsNeutral_NeverAFabricatedLean()
    {
        var book = Book([], []);

        BookImbalance.Ratio(book, 5).Should().Be(0m);
    }

    [Fact]
    public void OneSidedBooks_ReadTheFullLean()
    {
        BookImbalance.Ratio(Book(Levels(4m, 2m), []), 5).Should().Be(1m, "the whole window rides the bid side");
        BookImbalance.Ratio(Book([], Levels(4m, 2m)), 5).Should().Be(-1m, "the whole window rides the ask side");
    }

    [Fact]
    public void TheSmoothing_FirstReadPassesRaw_ThenBlendsWithTheFixedAlpha()
    {
        var first = BookImbalance.Smoothed(null, 0.4m, enabled: true);
        first.Should().Be(0.4m, "the stream's first read has nothing to blend from");

        var next = BookImbalance.Smoothed(first, 0.8m, enabled: true);
        next.Should().Be(0.4m + BookImbalance.SmoothingAlpha * (0.8m - 0.4m), "prev + alpha * (raw - prev)");
        next.Should().Be(0.5m);
    }

    [Fact]
    public void TheSmoothingOff_PassesTheRawReadThrough()
    {
        BookImbalance.Smoothed(0.4m, 0.8m, enabled: false).Should().Be(0.8m);
    }

    /// <summary>
    /// The acceptance's own clause (T-246): the hot path allocates nothing —
    /// 600 live frames (10 seconds of the 60 fps book) over the 1000-level
    /// synthetic book must not move the current thread's allocated-bytes
    /// counter at all.
    /// </summary>
    [Fact]
    public void TheHotPath_IsAllocationFree_OverThe1000LevelBook()
    {
        var book = ThousandLevelBook();

        for (var warmUp = 0; warmUp < 10; warmUp++)
        {
            BookImbalance.Ratio(book, BookImbalance.DefaultWindow);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var frame = 0; frame < 600; frame++)
        {
            BookImbalance.Ratio(book, BookImbalance.DefaultWindow);
        }
        var after = GC.GetAllocatedBytesForCurrentThread();

        (after - before).Should().Be(0, "the live indicator adds no per-frame allocation to the book's hot path");
    }

    /// <summary>
    /// The perf smoke over the 1000-level synthetic book (T-246's tests
    /// clause): 600 live frames — ten seconds of the 60 fps book — sit
    /// comfortably inside the frame budget with the indicator live.
    /// </summary>
    [Fact]
    public void TheHotPath_KeepsTheFrameBudget_WithTheIndicatorLive()
    {
        var book = ThousandLevelBook();

        for (var warmUp = 0; warmUp < 10; warmUp++)
        {
            BookImbalance.Ratio(book, BookImbalance.DefaultWindow);
        }

        var watch = Stopwatch.StartNew();
        for (var frame = 0; frame < 600; frame++)
        {
            BookImbalance.Ratio(book, BookImbalance.DefaultWindow);
        }
        watch.Stop();

        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1), "600 live window reads (the 60 fps × 10 s story) must be far under a second");
    }
}
