namespace ZettaTrade.App.Tests.Services;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ZettaTrade.App.Services;

/// <summary>
/// The log tail's end-to-end facts over fixture directories (T-101): the
/// seed reads the newest rolling file's line-carrying end and nothing re-reads,
/// the growth poll resolves only newline-terminated spans (a torn line waits),
/// the day roll re-resolves the newest file from its start, and the ring keeps
/// its bounded capacity while the lane hands out the evictions openly.
/// </summary>
/// <remarks>
/// The facts ride real-time waits over a fast internal poll interval; every
/// subscription forms BEFORE the surfaces it listens to (the standing Rx-test
/// hazard rule of the suite). Nothing here blocks: the settles ride a task
/// completion, only the settle wait suspends.
/// </remarks>
public sealed class LogTailServiceTests
{
    #region Constants

    /// <summary>The test poll interval (fast enough for real-time waits, slow enough to batch).</summary>
    private static readonly TimeSpan TestPollInterval = TimeSpan.FromMilliseconds(20);

    /// <summary>How long a fact waits for the poll loop to deliver (several polls).</summary>
    private static readonly TimeSpan PollDeliveryTimeout = TimeSpan.FromSeconds(3);

    /// <summary>A "confirm-absence" window: several poll cycles with no arrival.</summary>
    private static readonly TimeSpan AbsenceWindow = TimeSpan.FromMilliseconds(250);

    /// <summary>How many compact seed lines the big-boundary fact writes.</summary>
    private const int SeedLineCount = 30_000;

    #endregion

    #region Tests: the parser

    /// <summary>A header line becomes its own record; a non-header line continues it; a leading non-header line stands heading-less.</summary>
    [Fact]
    public void TheParser_HonorsTheSerilogHeaderShape_WithContinuationsAndHeadinglessLines()
    {
        var lines = new[]
        {
            "orphan continuation without any header above it",
            "2026-11-08 09:00:00.500 +02:00 [WRN] Tape.Ticker BTC/USDT moving",
            "   at ZettaTrade.App.Services.Leg.Run()",
            "2026-11-08 09:00:01.000 +02:00 [ERR] Exchange.Adapter Order refused",
        };

        var parsed = LogTailService.RedactAndParse(lines);

        parsed.Should().HaveCount(4);

        parsed[0].Level.Should().Be(LogLevel.None, "a heading-less line invents nothing");
        parsed[0].Moment.Should().Be(DateTimeOffset.MinValue, "a heading-less line invents no moment");
        parsed[0].Source.Should().BeEmpty();
        parsed[0].Message.Should().Be("orphan continuation without any header above it");
        parsed[0].IsContinuation.Should().BeTrue();

        parsed[1].Moment.ToUniversalTime().Should().Be(
            new DateTimeOffset(2026, 11, 8, 7, 0, 0, 500, TimeSpan.Zero),
            "the +02:00 offset maps to 07:00 UTC on every machine");
        parsed[1].Level.Should().Be(LogLevel.Warning);
        parsed[1].Source.Should().Be("Tape.Ticker");
        parsed[1].Message.Should().Be("BTC/USDT moving");
        parsed[1].IsContinuation.Should().BeFalse();

        parsed[2].Message.Should().Be("   at ZettaTrade.App.Services.Leg.Run()");
        parsed[2].Level.Should().Be(LogLevel.Warning, "a continuation inherits its header's level");
        parsed[2].Moment.Should().Be(parsed[1].Moment, "a continuation carries its header's moment");
        parsed[2].Source.Should().Be("Tape.Ticker");
        parsed[2].IsContinuation.Should().BeTrue();

        parsed[3].Level.Should().Be(LogLevel.Error, "a new header ends the continuation run");
        parsed[3].Source.Should().Be("Exchange.Adapter");
        parsed[3].Message.Should().Be("Order refused");
        parsed[3].IsContinuation.Should().BeFalse();
    }

    /// <summary>The parser runs after the redactor: the header shape survives while the secret value does not.</summary>
    [Fact]
    public void TheParser_RedactsBeforeParsing_KeepingHeaderStructure()
    {
        var lines = new[]
        {
            "2026-11-08 09:00:00.000 +00:00 [INF] Exchange.Http GET https://api/test?api_key=abc123def456 ok",
        };

        var parsed = LogTailService.RedactAndParse(lines);

        var only = parsed.Should().ContainSingle().Subject;
        only.Level.Should().Be(LogLevel.Information);
        only.Source.Should().Be("Exchange.Http");
        only.Message.Should().NotContain("abc123def456");
        only.Message.Should().Contain(LogRedactor.RedactedMarker);
    }

