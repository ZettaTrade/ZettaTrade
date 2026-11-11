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
using ZettaTrade.Infrastructure.Tests.Exchange;

/// <summary>
/// T-053 settings-service rules over real files in a per-test temp directory:
/// <list type="bullet">
/// <item>BUG-012: every memory mutation runs under the save lock, so a reload
/// racing a set never erases the just-written value (the follow-up save
/// persists it too).</item>
/// <item>BUG-013: an external reload publishes one event per difference.</item>
/// <item>BUG-045: old and new payloads of one change share one type; a save's
/// own file echo is recognized by content and never surfaces as a reload.</item>
/// </list>
/// </summary>
public sealed class SettingsServiceTests
{
    #region Constants

    /// <summary>Bounded wait for an event expected through the watcher pipeline.</summary>
    private static readonly TimeSpan EventWait = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The quiet window a save's own echo needs to (not) surface through the
    /// debounced watcher pipeline: the service's 1 s debounce plus a delivery
    /// tail.
    /// </summary>
    private static readonly TimeSpan ExternalEchoQuietWait =
        TimeSpan.FromMilliseconds(SettingsService.ExternalReloadDebounceMs + EchoDeliveryTailMs);

    /// <summary>Delivery tail on top of the debounce window (the watcher hop).</summary>
    private const int EchoDeliveryTailMs = 500;

    #endregion

    private static string NewSettingsDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "t053-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    #region Tests: lost-write race

