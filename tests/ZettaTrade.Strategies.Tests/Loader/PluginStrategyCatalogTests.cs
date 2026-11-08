namespace ZettaTrade.Strategies.Tests.Loader;

using System;
using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Plugins.SampleStrategy;
using ZettaTrade.Strategies;
using ZettaTrade.Strategies.Tests.Harness;

/// <summary>
/// The strategies loader matrix (T-111, the T-054 canon applied to the
/// strategy seam): a valid plugin assembly registers under its canonical
/// id, an empty-id type of a mixed assembly skips loudly while its healthy
/// neighbor registers, a duplicate id keeps the first registration, a
/// garbage dll warns without crashing the scan, a plugin with a missing
/// dependency fails the GetTypes() step and skips loudly (the T-054
/// regression, pinned) and a missing plugins directory is an honest zero.
/// The scan loads the dlls (the file stays locked for the process
/// lifetime), so the temp fixtures clean up what they can and tolerate
/// the locked leftovers.
/// </summary>
public sealed class PluginStrategyCatalogTests
{
    #region Fixture

    /// <summary>
    /// A per-fact strategies directory: optionally staged with the sample
    /// strategy plugin assembly (copied from the test run's own output BY
    /// NAME — a plugin loads from the plugins directory at runtime, never
    /// through the product graph) and a garbage dll for the failure path.
    /// </summary>
    private sealed class StrategiesDirectoryFixture : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "zettatrade-strategy-tests",
            Guid.NewGuid().ToString("N"));

        public StrategiesDirectoryFixture()
        {
            Directory.CreateDirectory(Path);
        }

        /// <summary>Stage the valid sample plugin under the given file name.</summary>
        public string StageValidSamplePlugin(string fileName)
        {
            var target = System.IO.Path.Combine(Path, fileName);
            File.Copy(typeof(SampleCrossStrategy).Assembly.Location, target, overwrite: true);
            return target;
        }

        public void StageGarbagePlugin()
        {
            File.WriteAllText(System.IO.Path.Combine(Path, "not-a-plugin.dll"), "this is not an assembly");
        }

        /// <summary>
        /// Stage the T-054 broken-dependency fixture: a VALID assembly whose
        /// base class lives in <c>ZettaTrade.Plugins.BrokenBase.dll</c> —
        /// an assembly deliberately never staged (it is compile-only for
        /// the test project) and deleted from the test output if tooling
        /// leaks it there, so the plugin's <c>GetTypes()</c> genuinely fails
        /// on a missing dependency. The dll is located BY NAME in the test
        /// output: loading the fixture's own type (typeof) would raise the
        /// very failure under test.
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
    private sealed class SpyLogger : ILogger<PluginStrategyCatalog>
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
    public void Scan_TheStagedSamplePlugin_RegistersItsCanonicalId()
    {
        using var fixture = new StrategiesDirectoryFixture();
        fixture.StageValidSamplePlugin("ZettaTrade.Plugins.SampleStrategy.dll");
        var logger = new SpyLogger();

        var catalog = new PluginStrategyCatalog(fixture.Path, logger);

        var strategy = catalog.Get(SampleCrossStrategy.StrategyId);
        strategy.Should().NotBeNull("the sample plugin assembly registers under its canonical id");
        strategy.Should().BeOfType<SampleCrossStrategy>("the ctor probe builds the documented parameterless shape");

        // The registered instance evaluates: a window ending on its own
        // high stands long — the sample's documented trivial read.
        var context = new StrategyContext(TestBars.Symbol, TimeFrame.M1, TestBars.Candles(2, 4, 6));
        strategy!.Evaluate(context).Should().Be(StrategyStance.Long, "the probe-built instance evaluates honestly");
        catalog.All.Should().ContainSingle(s => s.Id == SampleCrossStrategy.StrategyId, "the load-order snapshot holds the registration");

        logger.Entries.Should().Contain(entry =>
            entry.Level == LogLevel.Information &&
            entry.Message.Contains(SampleCrossStrategy.StrategyId),
            "the scan logs the discovered strategy ids");
    }

    /// <summary>
    /// The mixed assembly: the sample plugin carries a healthy type and an
    /// empty-id type. The per-type guard (T-054's per-type canon) drops the
    /// broken type alone, loudly, and the healthy neighbor of the SAME
    /// assembly still registers.
    /// </summary>
    [Fact]
    public void Scan_TheMixedAssembly_TheEmptyIdTypeSkipsAndItsNeighborRegisters()
    {
        using var fixture = new StrategiesDirectoryFixture();
        fixture.StageValidSamplePlugin("ZettaTrade.Plugins.SampleStrategy.dll");
        var logger = new SpyLogger();

        var catalog = new PluginStrategyCatalog(fixture.Path, logger);

        catalog.Get(SampleCrossStrategy.StrategyId).Should().NotBeNull("the healthy neighbor of the same file registers");
        catalog.All.Should().ContainSingle("only the id-carrying type registers; the empty id builds and is dropped");

        logger.Entries.Should().Contain(entry =>
            entry.Level == LogLevel.Warning &&
            entry.Message.Contains(nameof(NoIdStrategy)) &&
            entry.Message.Contains("empty id"),
            "the empty-id type skips loudly");
        logger.Entries.Should().NotContain(entry =>
            entry.Level == LogLevel.Warning &&
            entry.Message.Contains(nameof(SampleCrossStrategy)),
            "the warnings name the skipped type, never the healthy neighbor");
    }

    /// <summary>
    /// The registrations are keyed by the canonical id: a second file
    /// offering the same id does not join — the first registration wins,
    /// the duplicate warns.
    /// </summary>
    [Fact]
    public void Scan_ADuplicatedId_TheFirstRegistrationWinsAndTheDuplicateWarns()
    {
        using var fixture = new StrategiesDirectoryFixture();
        fixture.StageValidSamplePlugin("SampleCross.A.dll");
        fixture.StageValidSamplePlugin("SampleCross.B.dll");
        var logger = new SpyLogger();

        var catalog = new PluginStrategyCatalog(fixture.Path, logger);

        catalog.All.Should().ContainSingle("the id is the registration key; one registration per id");
        catalog.Get(SampleCrossStrategy.StrategyId).Should().NotBeNull();
        logger.Entries.Should().Contain(entry =>
            entry.Level == LogLevel.Warning &&
            entry.Message.Contains("already registered"),
            "the duplicate registration warns");
    }

    [Fact]
    public void Scan_AGarbageDll_WarnsAndKeepsWorking()
    {
        using var fixture = new StrategiesDirectoryFixture();
        fixture.StageValidSamplePlugin("ZettaTrade.Plugins.SampleStrategy.dll");
        fixture.StageGarbagePlugin();
        var logger = new SpyLogger();

        var catalog = new PluginStrategyCatalog(fixture.Path, logger);

        catalog.Get(SampleCrossStrategy.StrategyId).Should().NotBeNull("the malformed neighbor never breaks the scan");
        logger.Entries.Should().Contain(entry =>
            entry.Level == LogLevel.Warning &&
            entry.Message.Contains("not-a-plugin.dll"),
            "the malformed assembly skips loudly");
    }

    /// <summary>
    /// The T-054 regression, pinned for the strategy seam: a plugin whose
    /// dependencies are missing fails INSIDE assembly.GetTypes() (a
    /// ReflectionTypeLoadException the pre-fix code let escape the whole
    /// scan). The file warns and is skipped, the neighboring plugins keep
    /// registering and building — a broken/unloadable plugin degrades the
    /// scan, never the app.
    /// </summary>
    [Fact]
    public void Scan_APluginWithMissingDependencies_SkipsTheFileAndKeepsTheNeighborsWorking()
    {
        using var fixture = new StrategiesDirectoryFixture();
        fixture.StageValidSamplePlugin("ZettaTrade.Plugins.SampleStrategy.dll");
        fixture.StageBrokenDependencyPlugin();
        fixture.StageGarbagePlugin();
        var logger = new SpyLogger();

        var catalog = new PluginStrategyCatalog(fixture.Path, logger);

        catalog.Get(SampleCrossStrategy.StrategyId).Should().NotBeNull("the dependency-missing plugin never breaks its neighbors");
        catalog.All.Should().ContainSingle("the scan itself never throws for the healthy plugin");
        logger.Entries.Should().Contain(entry =>
            entry.Level == LogLevel.Warning &&
            entry.Message.Contains("ZettaTrade.Plugins.Broken.dll") &&
            entry.Message.Contains("fail to load"),
            "the dependency-missing plugin skips loudly, not silently");
        logger.Entries.Should().Contain(entry =>
            entry.Level == LogLevel.Warning &&
            entry.Message.Contains("not-a-plugin.dll"),
            "the garbage neighbor keeps its own warning");
    }

    /// <summary>
    /// The scan's precision: only the broken plugin is accused — the
    /// healthy sample assembly never shows up in a warning (a false
    /// accusation would hide which file actually failed the reflection
    /// step).
    /// </summary>
    [Fact]
    public void Scan_APluginWithMissingDependencies_NeverWarnsAboutTheHealthyNeighbors()
    {
        using var fixture = new StrategiesDirectoryFixture();
        fixture.StageValidSamplePlugin("ZettaTrade.Plugins.SampleStrategy.dll");
        fixture.StageBrokenDependencyPlugin();
        var logger = new SpyLogger();

        var catalog = new PluginStrategyCatalog(fixture.Path, logger);

        catalog.Get(SampleCrossStrategy.StrategyId).Should().NotBeNull("the scan itself never throws and the healthy plugin registers");
        logger.Entries.Should().NotContain(entry =>
            entry.Level == LogLevel.Warning &&
            entry.Message.Contains("ZettaTrade.Plugins.SampleStrategy.dll") &&
            (entry.Message.Contains("failed to load") ||
             entry.Message.Contains("fail to load") ||
             entry.Message.Contains("could not be inspected")),
            "the FILE-level warnings are precise: the healthy assembly is never accused (the per-type empty-id note is the only warning naming it)");
    }

    [Fact]
    public void Scan_AMissingDirectory_MeansNoStrategies()
    {
        var logger = new SpyLogger();
        var missing = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "zettatrade-strategy-tests",
            Guid.NewGuid().ToString("N"));

        var catalog = new PluginStrategyCatalog(missing, logger);

        catalog.Get(SampleCrossStrategy.StrategyId).Should().BeNull("no directory, no plugins, no strategies");
        catalog.All.Should().BeEmpty("the empty snapshot is the honest zero");
        logger.Entries.Should().Contain(entry =>
            entry.Level == LogLevel.Information &&
            entry.Message.Contains("does not exist"),
            "the absence is recorded, not an error");
    }

    [Fact]
    public void Scan_AnEmptyDirectory_RegistersNone()
    {
        using var fixture = new StrategiesDirectoryFixture();
        var logger = new SpyLogger();

        var catalog = new PluginStrategyCatalog(fixture.Path, logger);

        catalog.All.Should().NotBeNull("the contract hands out a snapshot, never null");
        catalog.All.Should().BeEmpty("no plugin assembly in the folder, no registration");
        logger.Entries.Should().NotContain(entry =>
            entry.Level == LogLevel.Warning,
            "an honest zero is not a failure; the scan stays quiet");
    }
}