    #endregion

    #region Tests: the seed

    /// <summary>An empty directory seeds an empty ring; the tail stays honest until the first write lands.</summary>
    [Fact]
    public async Task AnEmptyDirectory_SeedsNothing_AndStaysHonest()
    {
        using var directory = new TempLogDirectory();
        using var service = new LogTailService(
            NullLogger<LogTailService>.Instance,
            directory.DirectoryPath,
            TestPollInterval);

        await service.InitializeAsync();

        (await service.HistorySnapshotAsync()).Should().BeEmpty();
        service.TailFile.Should().BeNull();
    }

    /// <summary>
    /// The seed reads ONLY the newest rolling file (the day stamp sorts
    /// lexically), delivers the seeded lines once through the lane and leaves
    /// the poll position at the file's end (the growth poll never re-publishes
    /// the seeded end).
    /// </summary>
    [Fact]
    public async Task TheSeed_ReadsTheNewestFilesEnd_OldestFirst()
    {
        using var directory = new TempLogDirectory();
        _ = directory.WriteFile(
            "zettatrade-20261107.log",
            Stub("INF", "Adapter.Core", "yesterday's last line"));
        var today = directory.WriteFile(
            "zettatrade-20261108.log",
            Stub("INF", "App.Start", "first line of today"),
            Stub("ERR", "Exchange.Adapter", "order refused"));

        using var service = new LogTailService(
            NullLogger<LogTailService>.Instance,
            directory.DirectoryPath,
            TestPollInterval);

        var slices = new List<LogTailSlice>();
        using var subscription = service.Slices.Subscribe(slices.Add);

        await service.InitializeAsync();

        var ring = await service.HistorySnapshotAsync();

        service.TailFile.Should().Be(today, "the day stamp sorts lexically and the newest file wins");
        ring.Should().HaveCount(2, "yesterday's file never seeds — only the newest one does");
        ring[0].Level.Should().Be(LogLevel.Information);
        ring[0].Source.Should().Be("App.Start");
        ring[0].Message.Should().Be("first line of today");
        ring[1].Level.Should().Be(LogLevel.Error);
        ring[1].Source.Should().Be("Exchange.Adapter");

        var slice = slices.Should().ContainSingle().Subject;
        slice.Appended.Should().HaveCount(2);
        slice.Evicted.Should().BeEmpty();
    }

    #endregion

    #region Tests: growth + torn lines + the day roll

    /// <summary>File growth arrives as redacted parsed lines in order; nothing invented, nothing duplicated.</summary>
    [Fact]
    public async Task Growth_ArrivesThroughTheSliceLane()
    {
        using var directory = new TempLogDirectory();
        var file = directory.WriteFile(
            "zettatrade-20261108.log",
            Stub("INF", "App.Start", "seed"));

        using var service = await StartedAsync(directory.DirectoryPath);

        var lines = new List<LogLine>();
        using var gate = new ArrivalGate(2);
        using var subscription = service.Slices.Subscribe(
            slice =>
            {
                foreach (var line in slice.Appended)
                {
                    lines.Add(line);
                    gate.Arrived();
                }
            });

        directory.Append(file, Stub("WRN", "Tape.Ticker", "grew one"), Stub("ERR", "Tape.Ticker", "grew two"));

        (await gate.Settled.WaitAsync(PollDeliveryTimeout))
            .Should().BeTrue("both grown lines land through the lane");
        lines.Should().HaveCount(2);
        lines[0].Level.Should().Be(LogLevel.Warning);
        lines[0].Message.Should().Be("grew one");
        lines[1].Level.Should().Be(LogLevel.Error);
        lines[1].Message.Should().Be("grew two");
    }

