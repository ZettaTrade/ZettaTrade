namespace ZettaTrade.Infrastructure.Tests.Persistence;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ZettaTrade.Charting;
using ZettaTrade.Core.Enums;

/// <summary>
/// Tests for the SQLite chart drawings store (T-091): the whole-layer
/// round-trip (kinds, anchors, identities), the fresh drawings' store-named
/// identities riding the save's answer, the homecoming reload across a new
/// store instance (the persistence acceptance), and the honest drop paths —
/// an unknown kind name and a degenerate anchor set rebuild nothing.
/// </summary>
public sealed class SqliteChartDrawingStoreTests
{
    #region Test Constants

    private static readonly DateTimeOffset BaseTime = new(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);

    private const string DemoAdapter = "demo";

    private const string DemoSymbol = "BTC/USDT";

    #endregion Test Constants

    #region Test Data

    private static ChartDrawing TrendLine(decimal priceStep) => new()
    {
        Kind = ChartDrawingKind.TrendLine,
        Anchors =
        [
            new ChartDrawingAnchor(BaseTime, 100m + priceStep),
            new ChartDrawingAnchor(BaseTime.AddHours(2), 200m + priceStep),
        ],
    };

    private static ChartDrawing Level(decimal price, int hourOffset = 1) => new()
    {
        Kind = ChartDrawingKind.HorizontalLevel,
        Anchors = [new ChartDrawingAnchor(BaseTime.AddHours(hourOffset), price)],
    };

    private static ChartDrawing Fib(decimal priceStep) => new()
    {
        Kind = ChartDrawingKind.FibRetracement,
        Anchors =
        [
            new ChartDrawingAnchor(BaseTime.AddHours(1), 300m),
            new ChartDrawingAnchor(BaseTime.AddHours(3), 300m - priceStep),
        ],
    };

    private static ChartDrawing Zone(decimal priceStep) => new()
    {
        Kind = ChartDrawingKind.Zone,
        Anchors =
        [
            new ChartDrawingAnchor(BaseTime, 100m + priceStep),
            new ChartDrawingAnchor(BaseTime.AddHours(2), 150m + priceStep),
        ],
    };

    private static ChartDrawing Annotation(string? text, int hourOffset = 1) => new()
    {
        Kind = ChartDrawingKind.Text,
        Anchors = [new ChartDrawingAnchor(BaseTime.AddHours(hourOffset), 120m)],
        Text = text,
    };

    private static ChartDrawing Ruler(decimal priceStep) => new()
    {
        Kind = ChartDrawingKind.Measure,
        Anchors =
        [
            new ChartDrawingAnchor(BaseTime.AddHours(4), 200m),
            new ChartDrawingAnchor(BaseTime.AddHours(6), 200m + priceStep),
        ],
    };

    #endregion Test Data

    #region Helpers

