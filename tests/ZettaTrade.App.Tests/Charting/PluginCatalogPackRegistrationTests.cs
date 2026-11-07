namespace ZettaTrade.App.Tests.Charting;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ZettaTrade.Infrastructure.Indicators;

/// <summary>
/// The standard pack's catalog registration facts (T-092): the six bundled
/// plugin assemblies stage through the SAME plugins-directory scan the SMA
/// sample has always used - the pack registers nothing outside the scan
/// path, all six resolvable from the catalog through the ctor(int)
/// convention, and the tolerant scan keeps skipping its broken neighbors
/// unchanged. The cold-start charter also pins here: the production
/// assemblies (App, Charting, Infrastructure) reference NO plugin assembly
/// - an unloaded pack costs nothing, the plugins folder is the only thing
/// that can bring one in.
/// </summary>
public sealed class PluginCatalogPackRegistrationTests
{
    #region Fixture

    /// <summary>
    /// A per-fact plugins directory: staged with real plugin assemblies
    /// copied from the build output by NAME (the same contract the loader
    /// fixture suite carries), plus a garbage dll for the failure path.
    /// </summary>
    private sealed class PluginsDirectoryFixture : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "zettatrade-indicator-pack-tests",
            Guid.NewGuid().ToString("N"));

        public PluginsDirectoryFixture()
        {
            Directory.CreateDirectory(Path);
        }

        /// <summary>Stage one assembly by its file name off the build output.</summary>
        public void StagePlugin(string fileName)
        {
            File.Copy(
                System.IO.Path.Combine(AppContext.BaseDirectory, fileName),
                System.IO.Path.Combine(Path, fileName),
                overwrite: true);
        }

        /// <summary>Stage the whole bundled pack: the SMA sample plus the six standard plugins.</summary>
        public void StageThePack()
        {
            StagePlugin("ZettaTrade.Plugins.Sma.dll");
            StagePlugin("ZettaTrade.Plugins.Ema.dll");
            StagePlugin("ZettaTrade.Plugins.Wma.dll");
            StagePlugin("ZettaTrade.Plugins.Rsi.dll");
            StagePlugin("ZettaTrade.Plugins.Macd.dll");
            StagePlugin("ZettaTrade.Plugins.Atr.dll");
            StagePlugin("ZettaTrade.Plugins.Bollinger.dll");
        }

        public void StageGarbagePlugin()
        {
            File.WriteAllText(System.IO.Path.Combine(Path, "not-a-plugin.dll"), "this is not an assembly");
        }

        public void Dispose()
        {
            try
            {
                // The LoadFrom'd assemblies hold their files locked: delete
                // what unlocks, tolerate the rest (the OS cleans %TEMP%).
                foreach (var file in Directory.EnumerateFiles(Path))
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch (IOException)
                    {
                    }
                }

                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    #endregion

    [Fact]
    public void Scan_TheStagedPack_RegistersAllSevenUnderTheirCanonicalNames()
    {
        using var fixture = new PluginsDirectoryFixture();
        fixture.StageThePack();

        var catalog = new PluginIndicatorCatalog(fixture.Path, NullLogger<PluginIndicatorCatalog>.Instance);

        catalog.LoadedNames.Should().BeEquivalentTo(
            ["SMA", "EMA", "WMA", "RSI", "MACD", "ATR", "BOLL"],
            "every bundled plugin registers under its canonical name, nothing else appears");
    }

    /// <summary>
    /// Registration coverage: every pack name resolves through the catalog
    /// and the built instance reports its own canonical name - the
    /// settings surface can key the whole pack by that name.
    /// </summary>
    [Theory]
    [InlineData("EMA")]
    [InlineData("WMA")]
    [InlineData("RSI")]
    [InlineData("MACD")]
    [InlineData("ATR")]
    [InlineData("BOLL")]
    public void Create_EveryPackName_BuildsTheCanonicalInstance(string name)
    {
        using var fixture = new PluginsDirectoryFixture();
        fixture.StageThePack();

        var catalog = new PluginIndicatorCatalog(fixture.Path, NullLogger<PluginIndicatorCatalog>.Instance);

        var indicator = catalog.Create(name, 3);
        indicator.Should().NotBeNull("'{0}' is registered by the bundled scan", name);
        indicator!.Name.Should().Be(name);
    }

    /// <summary>
    /// The row's period reaches the plugin through the ctor(int) convention
    /// (the settings roundtrip's compute half): a three-period WMA row
    /// carries a two-bar warmup while a two-period row carries one - two
    /// separate Create calls build two separate instances, the second
    /// period wins (the earlier probe with period 1 never sticks).
    /// </summary>
    [Fact]
    public void Create_TheRowPeriod_ReachesThePlugin()
    {
        using var fixture = new PluginsDirectoryFixture();
        fixture.StageThePack();

        var catalog = new PluginIndicatorCatalog(fixture.Path, NullLogger<PluginIndicatorCatalog>.Instance);
        var start = DateTimeOffset.FromUnixTimeSeconds(0);
        var bars = new[]
        {
            new ZettaTrade.Core.Entities.Bar(start, 1m, 1m, 1m, 1m, 1),
            new ZettaTrade.Core.Entities.Bar(start.AddMinutes(1), 1m, 1m, 1m, 2m, 1),
            new ZettaTrade.Core.Entities.Bar(start.AddMinutes(2), 1m, 1m, 1m, 3m, 1),
        };

        var wma = catalog.Create("WMA", 3);
        var wmaValues = wma!.Apply(bars);
        wmaValues[0].Should().BeNull("the warmup head");
        wmaValues[1].Should().BeNull("the three-bar window fills on the third bar");
        wmaValues[2].Should().NotBeNull("the row's period drives the warmup shape");

        var wma2 = catalog.Create("WMA", 2);
        var wma2Values = wma2!.Apply(bars);
        wma2Values[0].Should().BeNull("the warmup head");
        wma2Values[1].Should().NotBeNull("a fresh instance with the row's two-period window");
    }

    [Fact]
    public void Create_ARejectedPackPeriod_WarnsAndResolvesToNull()
    {
        using var fixture = new PluginsDirectoryFixture();
        fixture.StageThePack();
        var spy = new SpyLogger();

        var catalog = new PluginIndicatorCatalog(fixture.Path, spy);

        catalog.Create("EMA", 0).Should().BeNull("the plugin rejects the period; nothing defaults");
        catalog.Create("MACD", 30).Should().BeNull("the fast length must stay below the canonical slow");
        spy.Entries.Should().Contain(entry =>
            entry.Level == LogLevel.Warning && entry.Message.Contains("EMA"),
            "the rejection is loud");
        spy.Entries.Should().Contain(entry =>
            entry.Level == LogLevel.Warning && entry.Message.Contains("MACD"),
            "the rejection is loud");
    }

    /// <summary>
    /// Scan tolerance around the pack: the broken and garbage neighbors
    /// keep their landed skip behavior, the pack assemblies never appear in
    /// any warning (the suspicion stays per-file precise).
    /// </summary>
    [Fact]
    public void Scan_BrokenNeighborsAroundThePack_StaySkippedAndNeverTaintIt()
    {
        using var fixture = new PluginsDirectoryFixture();
        fixture.StageThePack();
        fixture.StageGarbagePlugin();
        // A valid assembly with no indicators: ZettaTrade.Core.dll (the
        // loader inspects and moves on - no IIndicator implementations in
        // the shell contracts).
        fixture.StagePlugin("ZettaTrade.Core.dll");
        var spy = new SpyLogger();

        var catalog = new PluginIndicatorCatalog(fixture.Path, spy);

        catalog.LoadedNames.Should().BeEquivalentTo(
            ["SMA", "EMA", "WMA", "RSI", "MACD", "ATR", "BOLL"],
            "a non-plugin neighbor never adds a name, the pack never loses one");
        spy.Entries.Should().Contain(entry =>
            entry.Level == LogLevel.Warning && entry.Message.Contains("not-a-plugin.dll"),
            "the garbage neighbor keeps its own warning");
        spy.Entries.Should().NotContain(entry =>
            entry.Level == LogLevel.Warning && entry.Message.Contains("ZettaTrade.Plugins."),
            "the warnings are per-file precise: the pack is never accused");
        catalog.Create("ATR", 14).Should().NotBeNull("the weakened scan never drops a healthy plugin");
    }

    /// <summary>
    /// Cold-start laziness: an empty plugins folder registers nothing but
    /// scans cleanly - the pack costs literally nothing until its
    /// assemblies land in the folder.
    /// </summary>
    [Fact]
    public void ColdStart_AnEmptyFolder_RegistersNothingAndSkipsCleanly()
    {
        using var fixture = new PluginsDirectoryFixture();
        var spy = new SpyLogger();

        var catalog = new PluginIndicatorCatalog(fixture.Path, spy);

        catalog.LoadedNames.Should().BeEmpty("nothing staged, nothing registered");
        catalog.Create("EMA", 20).Should().BeNull();
        spy.Entries.Should().NotContain(entry => entry.Level == LogLevel.Warning,
            "an empty folder is the honest zero state, not a failure");
    }

    /// <summary>
    /// Cold-start laziness, the architectural half: the production
    /// assemblies reference ZERO plugin assemblies - a cold start without
    /// the plugins folder cannot even see the pack (the scan is the only
    /// path in). The negative control proves the pin would catch one.
    /// </summary>
    [Fact]
    public void ColdStart_TheProductionGraph_ReferencesNoPluginAssembly()
    {
        var appRefs = typeof(ZettaTrade.App.App).Assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .ToList();
        var chartingRefs = typeof(ZettaTrade.Charting.IIndicator).Assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .ToList();
        var infrastructureRefs = typeof(PluginIndicatorCatalog).Assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .ToList();

        foreach (var refs in new[] { appRefs, chartingRefs, infrastructureRefs })
        {
            refs.Where(name => name is not null && name.StartsWith("ZettaTrade.Plugins.", StringComparison.Ordinal))
                .Should().BeEmpty("the production graph never references a plugin assembly");
        }

        // Negative control: this test assembly references the pack, the
        // same reflection reads it back.
        typeof(PluginCatalogPackRegistrationTests).Assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .Should().Contain("ZettaTrade.Plugins.Ema", "the reflection pin catches a real reference");
    }

    #region Spy Logger

    /// <summary>The logger spy: records the level + message pairs.</summary>
    private sealed class SpyLogger : ILogger<PluginIndicatorCatalog>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    #endregion
}