    /// <summary>
    /// BUG-012: a debounced external reload interleaved with a set must never
    /// erase the just-written value from memory, and the next save persists it
    /// (the pre-fix unlock Clear+repopulate race persisted the stale snapshot).
    /// </summary>
    [Fact]
    public async Task AReload_RacingASet_NeverErasesTheJustWrittenValue()
    {
        var directory = NewSettingsDirectory();
        try
        {
            var path = Path.Combine(directory, "settings.json");
            WriteFile(path, """{ "chart": { "symbol": "BTC/USDT" } }""");

            var service = new SettingsService(path, NullLogger<SettingsService>.Instance);
            try
            {
                await service.InitializeAsync();

                var reload = service.ReloadAsync();
                var set = service.SetAsync("chart", "symbol", "ETH/USDT");
                await Task.WhenAll(reload, set);

                // The written value survived the interleaving in memory.
                (await service.GetAsync("chart", "symbol", "unset"))
                    .Should().Be("ETH/USDT", "a reload racing a set must not erase the just-written value");

                // The follow-up save persists the memory truth (no silent revert).
                await service.SaveAsync();
                File.ReadAllText(path).Should().Contain("ETH/USDT");
                (await service.GetAsync("chart", "symbol", "unset")).Should().Be("ETH/USDT");
            }
            finally
            {
                service.Dispose();
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// BUG-012, the save side: a reload that grabs the lock FIRST stays under
    /// it while a queued set applies afterwards — lock-serialization, not
    /// last-undefined-write-wins.
    /// </summary>
    [Fact]
    public async Task TwoSets_SerializedByTheSaveLock_BothSurvive()
    {
        var directory = NewSettingsDirectory();
        try
        {
            var path = Path.Combine(directory, "settings.json");
            WriteFile(path, """{ "chart": { "symbol": "BTC/USDT" } }""");

            var service = new SettingsService(path, NullLogger<SettingsService>.Instance);
            try
            {
                await service.InitializeAsync();

                var first = service.SetAsync("chart", "symbol", "ETH/USDT");
                var second = service.SetAsync("chart", "theme", "light");
                await Task.WhenAll(first, second);

                (await service.GetAsync("chart", "symbol", "unset")).Should().Be("ETH/USDT");
                (await service.GetAsync("chart", "theme", "unset")).Should().Be("light");
                File.ReadAllText(path).Should().Contain("light").And.Contain("ETH/USDT");
            }
            finally
            {
                service.Dispose();
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    #endregion

    #region Tests: reload publication

    /// <summary>
    /// BUG-013: an external reload publishes one event per difference against
    /// the previous memory snapshot.
    /// </summary>
    [Fact]
    public async Task AReload_PublishesOneEventPerDifference()
    {
        var directory = NewSettingsDirectory();
        try
        {
            var path = Path.Combine(directory, "settings.json");
            WriteFile(path, """{ "chart": { "symbol": "BTC/USDT" } }""");

            var service = new SettingsService(path, NullLogger<SettingsService>.Instance);
            try
            {
                await service.InitializeAsync();

                var events = new List<SettingsChange>();
                using var subscription = service.Changes.Subscribe(events.Add);

                WriteFile(path, """{ "chart": { "symbol": "ETH/USDT", "theme": "light" } }""");
                await service.ReloadAsync();

                var symbolEvent = SingleEvent(events, "chart", "symbol");
                symbolEvent.OldValue.Should().BeOfType<JsonElement>("reload payloads share the file model's element type");
                ElementString(symbolEvent.OldValue).Should().Be("BTC/USDT");
                ElementString(symbolEvent.NewValue).Should().Be("ETH/USDT");

                var addedEvent = SingleEvent(events, "chart", "theme");
                addedEvent.OldValue.Should().BeNull("the key did not exist before the reload");
                ElementString(addedEvent.NewValue).Should().Be("light");

                // And the state itself is fresh.
                (await service.GetAsync("chart", "symbol", "unset")).Should().Be("ETH/USDT");
            }
            finally
            {
                service.Dispose();
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>A key removed by an external reload publishes its OLD value with an absent NEW side.</summary>
    [Fact]
    public async Task AReload_RemovingAKey_PublishesTheOldValueOnly()
    {
        var directory = NewSettingsDirectory();
        try
        {
            var path = Path.Combine(directory, "settings.json");
            WriteFile(path, """{ "chart": { "symbol": "BTC/USDT", "legacy": "x" } }""");

            var service = new SettingsService(path, NullLogger<SettingsService>.Instance);
            try
            {
                await service.InitializeAsync();

                var events = new List<SettingsChange>();
                using var subscription = service.Changes.Subscribe(events.Add);

                WriteFile(path, """{ "chart": { "symbol": "BTC/USDT" } }""");
                await service.ReloadAsync();

                var removed = SingleEvent(events, "chart", "legacy");
                ElementString(removed.OldValue).Should().Be("x");
                removed.NewValue.Should().BeNull("the key is gone after the reload");
            }
            finally
            {
                service.Dispose();
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// The full hot-reload path: an externally rewritten file surfaces through
    /// the debounced watcher pipeline as a published change within the debounce
    /// window.
    /// </summary>
    [Fact]
    public async Task AnExternallyRewrittenFile_SurfacesThroughTheWatcherPipeline()
    {
        var directory = NewSettingsDirectory();
        try
        {
            var path = Path.Combine(directory, "settings.json");
            WriteFile(path, """{ "chart": { "symbol": "BTC/USDT" } }""");

            var service = new SettingsService(path, NullLogger<SettingsService>.Instance);
            try
            {
                await service.InitializeAsync();

                // Subscribed AFTER the initialize: the initial load publishes
                // its own key events, and this fact pins the WATCHER path —
                // not whatever the initialize already announced.
                var events = new List<SettingsChange>();
                using var subscription = service.Changes.Subscribe(events.Add);

                WriteFile(path, """{ "chart": { "symbol": "ETH/USDT" } }""");

                var appeared = TestPoll.PollUntilAsync(
                    () => events.Any(e => e.Section == "chart" && e.Key == "symbol"),
                    EventWait);
                await appeared;

                (await service.GetAsync("chart", "symbol", "unset")).Should().Be("ETH/USDT");
            }
            finally
            {
                service.Dispose();
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    #endregion

    #region Tests: payload typing and the save echo

    /// <summary>
    /// BUG-045: a typed set's event carries old and new as the SAME typed
    /// payload (the pre-fix event boxed a raw JsonElement against the typed T).
    /// </summary>
    [Fact]
    public async Task ASet_PublishesOldValueAndNewValueAsOneType()
    {
        var directory = NewSettingsDirectory();
        try
        {
            var path = Path.Combine(directory, "settings.json");
            WriteFile(path, """{ "chart": { "clock24h": true } }""");

            var service = new SettingsService(path, NullLogger<SettingsService>.Instance);
            try
            {
                await service.InitializeAsync();

                var events = new List<SettingsChange>();
                using var subscription = service.Changes.Subscribe(events.Add);

                await service.SetAsync("chart", "clock24h", false);

                var change = events.Should().ContainSingle().Which;
                change.Section.Should().Be("chart");
                change.Key.Should().Be("clock24h");
                change.OldValue.Should().BeOfType<bool>().Which.Should().BeTrue();
                change.NewValue.Should().BeOfType<bool>().Which.Should().BeFalse();
            }
            finally
            {
                service.Dispose();
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>A first set on a fresh key reports the old side as absent.</summary>
    [Fact]
    public async Task AFirstSet_PublishesAnAbsentOldSide()
    {
        var directory = NewSettingsDirectory();
        try
        {
            var path = Path.Combine(directory, "settings.json");

            var service = new SettingsService(path, NullLogger<SettingsService>.Instance);
            try
            {
                await service.InitializeAsync();

                var events = new List<SettingsChange>();
                using var subscription = service.Changes.Subscribe(events.Add);

                await service.SetAsync("chart", "clock24h", true);

                var change = events.Should().ContainSingle().Which;
                change.OldValue.Should().BeNull();
                change.NewValue.Should().BeOfType<bool>().Which.Should().BeTrue();
            }
            finally
            {
                service.Dispose();
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// BUG-045: a save never self-triggers its own reload — the file event the
    /// save's own write produced lands after the debounce and must NOT surface
    /// as a second (echo) change, on top of the set's own event.
    /// </summary>
    [Fact]
    public async Task ASave_NeverSelfTriggersItsOwnReloadEcho()
    {
        var directory = NewSettingsDirectory();
        try
        {
            var path = Path.Combine(directory, "settings.json");
            WriteFile(path, """{ "chart": { "symbol": "BTC/USDT" } }""");

            var service = new SettingsService(path, NullLogger<SettingsService>.Instance);
            try
            {
                await service.InitializeAsync();

                var events = new List<SettingsChange>();
                using var subscription = service.Changes.Subscribe(events.Add);

                // The set rewrites the file; the watcher pipeline fires on the
                // save's own write and would, unguarded, reload and republish
                // everything after the debounce.
                await service.SetAsync("chart", "symbol", "ETH/USDT");

                await Task.Delay(ExternalEchoQuietWait);

                events.Should().ContainSingle(
                    "the set publishes exactly one change; the save's own echo must not reload-and-republish it");
            }
            finally
            {
                service.Dispose();
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// The soak harness's (T-141) seed file: the driver pins the demo adapter
    /// by writing exactly this JSON as the sandbox's appsettings.json. The
    /// fact proves the loader reads it as the Workspace/ExchangeId string —
    /// a seed format drift fails the gate instead of silently leaving the
    /// child on its default adapter (which the run's own isolation census
    /// would then catch as a missing demo restore).
    /// </summary>
    [Fact]
    public async Task TheSoakSeedFile_ReadsAsThePersistedDemoChoice()
    {
        var directory = NewSettingsDirectory();
        try
        {
            var path = Path.Combine(directory, "appsettings.json");
            WriteFile(path, """{ "Workspace": { "ExchangeId": "demo" } }""");

            var service = new SettingsService(path, NullLogger<SettingsService>.Instance);
            try
            {
                await service.InitializeAsync();

                (await service.GetAsync<string>("Workspace", "ExchangeId"))
                    .Should().Be("demo", "the harness's seeded demo pin is the persisted exchange id");
            }
            finally
            {
                service.Dispose();
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    #endregion

    #region Helpers

    private static void WriteFile(string path, string json) =>
        File.WriteAllText(path, json);

    private static string ElementString(object? payload) =>
        ((JsonElement)payload!).GetString()!;

    private static SettingsChange SingleEvent(
        IReadOnlyList<SettingsChange> events,
        string section,
        string key) =>
        events.Should().ContainSingle(
            e => e.Section == section && e.Key == key,
            $"exactly one change for {section}:{key}").Which;

    #endregion
}
