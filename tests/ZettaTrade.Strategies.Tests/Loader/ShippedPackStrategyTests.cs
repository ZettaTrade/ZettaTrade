namespace ZettaTrade.Strategies.Tests.Loader;

using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;
using ZettaTrade.Strategies;

/// <summary>
/// The shipped pack's strategies, through the real loader (T-168, T-174):
/// the standard package's strategy assemblies - the feed's lib assets the
/// dev staging copies into the plugins directory - register under their
/// canonical ids when the production scan walks them. This is the
/// picker-facing contract: the backtest window's strategy picker reads
/// exactly this catalog, so a shipped pack makes the picker non-empty
/// (the T-111 gap - the seam had no shipped strategy, the scan honestly
/// answered zero). The pack is never linked, so the ids stay literals.
/// </summary>
public sealed class ShippedPackStrategyTests
{
    /// <summary>The id the shipped pack's SmaCross registers under.</summary>
    private const string ShippedStrategyId = "sma-golden-cross";

    /// <summary>The canonical ids the shipped pack's four strategies register under (T-174).</summary>
    private static readonly string[] ShippedStrategyIds =
    [
        "sma-golden-cross",
        "ema-cross",
        "rsi-reversion",
        "bollinger-breakout",
    ];

    /// <summary>The lib asset each shipped strategy rides (the PluginsVersion consumer's copy-local set).</summary>
    private static readonly string[] ShippedStrategyDlls =
    [
        "ZettaTrade.Plugins.SmaCross.dll",
        "ZettaTrade.Plugins.EmaCross.dll",
        "ZettaTrade.Plugins.RsiReversion.dll",
        "ZettaTrade.Plugins.BollingerBreakout.dll",
    ];

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

    [Fact]
    public void Scan_TheShippedPacksSmaCross_RegistersUnderItsCanonicalId()
    {
        var directory = Stage(ShippedStrategyDlls[0]);
        try
        {
            var logger = new SpyLogger();

            var catalog = new PluginStrategyCatalog(directory, logger);

            catalog.Get(ShippedStrategyId).Should().NotBeNull("the shipped strategy registers under the canonical id");
            catalog.All.Should().ContainSingle(s => s.Id == ShippedStrategyId, "the staged scan's only strategy is the registration");
            logger.Entries.Should().Contain(entry =>
                entry.Level == LogLevel.Information &&
                entry.Message.Contains(ShippedStrategyId),
                "the scan's log line names the shipped strategy id");
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public void Scan_TheShippedPacksStrategyCatalog_RegistersAllFourCanonicalIds()
    {
        // The pack 0.3.0 (T-174) ships four strategies: the production scan
        // walks the staged lib assets and the picker's catalog lists
        // exactly the canonical id set.
        foreach (var name in ShippedStrategyDlls)
        {
            File.Exists(Path.Combine(AppContext.BaseDirectory, name)).Should().BeTrue(
                $"the standard pack's {name} copies into the suite output (the PluginsVersion consumer)");
        }

        var directory = Stage(ShippedStrategyDlls);
        try
        {
            var logger = new SpyLogger();

            var catalog = new PluginStrategyCatalog(directory, logger);

            catalog.All.Select(strategy => strategy.Id).Should().BeEquivalentTo(
                ShippedStrategyIds,
                "the pack's four strategies are the staged scan's whole registration");
            foreach (var id in ShippedStrategyIds)
            {
                logger.Entries.Should().Contain(entry =>
                    entry.Level == LogLevel.Information && entry.Message.Contains(id),
                    $"the scan's log line names {id}");
            }
        }
        finally
        {
            Cleanup(directory);
        }
    }

    /// <summary>Stage the named pack DLLs into a fresh scan directory.</summary>
    private static string Stage(params string[] dllNames)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "zettatrade-strategy-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        foreach (var name in dllNames)
        {
            File.Copy(
                Path.Combine(AppContext.BaseDirectory, name),
                Path.Combine(directory, name),
                overwrite: true);
        }

        return directory;
    }

    /// <summary>
    /// Delete what unlocks - the LoadFrom'd assembly holds its file
    /// locked, so tolerate the rest (the OS cleans %TEMP%).
    /// </summary>
    private static void Cleanup(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
            }
        }

        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