    private static async Task WithTempDatabaseAsync(Func<(string Directory, string File), Task> testBody)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"zettatrade-drawings-{Guid.NewGuid():N}");
        var file = Path.Combine(directory, "sub", "drawings.db");
        try
        {
            await testBody((directory, file));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static SqliteChartDrawingStore CreateStore(string databaseFile) => new(
        new DbContextOptionsBuilder<ChartDrawingDbContext>()
            // Pooling=false: after each context disposes, its SQLite
            // connection closes, so the temp database file is unlocked.
            .UseSqlite($"Data Source={databaseFile};Pooling=false")
            .Options,
        NullLogger<SqliteChartDrawingStore>.Instance);

    /// <summary>A raw context over the store's own database, for the drop-path mutations.</summary>
    private static ChartDrawingDbContext OpenDatabase(string databaseFile) => new(
        new DbContextOptionsBuilder<ChartDrawingDbContext>()
            .UseSqlite($"Data Source={databaseFile};Pooling=false")
            .Options);

    #endregion Helpers

    #region Tests

    [Fact]
    public async Task Initialize_CreatesTheMissingDirectory_AndAppliesMigrations()
    {
        await WithTempDatabaseAsync(async paths =>
        {
            Directory.Exists(paths.Directory).Should().BeFalse("the store owns its clean-room setup.");

            var store = CreateStore(paths.File);
            await store.InitializeAsync();

            Directory.Exists(paths.Directory).Should().BeTrue("the store must not assume a prepared data directory.");
            await using var database = OpenDatabase(paths.File);
            (await database.Database.CanConnectAsync()).Should().BeTrue("the schema migrated.");
        });
    }

    [Fact]
    public async Task Calls_BeforeInitializeAsync_SelfHealTheSchema()
    {
        await WithTempDatabaseAsync(async paths =>
        {
            // The cold-start shape the startup walk must not own: a chart
            // page that loads first never waits for an init nobody ran yet.
            var store = CreateStore(paths.File);
            await store.SaveAsync(DemoAdapter, DemoSymbol, TimeFrame.H1, [TrendLine(4m), Level(6m, 2)]);

            var loaded = await store.LoadAsync(DemoAdapter, DemoSymbol, TimeFrame.H1);

            loaded.Select(drawing => drawing.Kind)
                .Should().Equal((IEnumerable<ChartDrawingKind>)[ChartDrawingKind.TrendLine, ChartDrawingKind.HorizontalLevel],
                    "the first chart call carries the one-time migration itself.");
        });
    }

    [Fact]
    public async Task ConcurrentFirstCalls_ShareTheOneTimeMigration()
    {
        await WithTempDatabaseAsync(async paths =>
        {
            var store = CreateStore(paths.File);
            var save = store.SaveAsync(DemoAdapter, DemoSymbol, TimeFrame.H1, [TrendLine(1m)]);
            var sibling = store.LoadAsync("demo2", DemoSymbol, TimeFrame.H1);

            // Both calls race the readiness gate on a never-initialized
            // store; the serialized migration leaves exactly one schema and
            // both callers served.
            var answers = await Task.WhenAll(save, sibling);
            answers[1].Should().BeEmpty("a sibling key started with an empty layer.");

            (await store.LoadAsync(DemoAdapter, DemoSymbol, TimeFrame.H1))
                .Select(drawing => drawing.Kind)
                .Should().Equal((IEnumerable<ChartDrawingKind>)[ChartDrawingKind.TrendLine],
                    "the concurrent winner's rows ride the migrated schema.");
        });
    }

    [Fact]
    public async Task SaveThenLoad_RoundTripsLayersAcrossASecondStore()
    {
        await WithTempDatabaseAsync(async paths =>
        {
            var trendLine = TrendLine(7m);
            var fib = Fib(60m);
            // The level's first anchor sits on its own time slot: the load
            // order rides the first anchor's times, so the fact's expected
            // layer order must never depend on a tie.
            var level = Level(95m, 4);

            var first = CreateStore(paths.File);
            await first.InitializeAsync();
            var saved = await first.SaveAsync(DemoAdapter, DemoSymbol, TimeFrame.H1, [trendLine, fib, level]);

            saved.Select(drawing => drawing.Id).Should().OnlyContain(id => id != Guid.Empty);
            saved.Select(drawing => drawing.Kind)
                .Should().Equal((IEnumerable<ChartDrawingKind>)[ChartDrawingKind.TrendLine, ChartDrawingKind.FibRetracement, ChartDrawingKind.HorizontalLevel],
                    "the answer mirrors the caller's order.");

            // The homecoming: a second instance over the same file reloads
            // the exact layer — kinds, anchors and identities (the
            // persistence acceptance).
            var second = CreateStore(paths.File);
            await second.InitializeAsync();
            var loaded = await second.LoadAsync(DemoAdapter, DemoSymbol, TimeFrame.H1);

            loaded.Select(drawing => drawing.Id)
                .Should().Equal(saved.Select(drawing => drawing.Id),
                    "the store identity outlives the process.");
            loaded.Select(drawing => drawing.Kind)
                .Should().Equal((IEnumerable<ChartDrawingKind>)[ChartDrawingKind.TrendLine, ChartDrawingKind.FibRetracement, ChartDrawingKind.HorizontalLevel]);
            loaded.Single(drawing => drawing.Kind == ChartDrawingKind.TrendLine).Anchors
                .Should().BeEquivalentTo(trendLine.Anchors);
            loaded.Single(drawing => drawing.Kind == ChartDrawingKind.HorizontalLevel).Anchors
                .Should().BeEquivalentTo(level.Anchors);
            loaded.Single(drawing => drawing.Kind == ChartDrawingKind.FibRetracement).Anchors
                .Should().BeEquivalentTo(fib.Anchors);
        });
    }

    [Fact]
    public async Task Save_ReplacesTheWholeLayer_AndHashesNothingHalfway()
    {
        await WithTempDatabaseAsync(async paths =>
        {
            var store = CreateStore(paths.File);
            await store.InitializeAsync();
            var first = await store.SaveAsync(
                DemoAdapter,
                DemoSymbol,
                TimeFrame.H4,
                [TrendLine(1m), Fib(5m), Level(42m, 4)]);

            var second = await store.SaveAsync(
                DemoAdapter,
                DemoSymbol,
                TimeFrame.H4,
                [first[1], first[2]]);

            second.Should().HaveCount(2, "the removed drawing's rows are gone with the layer.");
            second.Select(drawing => drawing.Id).Should().NotContain(first[0].Id);
            (await store.LoadAsync(DemoAdapter, DemoSymbol, TimeFrame.H4))
                .Select(drawing => drawing.Kind)
                .Should().Equal((IEnumerable<ChartDrawingKind>)[ChartDrawingKind.FibRetracement, ChartDrawingKind.HorizontalLevel],
                    "the load order rides the first anchor's times.");
        });
    }

    [Fact]
    public async Task Save_EmptyLayer_DeletesEveryRow_OfThatKeyOnly()
    {
        await WithTempDatabaseAsync(async paths =>
        {
            var store = CreateStore(paths.File);
            await store.InitializeAsync();
            await store.SaveAsync(DemoAdapter, DemoSymbol, TimeFrame.H1, [TrendLine(0m), Level(10m)]);
            await store.SaveAsync("demo2", DemoSymbol, TimeFrame.H1, [Fib(1m)]);

            await store.SaveAsync(DemoAdapter, DemoSymbol, TimeFrame.H1, []);

            (await store.LoadAsync(DemoAdapter, DemoSymbol, TimeFrame.H1))
                .Should().BeEmpty("the layer was rewritten empty.");
            (await store.LoadAsync("demo2", DemoSymbol, TimeFrame.H1))
                .Should().HaveCount(1, "a sibling key's layer is nobody's business.");
        });
    }

    [Fact]
    public async Task Load_MissingKey_ReturnsEmpty()
    {
        await WithTempDatabaseAsync(async paths =>
        {
            var store = CreateStore(paths.File);
            await store.InitializeAsync();

            (await store.LoadAsync(DemoAdapter, DemoSymbol, TimeFrame.M1))
                .Should().BeEmpty("an unannotated chart's layer is empty, never null.");
        });
    }

    [Fact]
    public async Task Load_UnknownKindRow_DropsThatDrawing()
    {
        await WithTempDatabaseAsync(async paths =>
        {
            var store = CreateStore(paths.File);
            await store.InitializeAsync();
            var saved = await store.SaveAsync(DemoAdapter, DemoSymbol, TimeFrame.H1, [TrendLine(3m), Level(7m)]);

            await using (var database = OpenDatabase(paths.File))
            {
                var row = await database.Drawings
                    .SingleAsync(entry => entry.DrawingId == saved[0].Id && entry.SortOrder == 0);
                row.Kind = "SharpieScribble";
                await database.SaveChangesAsync();
            }

            var loaded = await store.LoadAsync(DemoAdapter, DemoSymbol, TimeFrame.H1);

            loaded.Select(drawing => drawing.Id)
                .Should().Equal((IEnumerable<Guid>)[saved[1].Id],
                    "an unknown kind never rebuilds — the honest rows survive.");
        });
    }

    [Fact]
    public async Task Load_DegenerateAnchors_DropsThatDrawing()
    {
        await WithTempDatabaseAsync(async paths =>
        {
            var store = CreateStore(paths.File);
            await store.InitializeAsync();
            var saved = await store.SaveAsync(DemoAdapter, DemoSymbol, TimeFrame.H1, [TrendLine(2m), Level(9m)]);

            // A corrupted row set: the second anchor's time flips before the
            // first — geometry validation refuses, the drawing drops.
            await using (var database = OpenDatabase(paths.File))
            {
                var rows = await database.Drawings
                    .Where(entry => entry.DrawingId == saved[0].Id)
                    .OrderBy(entry => entry.SortOrder)
                    .ToListAsync();
                rows[1].Time = rows[0].Time.AddHours(-4);
                await database.SaveChangesAsync();
            }

            var loaded = await store.LoadAsync(DemoAdapter, DemoSymbol, TimeFrame.H1);

            loaded.Select(drawing => drawing.Id)
                .Should().Equal((IEnumerable<Guid>)[saved[1].Id],
                    "a flipped segment reads backwards — the validation net drops it.");
        });
    }

    [Fact]
    public async Task SaveThenLoad_RoundTripsTheTextAnnotation()
    {
        await WithTempDatabaseAsync(async paths =>
        {
            var zone = Zone(2m);
            var annotated = Annotation("supply above — wait for retest", hourOffset: 1);
            var unlabelled = Annotation(null, hourOffset: 3);
            var ruler = Ruler(5m);

            var first = CreateStore(paths.File);
            await first.InitializeAsync();
            var saved = await first.SaveAsync(
                DemoAdapter,
                DemoSymbol,
                TimeFrame.H1,
                // Each drawing's first anchor owns a distinct time slot: the
                // load order rides it, the fact's expected order never ties.
                [zone, annotated, unlabelled, ruler]);

            // The homecoming through a fresh store instance: the
            // annotation's string rides the rows (every anchor row carries
            // it), the plain kinds come back textless.
            var second = CreateStore(paths.File);
            await second.InitializeAsync();
            var loaded = await second.LoadAsync(DemoAdapter, DemoSymbol, TimeFrame.H1);

            loaded.Select(drawing => drawing.Id)
                .Should().Equal(saved.Select(drawing => drawing.Id));
            loaded.Single(drawing => drawing.Id == annotated.Id).Text
                .Should().Be("supply above — wait for retest", "the prompt's answer outlives the process.");
            loaded.Single(drawing => drawing.Id == unlabelled.Id).Text
                .Should().BeNull("an unlabelled annotation reads back textless.");
            loaded.Where(drawing => drawing.Kind is ChartDrawingKind.Zone or ChartDrawingKind.Measure)
                .Select(drawing => drawing.Text)
                .Should().HaveCount(2).And.OnlyContain(text => text == null, "only the annotation kind carries a string.");
            loaded.Single(drawing => drawing.Kind == ChartDrawingKind.Zone).Anchors
                .Should().BeEquivalentTo(zone.Anchors);
            loaded.Single(drawing => drawing.Kind == ChartDrawingKind.Measure).Anchors
                .Should().BeEquivalentTo(ruler.Anchors);
        });
    }

    [Fact]
    public async Task Load_MissingAnchorRows_DropsThatDrawing()
    {
        await WithTempDatabaseAsync(async paths =>
        {
            var store = CreateStore(paths.File);
            await store.InitializeAsync();
            var saved = await store.SaveAsync(DemoAdapter, DemoSymbol, TimeFrame.H1, [TrendLine(5m), Fib(11m)]);

            await using (var database = OpenDatabase(paths.File))
            {
                var rows = await database.Drawings
                    .Where(entry => entry.DrawingId == saved[0].Id)
                    .ToListAsync();
                database.Drawings.RemoveRange(rows);
                await database.SaveChangesAsync();
            }

            var loaded = await store.LoadAsync(DemoAdapter, DemoSymbol, TimeFrame.H1);

            loaded.Select(drawing => drawing.Kind)
                .Should().Equal((IEnumerable<ChartDrawingKind>)[ChartDrawingKind.FibRetracement],
                    "a drawing that lost its rows to corruption has nothing to rebuild from.");
        });
    }

    #endregion Tests
}
