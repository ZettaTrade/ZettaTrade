namespace ZettaTrade.App.Tests.Services;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;
using ZettaTrade.App.Services;
using ZettaTrade.App.ViewModels;

/// <summary>
/// The bounded excerpt writer's facts (T-101): the file renders one line per
/// row in the log's own shape (header lines carry moment, level, source,
/// message; continuation lines carry their message alone), the order holds
/// oldest-first, and the write lands UTF-8 on the picked path.
/// </summary>
public sealed class LogExcerptWriterTests
{
    /// <summary>A mixed excerpt renders exactly as the log shapes itself.</summary>
    [Fact]
    public async Task AnExcerpt_RendersOneLinePerRow_InTheLogsOwnShape()
    {
        var moment = new DateTimeOffset(2026, 11, 8, 9, 0, 0, 500, TimeSpan.Zero);
        var header = new LogLine(moment, LogLevel.Warning, "Tape.Ticker", "book drifted <redacted>", IsContinuation: false);
        var continuation = header with { Message = "   at Leg.Run()", IsContinuation = true };
        var rows = new List<LogViewerRow>
        {
            new(header, "2026-11-08 09:00:00.500", "Warn"),
            new(continuation, string.Empty, string.Empty),
        };

        var path = TempExcerptPath();
        try
        {
            await LogExcerptWriter.WriteAsync(path, rows);

            var content = await File.ReadAllTextAsync(path);
            content.ReplaceLineEndings(Environment.NewLine).Should().Be(
                "2026-11-08 09:00:00.500 [Warn] Tape.Ticker book drifted <redacted>" + Environment.NewLine +
                "   at Leg.Run()" + Environment.NewLine,
                "one line per row: continuations carry their message alone");
        }
        finally
        {
            TryDelete(path);
        }
    }

    /// <summary>The row order holds oldest-first (a faithful dump never reorders).</summary>
    [Fact]
    public async Task TheRowOrder_HoldsOldestFirst()
    {
        var first = new LogViewerRow(
            new LogLine(
                new DateTimeOffset(2026, 11, 8, 9, 0, 0, 100, TimeSpan.Zero),
                LogLevel.Information,
                "Seed",
                "alpha",
                IsContinuation: false),
            "2026-11-08 09:00:00.100",
            "Info");
        var second = new LogViewerRow(
            new LogLine(
                new DateTimeOffset(2026, 11, 8, 9, 0, 1, 100, TimeSpan.Zero),
                LogLevel.Error,
                "Seed",
                "beta",
                IsContinuation: false),
            "2026-11-08 09:00:01.100",
            "Error");

        var path = TempExcerptPath();
        try
        {
            await LogExcerptWriter.WriteAsync(path, new[] { first, second });

            var lines = (await File.ReadAllLinesAsync(path)).ToList();
            lines.Should().HaveCount(2);
            lines[0].Should().Contain("alpha");
            lines[1].Should().Contain("beta");
        }
        finally
        {
            TryDelete(path);
        }
    }

    /// <summary>An empty excerpt writes an empty file (a bounded empty slice exports honestly).</summary>
    [Fact]
    public async Task AnEmptyExcerpt_WritesAnEmptyFile()
    {
        var path = TempExcerptPath();
        try
        {
            await LogExcerptWriter.WriteAsync(path, []);

            (await File.ReadAllTextAsync(path)).Should().BeEmpty();
        }
        finally
        {
            TryDelete(path);
        }
    }

    /// <summary>The writer's guards: null path or rows fault before anything lands.</summary>
    [Fact]
    public async Task TheGuards_FaultExplicitly()
    {
        var rows = Array.Empty<LogViewerRow>();

        await FluentActions.Invoking(async () => await LogExcerptWriter.WriteAsync(null!, rows))
            .Should().ThrowAsync<ArgumentNullException>();

        var path = Path.Combine(Path.GetTempPath(), "excerpt-guard.txt");

        await FluentActions.Invoking(async () => await LogExcerptWriter.WriteAsync(path, null!))
            .Should().ThrowAsync<ArgumentNullException>();
    }

    private static string TempExcerptPath() =>
        Path.Combine(
            Path.GetTempPath(),
            "zettatrade-excerpt-tests-" + Guid.NewGuid().ToString("N") + ".txt");

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // The temp workspace's own cleanup race path.
        }
    }
}