    /// <summary>A line without its newline waits: only complete lines publish, and completion publishes them once.</summary>
    [Fact]
    public async Task ATornLine_WaitsForItsTerminator()
    {
        using var directory = new TempLogDirectory();
        var file = directory.WriteFile(
            "zettatrade-20261108.log",
            Stub("INF", "App.Start", "seed"));

        using var service = await StartedAsync(directory.DirectoryPath);

        var lines = new List<LogLine>();
        using var gate = new ArrivalGate(1);
        using var subscription = service.Slices.Subscribe(
            slice =>
            {
                foreach (var line in slice.Appended)
                {
                    lines.Add(line);
                    gate.Arrived();
                }
            });

        // A torn tail: the header exists but no newline closes it yet.
        directory.AppendRaw(file, StubRaw("WRN", "Tape.Ticker", "torn", newline: false));

        await Task.Delay(AbsenceWindow);

        lines.Should().BeEmpty("nothing publishes before the newline closes the line");

        directory.AppendRaw(file, Environment.NewLine);

        (await gate.Settled.WaitAsync(PollDeliveryTimeout))
            .Should().BeTrue("the completed line publishes on the next poll");
        lines.Should().ContainSingle().Which.Message.Should().Be("torn", "the torn line completes whole, never twice");
    }

    /// <summary>The day roll: a new day's file re-resolves as the newest and continues from its start.</summary>
    [Fact]
    public async Task ADayRoll_ReResolvesTheNewestFile_FromItsStart()
    {
        using var directory = new TempLogDirectory();
        _ = directory.WriteFile(
            "zettatrade-20261108.log",
            Stub("INF", "App.Start", "today"));

        using var service = await StartedAsync(directory.DirectoryPath);

        var lines = new List<LogLine>();
        using var gate = new ArrivalGate(1);
        using var subscription = service.Slices.Subscribe(
            slice =>
            {
                foreach (var line in slice.Appended)
                {
                    lines.Add(line);
                    gate.Arrived();
                }
            });

        // Tomorrow's file appears while the tail watches today.
        var tomorrow = directory.WriteFile(
            "zettatrade-20261109.log",
            Stub("ERR", "Exchange.Check", "the roll moved here"));

        (await gate.Settled.WaitAsync(PollDeliveryTimeout))
            .Should().BeTrue("the rolled file's first line lands");

        service.TailFile.Should().Be(tomorrow, "the newest day's file supersedes the current one");
        lines.Should().ContainSingle().Which.Message.Should().Be("the roll moved here");
        (await service.HistorySnapshotAsync()).Should().HaveCount(2, "today's line stays in the ring past the roll");
    }

    #endregion

    #region Tests: the bounded ring

    /// <summary>
    /// The ring holds the capacity and evicts from the head: a pre-seeded
    /// backlog plus one capacity-sized burst leaves the newest lines in the
    /// ring while the lane hands out the evictions openly (the burst also
    /// proves the staging drops the OLDEST lines first).
    /// </summary>
    /// <remarks>
    /// The arithmetic stays honest without magic numbers: every compact
    /// seed line is exactly 42 bytes, the seed file (<see cref="SeedLineCount"/>
    /// lines = 1 260 000 bytes) outgrows the backlog's 1 MB window, so the
    /// seed applies only the windowed tail (about 25 000 complete lines —
    /// the head's partial line never shows) and the ring stays under its
    /// capacity until the burst overflows it into evictions.
    /// </remarks>
    [Fact]
    public async Task TheRing_KeepsCapacity_WithExplicitEvictions()
    {
        using var directory = new TempLogDirectory();
        var logger = new CapturingLogger();
        var file = WriteSeedFile(directory);

        var service = new LogTailService(
            logger,
            directory.DirectoryPath,
            TestPollInterval);

        await service.InitializeAsync();

        var evicted = new List<LogLine>();
        using var settled = new ManualResetEventSlim(initialState: false);
        using var subscription = service.Slices.Subscribe(
            slice =>
            {
                foreach (var line in slice.Evicted)
                {
                    evicted.Add(line);
                }

                settled.Set();
            });

        // Exactly the staging capacity in one burst: the channel stages the
        // whole burst, the ring takes all of it and pushes past the capacity.
        var burst = Enumerable.Range(0, LogTailService.BackpressureChannelCapacity)
            .Select(i => Stub("WRN", "Burst", $"burst line {i}"));
        directory.Append(file, string.Concat(burst));

        settled.Wait(PollDeliveryTimeout * 2).Should().BeTrue("the burst settles within a poll");

        var snapshot = await service.HistorySnapshotAsync();
        snapshot.Count.Should().Be(LogTailService.MaxHistoryLines, "the ring holds exactly the capacity");
        snapshot[^1].Message.Should().Be(
            $"burst line {LogTailService.BackpressureChannelCapacity - 1}",
            "the staging's newest line is the ring's tail");
        evicted.Should().NotBeEmpty("the ring's evictions ride the lane openly");
        evicted.Should().OnlyContain(
            line => line.Source.StartsWith("S", StringComparison.Ordinal),
            "the eviction eats the ring's seeded head first");

        service.Dispose();
    }

