namespace ZettaTrade.Infrastructure.Tests.Configuration;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Core.Interfaces;
using FluentAssertions;
using Infrastructure.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// The T-100 batch primitives on <see cref="SettingsService"/> over a real
/// store in a per-test temp directory:
/// <list type="bullet">
/// <item><see cref="SettingsService.SnapshotAsync"/> returns every stored
/// entry as a detached clone the caller may keep across later writes.</item>
/// <item><see cref="SettingsService.ApplyBatchAsync"/> applies one locked
/// mutation batch: every real difference publishes exactly one change (the
/// T-053 canon — no-op numbers publish nothing, a removal reports a null
/// new value) and the file is written once.</item>
/// </list>
/// </summary>
public sealed class SettingsServiceBatchTests
{
    /// <summary>
    /// A batch of an upsert, a removal and a no-op publishes exactly one
    /// change per real difference: the already-equal value writes nothing
    /// (the no-op writer canon), the removal reports a null new payload.
    /// </summary>
    [Fact]
    public async Task ABatch_PublishesExactlyOneChangePerRealDifference()
    {
        var directory = NewBatchDirectory();
        try
        {
            var path = Path.Combine(directory, "settings.json");
            WriteFile(path, """{ "chart": { "symbol": "BTC/USDT", "height": 600, "grade": "A" } }""");

            using var service = new SettingsService(path, NullLogger<SettingsService>.Instance);
            await service.InitializeAsync();

            var liveChanges = new List<SettingsChange>();
            using var subscription = service.Changes.Subscribe(liveChanges.Add);

            var changes = await service.ApplyBatchAsync(
            [
                new SettingsMutation("chart", "symbol", Json("ETH/USDT")), // a real change.
                new SettingsMutation("chart", "height", Json(600)), // already stored — publishes nothing.
                new SettingsMutation("chart", "width", Json(900)), // an added entry.
                new SettingsMutation("chart", "grade", null), // a removal of a stored entry.
            ]);

            changes
                .Select(change => (change.Section, change.Key))
                .Should()
                .BeEquivalentTo(
                [
                    ("chart", "symbol"),
                    ("chart", "width"),
                    ("chart", "grade"),
                ]);
            changes.Where(change => change.Key == "grade").Single()
                .NewValue.Should().BeNull("the removal reports the absence as the new payload");

            liveChanges.Should().BeEquivalentTo(changes, "the batch publishes its own result at the end");

            (await service.GetAsync<string>("chart", "symbol")).Should().Be("ETH/USDT");
            (await service.GetAsync<string>("chart", "grade")).Should().BeNull();
            (await service.GetAsync<int>("chart", "width", 0)).Should().Be(900);
            (await service.GetAsync<int>("chart", "height", 999))
                .Should().Be(600, "the no-op mutation keeps the stored value");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// A snapshot survives later writes intact: the entries are detached
    /// clones, so an importing store can hold on to its export while the
    /// world keeps turning underneath it.
    /// </summary>
    [Fact]
    public async Task TheSnapshot_SurvivesLaterWritesIntact()
    {
        var directory = NewBatchDirectory();
        try
        {
            var path = Path.Combine(directory, "settings.json");
            WriteFile(path, """{ "chart": { "symbol": "BTC/USDT" } }""");

            using var service = new SettingsService(path, NullLogger<SettingsService>.Instance);
            await service.InitializeAsync();

            var snapshot = await service.SnapshotAsync();
            snapshot.Single(entry => entry.Key == "symbol").Value.GetRawText()
                .Should().Be("\"BTC/USDT\"");

            await service.ApplyBatchAsync(
                [new SettingsMutation("chart", "symbol", Json("ETH/USDT"))]);

            snapshot.Single(entry => entry.Key == "symbol").Value.GetRawText()
                .Should().Be("\"BTC/USDT\"", "a snapshot entry is a detached clone");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>An empty batch is legal: the locked unit saves once, publishes nothing.</summary>
    [Fact]
    public async Task AnEmptyBatch_IsANoOpThatStillSaves()
    {
        var directory = NewBatchDirectory();
        try
        {
            var path = Path.Combine(directory, "settings.json");
            WriteFile(path, """{ "chart": { "symbol": "BTC/USDT" } }""");

            using var service = new SettingsService(path, NullLogger<SettingsService>.Instance);
            await service.InitializeAsync();

            var changes = await service.ApplyBatchAsync([]);

            changes.Should().BeEmpty("a no-op batch publishes nothing");
            (await service.GetAsync<string>("chart", "symbol")).Should().Be("BTC/USDT");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    #region Factories

    /// <summary>The temp directory one store (and its saved file) lives in.</summary>
    private static string NewBatchDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "t100-batch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>A stored-shape JSON value the mutations carry.</summary>
    private static JsonElement Json<T>(T value) where T : notnull =>
        JsonSerializer.SerializeToElement(value, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });

    /// <summary>Write the store's starting file (the SettingsServiceTests harness posture).</summary>
    private static void WriteFile(string path, string json) =>
        File.WriteAllText(path, json);

    #endregion Factories
}
