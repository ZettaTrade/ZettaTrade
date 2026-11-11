namespace ZettaTrade.Strategies.Tests.Loader;

using System;
using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;
using ZettaTrade.Strategies;

/// <summary>
/// The shipped pack's strategy, through the real loader (T-168): the
/// standard package's SmaCross assembly - the feed's lib asset the dev
/// staging copies into the plugins directory - registers under its
/// canonical id when the production scan walks it. This is the
/// picker-facing contract: the backtest window's strategy picker reads
/// exactly this catalog, so a shipped pack makes the picker non-empty
/// (the T-111 gap - the seam had no shipped strategy, the scan honestly
/// answered zero). The pack is never linked, so the id stays a literal.
/// </summary>
public sealed class ShippedPackStrategyTests
{
    /// <summary>The id the shipped pack's strategy registers under.</summary>
    private const string ShippedStrategyId = "sma-golden-cross";

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
        var source = Path.Combine(
            AppContext.BaseDirectory,
            "ZettaTrade.Plugins.SmaCross.dll");
        File.Exists(source).Should().BeTrue(
            "the standard pack's lib assets copy-local into the suite output (the PluginsVersion consumer)");

        var directory = Path.Combine(
            Path.GetTempPath(),
            "zettatrade-strategy-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.Copy(source, Path.Combine(directory, "ZettaTrade.Plugins.SmaCross.dll"), overwrite: true);
            var logger = new SpyLogger();

            var catalog = new PluginStrategyCatalog(directory, logger);

            catalog.Get(ShippedStrategyId).Should().NotBeNull("the shipped strategy registers under the canonical id");
            catalog.All.Should().ContainSingle(s => s.Id == ShippedStrategyId, "the pack's only strategy is the registration");
            logger.Entries.Should().Contain(entry =>
                entry.Level == LogLevel.Information &&
                entry.Message.Contains(ShippedStrategyId),
                "the scan's log line names the shipped strategy id");
        }
        finally
        {
            // The LoadFrom'd assembly holds its file locked: delete what
            // unlocks, tolerate the rest (the OS cleans %TEMP%).
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
}