    /// <summary>
    /// Write the ring facts' compact seed file (fixed 41-byte lines, every
    /// one stamping ONE millisecond after its predecessor so each value
    /// record stays distinct) and return its path. The arithmetic:
    /// <see cref="SeedLineCount"/> lines = 1 230 000 bytes > the backlog's
    /// 1 MB window, so the seed applies only the windowed tail (~25 500
    /// lines) and one capacity-sized burst overflows the ring.
    /// </summary>
    private static string WriteSeedFile(TempLogDirectory directory)
    {
        var path = System.IO.Path.Combine(directory.DirectoryPath, "zettatrade-20261108.log");
        var firstLineMoment = new DateTimeOffset(2026, 11, 8, 9, 0, 0, TimeSpan.Zero);

        using var writer = new StreamWriter(path, append: false);
        for (var i = 0; i < SeedLineCount; i++)
        {
            var stamp = firstLineMoment
                .AddMilliseconds(i)
                .ToString("yyyy-MM-dd HH:mm:ss.fff zzz", System.Globalization.CultureInfo.InvariantCulture);
            writer.Write($"{stamp} [INF] S {Environment.NewLine}");
        }

        return path;
    }

    #endregion

    #region Harness

    /// <summary>Build one Serilog-shaped line (terminated); the parser's own timestamp format.</summary>
    private static string Stub(string level, string source, string message) =>
        StubRaw(level, source, message, newline: true);

    private static string StubRaw(string level, string source, string message, bool newline)
    {
        const string stamp = "2026-11-08 09:00:00.000 +00:00";
        var line = $"{stamp} [{level}] {source} {message}";

        return newline ? line + Environment.NewLine : line;
    }

    /// <summary>Start a service against a fixture directory and let the first poll cycle pass.</summary>
    private static async Task<LogTailService> StartedAsync(string directory)
    {
        var service = new LogTailService(
            NullLogger<LogTailService>.Instance,
            directory,
            TestPollInterval);

        await service.InitializeAsync();
        await Task.Delay(TestPollInterval * 2, CancellationToken.None);

        return service;
    }

    /// <summary>A capture logger for diagnosing the legs' swallowed faults (NullLogger hides them).</summary>
    private sealed class CapturingLogger : ILogger<LogTailService>
    {
        private readonly List<string> _entries = new();

        internal IReadOnlyList<string> Entries => _entries;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            _entries.Add($"{logLevel}: {formatter(state, exception)} {exception}");
    }

    /// <summary>
    /// The fixture directory: auto-cleaned, holds real log files without
    /// touching the developer machine's logs.
    /// </summary>
    private sealed class TempLogDirectory : IDisposable
    {
        internal TempLogDirectory()
        {
            DirectoryPath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "zettatrade-tail-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DirectoryPath);
        }

        internal string DirectoryPath { get; }

        internal string WriteFile(string name, params string[] lines)
        {
            var path = System.IO.Path.Combine(DirectoryPath, name);
            File.WriteAllText(path, string.Concat(lines));

            return path;
        }

        internal string WriteFile(string name, IEnumerable<string> lines)
        {
            var path = System.IO.Path.Combine(DirectoryPath, name);
            File.WriteAllText(path, string.Concat(lines));

            return path;
        }

        internal void Append(string path, params string[] lines) =>
            File.AppendAllText(path, string.Concat(lines));

        internal void AppendRaw(string path, string text) =>
            File.AppendAllText(path, text);

        public void Dispose()
        {
            try
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
                // Already gone: the fixtures' own race path.
            }
        }
    }

    /// <summary>The settled-gate: a task-based latch for real-time waits — never a blocking primitive inside the facts.</summary>
    private sealed class ArrivalGate : IDisposable
    {
        private readonly TaskCompletionSource<bool> _settle =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly int _target;
        private int _count;

        internal ArrivalGate(int target) => _target = target;

        internal Task<bool> Settled => _settle.Task;

        internal void Arrived()
        {
            if (Interlocked.Increment(ref _count) >= _target)
            {
                _settle.TrySetResult(true);
            }
        }

        /// <summary>Nothing to release: the latch dies with the fact's locals.</summary>
        public void Dispose() { }
    }

    #endregion
}
