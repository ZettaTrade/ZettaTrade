namespace ZettaTrade.Infrastructure.Tests.Persistence;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using FluentAssertions;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.Infrastructure.Tests.Exchange;

/// <summary>
/// Tests for the SQLite candle cache (T-085): OHLCV round-trip fidelity,
/// the upsert-by-open-time write-through (idempotency across restarts,
/// range-overlap conflation, the check-then-act race pinned via two store
/// instances on the same file), the per-series eviction cap, series
/// isolation by (adapter, symbol, interval), the reactive Caching
/// settings policy, and the T-086 older-than probe (the strictly-older
/// bounded window the deep-history repair chain reads).
/// </summary>
public sealed class SqliteCandleCacheStoreTests
{
    #region Test Constants

    /// <summary>The poll budget for the settings reload fact.</summary>
    private static readonly TimeSpan EventWait = TimeSpan.FromSeconds(5);

    private static readonly DateTimeOffset BaseTime = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    #endregion Test Constants

    #region Helpers

    private static async Task WithTempDatabaseAsync(
        Func<string, Task> testBody)
    {
        var file = Path.Combine(
            Path.GetTempPath(),
            $"zettatrade-candles-{Guid.NewGuid():N}.db");
        try
        {
            await testBody(file);
        }
        finally
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }

    private static SqliteCandleCacheStore CreateStore(string databaseFile, ISettingsService settings) => new(
        new DbContextOptionsBuilder<CandleCacheDbContext>()
            // Pooling=false: after each context disposes, its SQLite
            // connection closes, so the temp database file is unlocked.
            .UseSqlite($"Data Source={databaseFile};Pooling=false")
            .Options,
        settings,
        NullLogger<SqliteCandleCacheStore>.Instance);

