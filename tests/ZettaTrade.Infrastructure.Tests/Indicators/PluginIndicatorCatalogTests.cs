namespace ZettaTrade.Infrastructure.Tests.Indicators;

using System;
using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;
using ZettaTrade.Infrastructure.Indicators;
using ZettaTrade.Plugins.Sma;

/// <summary>
/// The plugin loader matrix: a valid plugin assembly resolves (the
/// ctor(int) convention preferred), a rejected period skips with a logged
/// warning, an unknown name resolves quietly to null, a garbage dll drops
/// on the debug level (T-229) without crashing the scan, a plugin with a
/// missing dependency fails the GetTypes() step and skips loudly (BUG-014)
/// and a missing plugins directory means no indicators at all. The scan
/// loads the dlls (the file
/// stays locked for the process lifetime), so the temp fixtures clean up
/// what they can and tolerate the locked leftovers.
/// </summary>
public sealed class PluginIndicatorCatalogTests
{
    #region Fixture

    /// <summary>
    /// A per-fact plugins directory: optionally staged with the real SMA
    /// plugin assembly (copied from the test run's own output) and a garbage
    /// dll for the failure path.
    /// </summary>
    private sealed class PluginsDirectoryFixture : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "zettatrade-indicator-tests",
            Guid.NewGuid().ToString("N"));

        public PluginsDirectoryFixture()
        {
            Directory.CreateDirectory(Path);
        }

        public string StageValidSmaPlugin()
        {
            var target = System.IO.Path.Combine(Path, "ZettaTrade.Plugins.Sma.dll");
            File.Copy(typeof(SmaIndicator).Assembly.Location, target, overwrite: true);
            return target;
        }

        public void StageGarbagePlugin()
        {
            File.WriteAllText(System.IO.Path.Combine(Path, "not-a-plugin.dll"), "this is not an assembly");
        }

        /// <summary>
        /// Stage the broken-dependency fixture: a VALID assembly whose base
        /// class lives in <c>ZettaTrade.Plugins.BrokenBase.dll</c> — an
        /// assembly deliberately never staged (it is compile-only for the
        /// test project) and deleted from the test output if tooling leaks
        /// it there, so the plugin's <c>GetTypes()</c> genuinely fails on a
        /// missing dependency (BUG-014). The dll is located BY NAME in the
        /// test output: loading the fixture's own type (typeof) would raise
        /// the very failure under test.
        /// </summary>
        public void StageBrokenDependencyPlugin()
        {
            File.Copy(
                System.IO.Path.Combine(AppContext.BaseDirectory, "ZettaTrade.Plugins.Broken.dll"),
                System.IO.Path.Combine(Path, "ZettaTrade.Plugins.Broken.dll"),
                overwrite: true);

            var leaked = System.IO.Path.Combine(
                AppContext.BaseDirectory,
                "ZettaTrade.Plugins.BrokenBase.dll");
            if (File.Exists(leaked))
            {
                File.Delete(leaked);
            }
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

    /// <summary>The logger spy: records the level + message pairs.</summary>
    private sealed class SpyLogger : ILogger<PluginIndicatorCatalog>
    {
        public System.Collections.Generic.List<(LogLevel Level, string Message)> Entries { get; } = [];

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

    [Fact]
    public void Scan_AValidPluginAssembly_RegistersAndBuildsThroughThePeriodCtor()
    {
        using var fixture = new PluginsDirectoryFixture();
        fixture.StageValidSmaPlugin();
        var logger = new SpyLogger();

        var catalog = new PluginIndicatorCatalog(fixture.Path, logger);

        var indicator = catalog.Create("SMA", 2);
        indicator.Should().NotBeNull("the SMA plugin assembly registers under its canonical name");
        indicator!.Name.Should().Be("SMA");

        // The ctor(int) convention: the row's period reaches the indicator.
        // The parameterless fallback (period 20) would have produced all
        // nulls over a three-bar series; period 2 rolls the mean.
        var bars = new[]
        {
            new ZettaTrade.Plugins.Abstractions.IndicatorBar(1m, 1m, 1m, 3m),
            new ZettaTrade.Plugins.Abstractions.IndicatorBar(1m, 1m, 1m, 5m),
            new ZettaTrade.Plugins.Abstractions.IndicatorBar(1m, 1m, 1m, 7m),
        };
        var values = indicator.Apply(bars);
        values[0].Should().BeNull("the warmup head");
        values[1].Should().Be(4m, "the mean of closes 3,5");
        values[2].Should().Be(6m, "the mean of closes 5,7");

        logger.Entries.Should().Contain(entry =>
            entry.Level == LogLevel.Information &&
            entry.Message.Contains("SMA"),
            "the scan logs the discovered indicator names");
    }

    [Fact]
    public void Create_ARejectedPeriod_WarnsAndResolvesToNull()
    {
        using var fixture = new PluginsDirectoryFixture();
        fixture.StageValidSmaPlugin();
        var logger = new SpyLogger();

        var catalog = new PluginIndicatorCatalog(fixture.Path, logger);

        catalog.Create("SMA", 0).Should().BeNull("the plugin rejects the period; nothing defaults");
        logger.Entries.Should().Contain(entry =>
            entry.Level == LogLevel.Warning &&
            entry.Message.Contains("SMA"),
            "the rejection is loud");
    }

    [Fact]
    public void Create_AnUnknownName_ResolvesQuietlyToNull()
    {
        using var fixture = new PluginsDirectoryFixture();
        fixture.StageValidSmaPlugin();
        var logger = new SpyLogger();

        var catalog = new PluginIndicatorCatalog(fixture.Path, logger);

        catalog.Create("EMA", 9).Should().BeNull();
        logger.Entries.Should().NotContain(entry =>
            entry.Level == LogLevel.Warning && entry.Message.Contains("EMA"),
            "an unknown name is a scan-time concern, not a per-create warning");
    }

    [Fact]
    public void Scan_AGarbageDll_DropsItOnTheDebugLevelAndKeepsWorking()
    {
        using var fixture = new PluginsDirectoryFixture();
        fixture.StageValidSmaPlugin();
        fixture.StageGarbagePlugin();
        var logger = new SpyLogger();

        var catalog = new PluginIndicatorCatalog(fixture.Path, logger);

        catalog.Create("SMA", 20).Should().NotBeNull("the malformed neighbor never breaks the scan");
        logger.Entries.Should().Contain(entry =>
            entry.Level == LogLevel.Debug &&
            entry.Message.Contains("not-a-plugin.dll"),
            "a file that is not a loadable assembly is the scan's routine cargo and drops on the debug level (T-229)");
        logger.Entries.Should().NotContain(entry =>
            entry.Level == LogLevel.Warning &&
            entry.Message.Contains("not-a-plugin.dll"),
            "a stray file in the plugins folder is never a warning family (T-229)");
    }

    /// <summary>
    /// BUG-014: a plugin whose dependencies are missing fails INSIDE
    /// assembly.GetTypes() (a ReflectionTypeLoadException the pre-fix code
    /// let escape the whole scan). The file warns and is skipped, while the
    /// neighboring plugins keep registering and building.
    /// </summary>
    [Fact]
    public void Scan_APluginWithMissingDependencies_SkipsTheFileAndKeepsTheNeighborsWorking()
    {
        using var fixture = new PluginsDirectoryFixture();
        fixture.StageValidSmaPlugin();
        fixture.StageBrokenDependencyPlugin();
        fixture.StageGarbagePlugin();
        var logger = new SpyLogger();

        var catalog = new PluginIndicatorCatalog(fixture.Path, logger);

        catalog.Create("SMA", 2).Should().NotBeNull("the dependency-missing plugin never breaks its neighbors");
        logger.Entries.Should().Contain(entry =>
            entry.Level == LogLevel.Warning &&
            entry.Message.Contains("ZettaTrade.Plugins.Broken.dll") &&
            entry.Message.Contains("fail to load"),
            "the dependency-missing plugin skips loudly, not silently");
        logger.Entries.Should().Contain(entry =>
            entry.Level == LogLevel.Debug &&
            entry.Message.Contains("not-a-plugin.dll"),
            "the garbage neighbor drops on the debug level (T-229)");
    }

    /// <summary>
    /// The scan's precision: only the broken plugin is accused — the healthy
    /// SMA assembly never shows up in a warning (a false accusation would
    /// hide which file actually failed the reflection step).
    /// </summary>
    [Fact]
    public void Scan_APluginWithMissingDependencies_NeverWarnsAboutTheHealthyNeighbors()
    {
        using var fixture = new PluginsDirectoryFixture();
        fixture.StageValidSmaPlugin();
        fixture.StageBrokenDependencyPlugin();
        var logger = new SpyLogger();

        var catalog = new PluginIndicatorCatalog(fixture.Path, logger);

        catalog.Create("SMA", 2).Should().NotBeNull("the scan itself never throws and the healthy plugin registers");
        logger.Entries.Should().NotContain(entry =>
            entry.Level == LogLevel.Warning &&
            entry.Message.Contains("ZettaTrade.Plugins.Sma.dll"),
            "the warnings are per-file precise");
    }

    [Fact]
    public void Scan_AMissingDirectory_MeansNoIndicators()
    {
        var logger = new SpyLogger();
        var missing = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "zettatrade-indicator-tests",
            Guid.NewGuid().ToString("N"));

        var catalog = new PluginIndicatorCatalog(missing, logger);

        catalog.Create("SMA", 20).Should().BeNull("no directory, no plugins, no indicators");
        logger.Entries.Should().Contain(entry =>
            entry.Level == LogLevel.Information &&
            entry.Message.Contains("does not exist"),
            "the absence is recorded, not an error");
    }

    /// <summary>
    /// The loaded-name set the settings surface keys its rows against
    /// (T-067): a staged plugin registers its canonical name, an empty or
    /// missing plugins directory registers none — the scan gate reads
    /// exactly one truth on every path.
    /// </summary>
    [Fact]
    public void LoadedNames_TheStagedSmaPlugin_RegistersItsCanonicalName()
    {
        using var fixture = new PluginsDirectoryFixture();
        fixture.StageValidSmaPlugin();
        var logger = new SpyLogger();

        var catalog = new PluginIndicatorCatalog(fixture.Path, logger);

        catalog.LoadedNames.Should().Equal("SMA");
    }

    [Fact]
    public void LoadedNames_AnEmptyDirectory_IsEmpty()
    {
        using var fixture = new PluginsDirectoryFixture();
        var logger = new SpyLogger();

        var catalog = new PluginIndicatorCatalog(fixture.Path, logger);

        catalog.LoadedNames.Should().NotBeNull("the contract hands out a set, never null");
        catalog.LoadedNames.Should().BeEmpty("no plugin assembly in the folder, no registration");
    }

    [Fact]
    public void LoadedNames_AMissingDirectory_IsEmpty()
    {
        var logger = new SpyLogger();
        var missing = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "zettatrade-indicator-tests",
            Guid.NewGuid().ToString("N"));

        var catalog = new PluginIndicatorCatalog(missing, logger);

        catalog.LoadedNames.Should().BeEmpty("the early return leaves the initialized empty snapshot");
    }
}
