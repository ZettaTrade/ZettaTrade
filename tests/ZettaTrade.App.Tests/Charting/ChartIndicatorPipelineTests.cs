namespace ZettaTrade.App.Tests.Charting;

using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;
using ZettaTrade.App.Services;
using ZettaTrade.Charting;
using ZettaTrade.Core.Entities;
using ZettaTrade.Plugins.Abstractions;

/// <summary>
/// The indicator compute pipeline on a fake catalog and a stub indicator
/// (pure, marshal-free): enabled rows compute, disabled and unresolvable
/// rows skip, the row order survives and the parse problems surface as
/// logged warnings.
/// </summary>
public sealed class ChartIndicatorPipelineTests
{
    #region Fakes

    /// <summary>A stub indicator: every bar maps to the constant 42.</summary>
    private sealed class StubIndicator : IIndicator
    {
        public string Name => "STUB";

        public IReadOnlyList<decimal?> Apply(IReadOnlyList<IndicatorBar> bars) =>
            [.. new decimal?[bars.Count].Select(value => 42m)];
    }

    /// <summary>The catalog fake: resolves "STUB" only and records the Create calls.</summary>
    private sealed class FakeCatalog : IIndicatorCatalog
    {
        public List<(string Name, int Period)> CreateCalls { get; } = [];

        public IReadOnlyCollection<string> LoadedNames => ["STUB"];

        public IIndicator? Create(string name, int period)
        {
            CreateCalls.Add((name, period));
            return name == "STUB" ? new StubIndicator() : null;
        }
    }

    /// <summary>The logger spy: counts the warning entries (marshal-free, no NSubstitute extension gymnastics).</summary>
    private sealed class SpyLogger : ILogger
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }

    private static IReadOnlyList<Bar> MakeBars(int count)
    {
        var start = new System.DateTimeOffset(2026, 1, 1, 0, 0, 0, System.TimeSpan.Zero);
        return [.. Enumerable.Range(0, count).Select(i => new Bar(
            start.AddMinutes(i),
            1m,
            2m,
            0.5m,
            1.5m,
            3))];
    }

    #endregion

    [Fact]
    public void Compute_AnEnabledRow_ComputesThroughTheCatalog()
    {
        var catalog = new FakeCatalog();
        var logger = new SpyLogger();

        var lines = ChartIndicatorPipeline.Compute(
            MakeBars(5),
            """[{"name":"STUB","enabled":true,"period":20}]""",
            catalog,
            logger);

        lines.Should().ContainSingle();
        lines[0].Name.Should().Be("STUB");
        lines[0].Values.Should().HaveCount(5).And.OnlyContain(value => value == 42m);
        catalog.CreateCalls.Should().ContainSingle().Which.Should().Be(("STUB", 20));
        logger.Warnings.Should().BeEmpty();
    }

    [Fact]
    public void Compute_ADisabledRow_NeverTouchesTheCatalog()
    {
        var catalog = new FakeCatalog();
        var logger = new SpyLogger();

        var lines = ChartIndicatorPipeline.Compute(
            MakeBars(5),
            """[{"name":"STUB","enabled":false,"period":20}]""",
            catalog,
            logger);

        lines.Should().BeEmpty();
        catalog.CreateCalls.Should().BeEmpty("a disabled row is not even resolved");
        logger.Warnings.Should().BeEmpty();
    }

    [Fact]
    public void Compute_AnUnknownName_SkipsQuietly()
    {
        var catalog = new FakeCatalog();
        var logger = new SpyLogger();

        var lines = ChartIndicatorPipeline.Compute(
            MakeBars(5),
            """[{"name":"EMA","enabled":true,"period":9}]""",
            catalog,
            logger);

        lines.Should().BeEmpty();
        logger.Warnings.Should().BeEmpty("the missing plugin is recorded at the scan, not per compute");
    }

    [Fact]
    public void Compute_AResolveFailure_SkipsTheRow()
    {
        var catalog = new FakeCatalog();
        var logger = new SpyLogger();

        var lines = ChartIndicatorPipeline.Compute(
            MakeBars(5),
            """[{"name":"STUB","enabled":true,"period":20}]""",
            new RejectingCatalog(),
            logger);

        lines.Should().BeEmpty();
    }

    [Fact]
    public void Compute_MultipleRows_KeepTheRowOrder()
    {
        var catalog = new FakeCatalog();
        var logger = new SpyLogger();

        var lines = ChartIndicatorPipeline.Compute(
            MakeBars(3),
            """[{"name":"EMA","enabled":true,"period":9},{"name":"STUB","enabled":true,"period":20}]""",
            catalog,
            logger);

        lines.Should().ContainSingle().Which.Name.Should().Be("STUB");
    }

    [Fact]
    public void Compute_GarbageConfig_PublishesNothingAndWarns()
    {
        var catalog = new FakeCatalog();
        var logger = new SpyLogger();

        var lines = ChartIndicatorPipeline.Compute(MakeBars(5), """[{"name":""", catalog, logger);

        lines.Should().BeEmpty();
        catalog.CreateCalls.Should().BeEmpty();
        logger.Warnings.Should().ContainSingle().Which.Should().Contain("JSON");
    }

    [Fact]
    public void Compute_AnEmptySeries_PublishesNothing()
    {
        var catalog = new FakeCatalog();
        var logger = new SpyLogger();

        var lines = ChartIndicatorPipeline.Compute(
            [],
            """[{"name":"STUB","enabled":true,"period":20}]""",
            catalog,
            logger);

        lines.Should().BeEmpty();
        catalog.CreateCalls.Should().BeEmpty("no bars means nothing to compute against");
    }

    /// <summary>The catalog variant that fails the build (a rejected period) - the row must skip, not throw.</summary>
    private sealed class RejectingCatalog : IIndicatorCatalog
    {
        public IReadOnlyCollection<string> LoadedNames { get; } = [];

        public IIndicator? Create(string name, int period) => null;
    }
}