    /// <summary>
    /// A settings substitute polluting nothing: the Get answers echo the
    /// store's own defaults (the shipped policy), an empty change stream.
    /// The cap fact narrows the int read to its own key afterwards.
    /// </summary>
    private static ISettingsService CreateSettings()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetAsync<bool>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns(callInfo => callInfo.ArgAt<bool>(2));
        settings.GetAsync<int>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>())
            .Returns(callInfo => callInfo.ArgAt<int>(2));
        settings.Changes.Returns(System.Reactive.Linq.Observable.Empty<SettingsChange>());
        return settings;
    }

    /// <summary>One synthetic bar: minute n of the base day, values derived
    /// from n so assertions verify by content, never by timing.</summary>
    private static Bar Bar(int minute) => new(
        BaseTime.AddMinutes(minute),
        minute + 1m,
        minute + 2.25m,
        minute + 0.125m,
        minute + 1.75m,
        minute + 9.5m);

    private static IReadOnlyList<Bar> Bars(int startMinute, int count) =>
        Enumerable.Range(startMinute, count).Select(Bar).ToArray();

    #endregion Helpers

    #region Happy Path

    [Fact]
    public async Task Initialize_ThenGetLatestOnEmptyCache_ReturnsEmpty()
    {
        await WithTempDatabaseAsync(async file =>
        {
            var store = CreateStore(file, CreateSettings());
            await store.InitializeAsync();

            (await store.GetLatestAsync("paper", Symbol.Parse("BTC/USDT"), TimeFrame.M1, 100))
                .Should().BeEmpty();
        });
    }

    [Fact]
    public async Task WriteThrough_RoundTripsTheOhlcvByOpenTime()
    {
        await WithTempDatabaseAsync(async file =>
        {
            var store = CreateStore(file, CreateSettings());
            await store.InitializeAsync();

            await store.WriteThroughAsync("paper", Symbol.Parse("BTC/USDT"), TimeFrame.M1, Bars(0, 3));

            var bars = await store.GetLatestAsync("paper", Symbol.Parse("BTC/USDT"), TimeFrame.M1, 100);
            bars.Should().HaveCount(3, "every served bar persists");
            bars[0].Time.Should().Be(BaseTime);
            bars.Should().BeInAscendingOrder(bar => bar.Time, "the cache serves the bootstrap window oldest-first");
            bars[0].Should().Be(Bar(0), "the full OHLCV record round-trips");
            bars[2].Should().Be(Bar(2));
        });
    }

    [Fact]
    public async Task GetLatest_LimitsToTheRequestedCount_WithTheNewestBars()
    {
        await WithTempDatabaseAsync(async file =>
        {
            var store = CreateStore(file, CreateSettings());
            await store.InitializeAsync();
            await store.WriteThroughAsync("paper", Symbol.Parse("BTC/USDT"), TimeFrame.M1, Bars(0, 5));

            var bars = await store.GetLatestAsync("paper", Symbol.Parse("BTC/USDT"), TimeFrame.M1, 2);

            bars.Should().HaveCount(2);
            bars[0].Time.Should().Be(BaseTime.AddMinutes(3), "the newest bars answer a bootstrap request");
            bars[1].Time.Should().Be(BaseTime.AddMinutes(4));
        });
    }

    #endregion Happy Path

    #region Upsert Conflation

    [Fact]
    public async Task WriteThrough_IsIdempotentAcrossRestarts()
    {
        await WithTempDatabaseAsync(async file =>
        {
            var first = CreateStore(file, CreateSettings());
            await first.InitializeAsync();
            await first.WriteThroughAsync("paper", Symbol.Parse("BTC/USDT"), TimeFrame.M1, Bars(0, 3));

            // A fresh store instance on the SAME file rewrites the same
            // open times with updated values: the restart replay.
            var second = CreateStore(file, CreateSettings());
            await second.InitializeAsync();
            var rewritten = Bars(0, 3).Select(bar => bar with
            {
                Close = 77.77m,
                Volume = 123.5m,
            }).ToArray();
            await second.WriteThroughAsync("paper", Symbol.Parse("BTC/USDT"), TimeFrame.M1, rewritten);

            var bars = await second.GetLatestAsync("paper", Symbol.Parse("BTC/USDT"), TimeFrame.M1, 100);
            bars.Should().HaveCount(3, "the rewrite upserts by open time instead of duplicating rows");
            bars[1].Close.Should().Be(77.77m, "the latest write's value wins the upsert");
            bars[1].Volume.Should().Be(123.5m);
            bars[0].High.Should().Be(Bar(0).High, "untouched fields keep the first write's values");
            bars.Should().BeInAscendingOrder(bar => bar.Time);
        });
    }

    [Fact]
    public async Task WriteThrough_ConflatesOverlappingRangesWithoutDuplicates()
    {
        await WithTempDatabaseAsync(async file =>
        {
            var store = CreateStore(file, CreateSettings());
            await store.InitializeAsync();
            await store.WriteThroughAsync("paper", Symbol.Parse("BTC/USDT"), TimeFrame.M1, Bars(0, 10));

            var overlap = Bars(7, 6).Select(bar => bar with
            {
                Close = 42m,
            }).ToArray();
            await store.WriteThroughAsync("paper", Symbol.Parse("BTC/USDT"), TimeFrame.M1, overlap);

            var bars = await store.GetLatestAsync("paper", Symbol.Parse("BTC/USDT"), TimeFrame.M1, 100);
            bars.Should().HaveCount(
                13,
                "bars 7..9 conflate INTO the stored rows, bars 10..12 are new - no duplicate open times");
            bars.Should().BeInAscendingOrder(bar => bar.Time);
            bars[6].Close.Should().Be(Bar(6).Close, "the untouched range keeps the first write");
            bars[7].Close.Should().Be(42m, "the overlapped range carries the latest write");
            bars[12].Time.Should().Be(BaseTime.AddMinutes(12), "the fresh tail extends the series");
        });
    }

    [Fact]
    public async Task WriteThrough_ConcurrentIdenticalWritesFromTwoStores_MergeIntoOneBar()
    {
        // The check-then-act race pinned (the BUG-043 lesson): two store
        // instances on the SAME file write the same bar concurrently - ONE
        // statement decides insert-or-update on the database side, so both
        // writers succeed and the row merges once.
        await WithTempDatabaseAsync(async file =>
        {
            var first = CreateStore(file, CreateSettings());
            var second = CreateStore(file, CreateSettings());
            await first.InitializeAsync();
            await second.InitializeAsync();

            var writes = new[]
            {
                Task.Run(() => first.WriteThroughAsync(
                    "paper",
                    Symbol.Parse("BTC/USDT"),
                    TimeFrame.M1,
                    [Bar(0)])),
                Task.Run(() => second.WriteThroughAsync(
                    "paper",
                    Symbol.Parse("BTC/USDT"),
                    TimeFrame.M1,
                    [Bar(0)])),
            };
            await Task.WhenAll(writes);

            var judge = CreateStore(file, CreateSettings());
            await judge.InitializeAsync();
            var bars = await judge.GetLatestAsync("paper", Symbol.Parse("BTC/USDT"), TimeFrame.M1, 100);

            bars.Should().ContainSingle("both writers raced, the composite key merges the row once");
            bars[0].Should().Be(Bar(0));
        });
    }

    #endregion Upsert Conflation

    #region Eviction

    [Fact]
    public async Task WriteThrough_ExceedingTheCap_KeepsOnlyTheNewestBarsOfItsOwnSeries()
    {
        await WithTempDatabaseAsync(async file =>
        {
            var settings = CreateSettings();
            settings
                .GetAsync<int>(Arg.Is(SqliteCandleCacheStore.SettingsSection), Arg.Is(SqliteCandleCacheStore.MaxBarsPerSeriesSettingsKey), Arg.Any<int>())
                .Returns(5);
            var store = CreateStore(file, settings);
            await store.InitializeAsync();

            // Two write-throughs overflow the cap together.
            await store.WriteThroughAsync("paper", Symbol.Parse("BTC/USDT"), TimeFrame.M1, Bars(0, 6));
            await store.WriteThroughAsync("paper", Symbol.Parse("BTC/USDT"), TimeFrame.M1, Bars(6, 6));

            var bars = await store.GetLatestAsync("paper", Symbol.Parse("BTC/USDT"), TimeFrame.M1, 1000);
            bars.Should().HaveCount(5, "the eviction keeps the cap's newest window");
            bars[0].Time.Should().Be(BaseTime.AddMinutes(7), "the evicted head is the aged one");
            bars.Last().Time.Should().Be(BaseTime.AddMinutes(11));
            bars.Should().BeInAscendingOrder(bar => bar.Time);
        });
    }

    [Fact]
    public async Task Eviction_BudgetsEachSeriesSeparately()
    {
        await WithTempDatabaseAsync(async file =>
        {
            var settings = CreateSettings();
            settings
                .GetAsync<int>(Arg.Is(SqliteCandleCacheStore.SettingsSection), Arg.Is(SqliteCandleCacheStore.MaxBarsPerSeriesSettingsKey), Arg.Any<int>())
                .Returns(5);
            var store = CreateStore(file, settings);
            await store.InitializeAsync();

            await store.WriteThroughAsync("paper", Symbol.Parse("BTC/USDT"), TimeFrame.M1, Bars(0, 6));
            await store.WriteThroughAsync("paper", Symbol.Parse("ETH/USDT"), TimeFrame.M1, Bars(0, 3));

            var btc = await store.GetLatestAsync("paper", Symbol.Parse("BTC/USDT"), TimeFrame.M1, 1000);
            var eth = await store.GetLatestAsync("paper", Symbol.Parse("ETH/USDT"), TimeFrame.M1, 1000);
            btc.Should().HaveCount(5, "the overflowing series trims to the cap");
            eth.Should().HaveCount(3, "the small series keeps every bar the eviction had no claim on");
        });
    }

    #endregion Eviction

    #region Series Isolation

    [Fact]
    public async Task Series_StayIsolatedByAdapterSymbolAndInterval()
    {
        await WithTempDatabaseAsync(async file =>
        {
            var store = CreateStore(file, CreateSettings());
            await store.InitializeAsync();

            await store.WriteThroughAsync("paper", Symbol.Parse("BTC/USDT"), TimeFrame.M1, Bars(0, 2));
            await store.WriteThroughAsync("fake", Symbol.Parse("BTC/USDT"), TimeFrame.M1, Bars(10, 2));
            await store.WriteThroughAsync("paper", Symbol.Parse("ETH/USDT"), TimeFrame.M1, Bars(20, 2));
            await store.WriteThroughAsync("paper", Symbol.Parse("BTC/USDT"), TimeFrame.M5, Bars(30, 2));

            var btc = await store.GetLatestAsync("paper", Symbol.Parse("BTC/USDT"), TimeFrame.M1, 1000);
            btc.Should().HaveCount(2);
            btc[0].Time.Should().Be(BaseTime);
            btc[1].Time.Should().Be(BaseTime.AddMinutes(1));
        });
    }

    #endregion Series Isolation

    #region Settings Policy

    [Fact]
    public async Task DisabledCache_PersistsNothingAndServesNothing()
    {
        await WithTempDatabaseAsync(async file =>
        {
            var settings = CreateSettings();
            settings
                .GetAsync<bool>(Arg.Is(SqliteCandleCacheStore.SettingsSection), Arg.Is(SqliteCandleCacheStore.EnabledSettingsKey), Arg.Any<bool>())
                .Returns(false);
            var disabled = CreateStore(file, settings);
            await disabled.InitializeAsync();

            await disabled.WriteThroughAsync("paper", Symbol.Parse("BTC/USDT"), TimeFrame.M1, Bars(0, 3));

            (await disabled.GetLatestAsync("paper", Symbol.Parse("BTC/USDT"), TimeFrame.M1, 1000))
                .Should().BeEmpty("the disabled cache serves nothing");

            // A second, ENABLED store on the same file reads the truth: the
            // disabled cache really persisted nothing.
            var enabled = CreateStore(file, CreateSettings());
            await enabled.InitializeAsync();
            (await enabled.GetLatestAsync("paper", Symbol.Parse("BTC/USDT"), TimeFrame.M1, 1000))
                .Should().BeEmpty("the disabled store's write-through is a no-op");
        });
    }

    [Fact]
    public async Task InitializeAsync_ACachingSectionChange_ReReadsAndSwapsThePolicy()
    {
        await WithTempDatabaseAsync(async file =>
        {
            var settings = Substitute.For<ISettingsService>();
            var changes = new Subject<SettingsChange>();
            settings.Changes.Returns(changes);
            settings.GetAsync<bool>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>())
                .Returns(callInfo => callInfo.ArgAt<bool>(2));
            settings.GetAsync<int>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>())
                .Returns(callInfo => callInfo.ArgAt<int>(2));
            settings
                .GetAsync<int>(Arg.Is(SqliteCandleCacheStore.SettingsSection), Arg.Is(SqliteCandleCacheStore.MaxBarsPerSeriesSettingsKey), Arg.Any<int>())
                .Returns(3);

            var store = CreateStore(file, settings);
            await store.InitializeAsync();
            await store.WriteThroughAsync("paper", Symbol.Parse("BTC/USDT"), TimeFrame.M1, Bars(0, 5));
            (await store.GetLatestAsync("paper", Symbol.Parse("BTC/USDT"), TimeFrame.M1, 1000))
                .Should().HaveCount(3, "the shipped snapshot is not read - the configured cap holds");

            // The settings change re-reads the section: the new cap swaps
            // into the snapshot with no restart, then applies to the next
            // write-through.
            settings
                .GetAsync<int>(Arg.Is(SqliteCandleCacheStore.SettingsSection), Arg.Is(SqliteCandleCacheStore.MaxBarsPerSeriesSettingsKey), Arg.Any<int>())
                .Returns(6);
            changes.OnNext(new SettingsChange(
                SqliteCandleCacheStore.SettingsSection,
                SqliteCandleCacheStore.MaxBarsPerSeriesSettingsKey,
                null,
                6));

            await TestPoll.PollUntilAsync(
                () => store.MaxBarsPerSeries == 6,
                EventWait);

            await store.WriteThroughAsync("paper", Symbol.Parse("BTC/USDT"), TimeFrame.M1, Bars(5, 6));
            var bars = await store.GetLatestAsync("paper", Symbol.Parse("BTC/USDT"), TimeFrame.M1, 1000);
            bars.Should().HaveCount(
                6,
                "bars 0..2 already left with the first cap; the raised cap keeps the 5..10 that follow the reload");
            bars[0].Time.Should().Be(BaseTime.AddMinutes(5), "the evicted head under the old cap stays gone");
        });
    }

    #endregion Settings Policy

    #region Older-than Probe (T-086)

    [Fact]
    public async Task GetOlderThan_ServesOnlyTheStrictlyOlderWindow_OldestFirst()
    {
        await WithTempDatabaseAsync(async file =>
        {
            var store = CreateStore(file, CreateSettings());
            await store.InitializeAsync();
            await store.WriteThroughAsync("paper", Symbol.Parse("BTC/USDT"), TimeFrame.M1, Bars(0, 5));

            var older = await store.GetOlderThanAsync(
                "paper",
                Symbol.Parse("BTC/USDT"),
                TimeFrame.M1,
                BaseTime.AddMinutes(3),
                100);

            // Minutes 0..2 sit strictly below the boundary; the boundary's
            // OWN open (minute 3) stays out — the probe serves the coverage
            // behind the repair's window exactly, oldest-first like every
            // window the cache returns.
            older.Should().BeEquivalentTo(Bars(0, 3));
            older.Should().BeInAscendingOrder(bar => bar.Time);
        });
    }

    [Fact]
    public async Task GetOlderThan_ClampsTheLimitAndIsolatesTheSeries()
    {
        await WithTempDatabaseAsync(async file =>
        {
            var store = CreateStore(file, CreateSettings());
            await store.InitializeAsync();
            await store.WriteThroughAsync("paper", Symbol.Parse("BTC/USDT"), TimeFrame.M1, Bars(0, 8));

            var newestOlder = await store.GetOlderThanAsync(
                "paper",
                Symbol.Parse("BTC/USDT"),
                TimeFrame.M1,
                BaseTime.AddMinutes(10),
                3);

            // The newest strictly-older tail the cap allows, oldest-first:
            // the probe is a bounded window, not the whole cached past.
            newestOlder.Select(bar => bar.Time).Should().Equal(
                BaseTime.AddMinutes(5),
                BaseTime.AddMinutes(6),
                BaseTime.AddMinutes(7));

            (await store.GetOlderThanAsync(
                "other",
                Symbol.Parse("BTC/USDT"),
                TimeFrame.M1,
                BaseTime.AddMinutes(10),
                100))
                .Should().BeEmpty("another adapter's series key isolates the coverage");
        });
    }

    #endregion Older-than Probe (T-086)
}
