namespace ZettaTrade.App.Tests.ViewModels;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Livery.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ZettaTrade.App;
using ZettaTrade.App.Services;
using ZettaTrade.App.ViewModels;

/// <summary>The half-day-agnostic status byte ReactiveUI.Primitives names RxVoid.</summary>
using RxVoid = ReactiveUI.Primitives.RxVoid;

/// <summary>
/// The log viewer's view model facts (T-101), run strictly sequentially on the
/// process's one Avalonia dispatcher: the transient view model attaches the
/// live lane first, seeds from the ring's snapshot right behind it, and the
/// value-identity guard keeps every crossing slice from double rendering; the
/// filters rebuild the surface synchronously out of the mirror (never the
/// disk); the export lands the matching newest slice (a cancelled pick exports
/// nothing, a failed write surfaces on the error line), and the evictions
/// land on the surface's head exactly as the ring hands them out.
/// </summary>
[Collection("AvaloniaDispatcherSerial")]
public sealed class LogViewerViewModelTests
{
    /// <summary>The dispatcher facts drain through the pump; claim it once, before any fact.</summary>
    static LogViewerViewModelTests() => AvaloniaDispatcherPump.EnsureInstalled();

    #region Constants

    /// <summary>The test poll interval (matches the service tests' cadence).</summary>
    private static readonly TimeSpan TestPollInterval = TimeSpan.FromMilliseconds(20);

    /// <summary>How long the loading facts wait for a poll to deliver growth.</summary>
    private static readonly TimeSpan PollDeliveryTimeout = TimeSpan.FromSeconds(3);

    /// <summary>How long a "crossing slice" fact waits for its poll cycle.</summary>
    private static readonly TimeSpan CrossingWindow = TimeSpan.FromMilliseconds(200);

    #endregion

    #region Tests: the attach / seed discipline

    /// <summary>The seed fills the surface, the counters, and the tail-file label once per open.</summary>
    [Fact]
    public async Task AttachAndLoad_SeedsTheSurface_Once()
    {
        var file = "zettatrade-20261108.log";
        using var directory = new TempLogDirectory();
        directory.WriteFile(
            file,
            Line("INF", "App.Start", "hello viewer"),
            Line("WRN", "Tape.Ticker", "book drifted"));

        using var service = await StartedAsync(directory.DirectoryPath);

        var viewModel = new LogViewerViewModel(
            service,
            new TestLocalization(),
            NullLogger<LogViewerViewModel>.Instance);

        using var lane = viewModel.AttachLiveTail();
        await viewModel.LoadCommand.Execute(RxVoid.Default);
        await AvaloniaDispatcherPump.DrainAsync();

        viewModel.Rows.Should().HaveCount(2);
        viewModel.HasRows.Should().BeTrue();
        viewModel.LinesLabel.Should().Be("[Log.Lines(2)]");
        viewModel.TailFileLabel.Should().StartWith("[Log.Tail.File(");
        viewModel.TailFileLabel.Should().Contain(file);

        viewModel.Rows[0].Line.Message.Should().Be("hello viewer");
        viewModel.Rows[0].LevelName.Should().Be("[Log.Level.Information]");
        viewModel.Rows[1].Line.Message.Should().Be("book drifted");
        viewModel.Rows[1].LevelName.Should().Be("[Log.Level.Warning]");
    }

    /// <summary>
    /// A slice crossing the attach renders exactly once: the pre-seed drop is
    /// factored out by the snapshot (the same lines the ring holds), so the
    /// value-identity guard never renders a line twice.
    /// </summary>
    [Fact]
    public async Task ACrossingSlice_RendersExactlyOnce()
    {
        using var directory = new TempLogDirectory();
        var file = directory.WriteFile(
            "zettatrade-20261108.log",
            Line("INF", "App.Start", "seed"));

        using var service = await StartedAsync(directory.DirectoryPath);

        var viewModel = new LogViewerViewModel(
            service,
            new TestLocalization(),
            NullLogger<LogViewerViewModel>.Instance);

        using var lane = viewModel.AttachLiveTail();

        // Growth lands on disk BEFORE the seed ran: the poll publishes a
        // crossing slice while the view model is still unseeded, so the slice
        // changes nothing — the snapshot carries the lines instead.
        directory.Append(file, Line("ERR", "Exchange.Adapter", "crossed before the seed"));
        await Task.Delay(CrossingWindow);

        viewModel.Rows.Should().BeEmpty("pre-seed slices change nothing");

        await viewModel.LoadCommand.Execute(RxVoid.Default);
        await AvaloniaDispatcherPump.DrainAsync();

        viewModel.Rows.Select(row => row.Line.Message)
            .Should().Equal(
                new[] { "seed", "crossed before the seed" },
                "nothing lost, nothing doubled — the snapshot carried the crossing line");
    }

    /// <summary>An empty log directory keeps the empty state and composes the no-file label.</summary>
    [Fact]
    public async Task AnEmptyDirectory_ComposesTheNoFileState()
    {
        using var directory = new TempLogDirectory();
        using var service = await StartedAsync(directory.DirectoryPath);

        var viewModel = new LogViewerViewModel(
            service,
            new TestLocalization(),
            NullLogger<LogViewerViewModel>.Instance);

        using var lane = viewModel.AttachLiveTail();
        await viewModel.LoadCommand.Execute(RxVoid.Default);
        await AvaloniaDispatcherPump.DrainAsync();

        viewModel.HasRows.Should().BeFalse();
        viewModel.Rows.Should().BeEmpty();
        viewModel.TailFileLabel.Should().Be("[Log.Tail.NoFile]");
    }

    /// <summary>Once the lane subscription dies, its slices change nothing (the attach handle dies with the window).</summary>
    [Fact]
    public async Task ADeadLaneStopsTheSurface()
    {
        using var directory = new TempLogDirectory();
        var file = directory.WriteFile(
            "zettatrade-20261108.log",
            Line("INF", "App.Start", "seed"));

        using var service = await StartedAsync(directory.DirectoryPath);

        var viewModel = new LogViewerViewModel(
            service,
            new TestLocalization(),
            NullLogger<LogViewerViewModel>.Instance);

        using (var lane = viewModel.AttachLiveTail())
        {
            await viewModel.LoadCommand.Execute(RxVoid.Default);
            await AvaloniaDispatcherPump.DrainAsync();
        }

        directory.Append(file, Line("WRN", "Tape.Ticker", "post-mortem"));
        await Task.Delay(TestPollInterval * 8);
        await AvaloniaDispatcherPump.DrainAsync();

        viewModel.Rows.Should().ContainSingle().Which.Line.Message.Should().Be(
            "seed",
            "a disposed lane subscription never mutates the surface again");
    }

    #endregion

    #region Tests: the filters

    /// <summary>The level and source filters rebuild from the mirror synchronously (never a disk read).</summary>
    [Fact]
    public async Task TheFilters_RebuildReActively_FromTheMirror()
    {
        using var directory = new TempLogDirectory();
        directory.WriteFile(
            "zettatrade-20261108.log",
            Line("DBG", "Exchange.Adapter", "connecting"),
            Line("INF", "App.Start", "started"),
            Line("WRN", "Tape.Ticker", "drifted"),
            Line("ERR", "Exchange.Adapter", "refused"));

        using var service = await StartedAsync(directory.DirectoryPath);

        var viewModel = new LogViewerViewModel(
            service,
            new TestLocalization(),
            NullLogger<LogViewerViewModel>.Instance);

        using var lane = viewModel.AttachLiveTail();
        await viewModel.LoadCommand.Execute(RxVoid.Default);
        await AvaloniaDispatcherPump.DrainAsync();

        viewModel.IsFiltered.Should().BeFalse();

        viewModel.SourceFilter = "Exchange";
        viewModel.IsFiltered.Should().BeTrue();
        viewModel.Rows.Select(row => row.Line.Source).Should().OnlyContain(source => source == "Exchange.Adapter");
        viewModel.Rows.Should().HaveCount(2, "the debug and information rows leave the surface");

        viewModel.SourceFilter = string.Empty;
        viewModel.LevelFilterIndex = 2;
        viewModel.Rows.Select(row => row.Line.Level)
            .Should().OnlyContain(level => level >= LogLevel.Warning, "warnings and above survive the level slot");
        viewModel.Rows.Select(row => row.Line.Level)
            .Should().NotContain(LogLevel.Trace).And.NotContain(LogLevel.Information);

        viewModel.LevelFilterIndex = 0;
        viewModel.IsFiltered.Should().BeFalse();
        viewModel.Rows.Should().HaveCount(4, "the filter flip landed back on the whole mirror");
    }

    #endregion

    #region Tests: the export

    /// <summary>The export lands the matching newest bounded slice on the picked path.</summary>
    [Fact]
    public async Task TheExport_WritesTheMatchingSlice_AndConfirms()
    {
        var directoryPath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "zettatrade-export-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directoryPath);
        var excerpt = System.IO.Path.Combine(directoryPath, "excerpt.txt");
        try
        {
            using var directory = new TempLogDirectory();
            var file = directory.WriteFile(
                "zettatrade-20261108.log",
                Line("INF", "App.Start", "a"),
                Line("INF", "Exchange.Adapter", "b"),
                Line("WRN", "Exchange.Adapter", "c"));

            using var service = await StartedAsync(directory.DirectoryPath);

            var viewModel = new LogViewerViewModel(
                service,
                new TestLocalization(),
                NullLogger<LogViewerViewModel>.Instance);
            viewModel.SourceFilter = "Exchange";

            using var lane = viewModel.AttachLiveTail();
            await viewModel.LoadCommand.Execute(RxVoid.Default);
            await AvaloniaDispatcherPump.DrainAsync();

            viewModel.ExportPathRequested.RegisterHandler(context =>
            {
                context.SetOutput(excerpt);
                return Task.CompletedTask;
            });

            await viewModel.ExportCommand.Execute(RxVoid.Default);
            await AvaloniaDispatcherPump.DrainAsync();

            _ = file;
            var content = await File.ReadAllLinesAsync(excerpt);
            content.Should().HaveCount(2, "only the matching rows land on the disk");
            content.Should().Contain(line => line.Contains(" b"), "the source filter's b and c rows export");
            content.Should().NotContain(line => line.Contains(" a"), "the App.Start row never matches the source filter");
            viewModel.StatusNote.Should().StartWith("[Log.Excerpt.Saved(2|");
            viewModel.ErrorMessage.Should().BeNull();
        }
        finally
        {
            try
            {
                Directory.Delete(directoryPath, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
                // The temp workspace's own cleanup race path.
            }
        }
    }

    /// <summary>A cancelled pick exports nothing and stays silent.</summary>
    [Fact]
    public async Task ACancelledPick_ExportsNothing()
    {
        using var directory = new TempLogDirectory();
        directory.WriteFile(
            "zettatrade-20261108.log",
            Line("INF", "App.Start", "a"));

        using var service = await StartedAsync(directory.DirectoryPath);

        var viewModel = new LogViewerViewModel(
            service,
            new TestLocalization(),
            NullLogger<LogViewerViewModel>.Instance);
        var asked = 0;
        viewModel.ExportPathRequested.RegisterHandler(context =>
        {
            Interlocked.Increment(ref asked);
            context.SetOutput(null);
            return Task.CompletedTask;
        });

        using var lane = viewModel.AttachLiveTail();
        await viewModel.LoadCommand.Execute(RxVoid.Default);
        await AvaloniaDispatcherPump.DrainAsync();

        await viewModel.ExportCommand.Execute(RxVoid.Default);
        await AvaloniaDispatcherPump.DrainAsync();

        asked.Should().Be(1);
        viewModel.StatusNote.Should().BeNull("a cancelled pick exports nothing and says nothing");
        viewModel.ErrorMessage.Should().BeNull();
    }

    /// <summary>A failed write surfaces on the error line; the surface stays usable.</summary>
    [Fact]
    public async Task AFailedWrite_SurfacesTheErrorLine()
    {
        using var directory = new TempLogDirectory();
        directory.WriteFile(
            "zettatrade-20261108.log",
            Line("INF", "App.Start", "a"));

        using var service = await StartedAsync(directory.DirectoryPath);

        var viewModel = new LogViewerViewModel(
            service,
            new TestLocalization(),
            NullLogger<LogViewerViewModel>.Instance);
        viewModel.ExportPathRequested.RegisterHandler(context =>
        {
            // A DIRECTORY path: every write faults — the picker never filters
            // a directory away, so the export's own error line is the honest
            // answer.
            context.SetOutput(directory.DirectoryPath);
            return Task.CompletedTask;
        });

        using var lane = viewModel.AttachLiveTail();
        await viewModel.LoadCommand.Execute(RxVoid.Default);
        await AvaloniaDispatcherPump.DrainAsync();

        await viewModel.ExportCommand.Execute(RxVoid.Default);
        await AvaloniaDispatcherPump.DrainAsync();

        viewModel.HasErrorMessage.Should().BeTrue();
        viewModel.ErrorMessage.Should().StartWith("[Log.Excerpt.Failed(");
        viewModel.StatusNote.Should().BeNull();
        viewModel.Rows.Should().ContainSingle("the failed write left the surface usable");
    }

    #endregion

    #region Tests: the evictions

    /// <summary>The ring's evictions mirror onto the surface's head; the surface never outgrows the ring.</summary>
    [Fact]
    public async Task TheEvictions_MirrorOntoTheSurface()
    {
        using var directory = new TempLogDirectory();

        var path = System.IO.Path.Combine(directory.DirectoryPath, "zettatrade-20261108.log");
        var firstLineMoment = new DateTimeOffset(2026, 11, 8, 9, 0, 0, TimeSpan.Zero);

        // The seed arithmetic stays honest: every line stamps ONE millisecond
        // after its predecessor, so every value record is distinct (the seed /
        // attach value-identity dedupe must not collapse this fact) and the
        // compact 41-byte shape keeps the file (30 000 lines = 1 230 000
        // bytes) past the backlog's 1 MB window: the ring seeds its ~25 500
        // line tail, and one capacity-sized burst overflows into evictions.
        using (var writer = new StreamWriter(path, append: false))
        {
            for (var i = 0; i < 30_000; i++)
            {
                writer.Write(
                    firstLineMoment.AddMilliseconds(i)
                        .ToString("yyyy-MM-dd HH:mm:ss.fff zzz", System.Globalization.CultureInfo.InvariantCulture)
                    + " [INF] S " + Environment.NewLine);
            }
        }

        using var service = new LogTailService(NullLogger<LogTailService>.Instance, directory.DirectoryPath, TestPollInterval);
        await service.InitializeAsync();

        var viewModel = new LogViewerViewModel(
            service,
            new TestLocalization(),
            NullLogger<LogViewerViewModel>.Instance);

        using var lane = viewModel.AttachLiveTail();
        await viewModel.LoadCommand.Execute(RxVoid.Default);
        await AvaloniaDispatcherPump.DrainAsync();

        var seededRows = viewModel.Rows.Count;

        var burst = Enumerable.Range(0, LogTailService.BackpressureChannelCapacity)
            .Select(i => Line("WRN", "Burst", $"burst line {i}"));
        System.IO.File.AppendAllText(path, string.Concat(burst));

        await Task.Delay(TestPollInterval * 10);
        await AvaloniaDispatcherPump.DrainAsync();
        await Task.Delay(TestPollInterval * 5);
        await AvaloniaDispatcherPump.DrainAsync();

        viewModel.Rows.Count.Should().Be(
            LogTailService.MaxHistoryLines,
            "the surface holds the ring's filtered projection — seeds minus evictions plus the whole burst");
        (seededRows + LogTailService.BackpressureChannelCapacity)
            .Should().BeGreaterThan(LogTailService.MaxHistoryLines, "the burst actually overflows the ring");
        viewModel.LinesLabel.Should().Be($"[Log.Lines({LogTailService.MaxHistoryLines})]");
        viewModel.Rows[^1].Line.Message.Should().Be(
            $"burst line {LogTailService.BackpressureChannelCapacity - 1}",
            "the staging's newest line is the surface's tail");
    }

    #endregion

    #region Harness

    /// <summary>Build one Serilog-shaped line (terminated).</summary>
    private static string Line(string level, string source, string message) =>
        "2026-11-08 09:00:00.000 +00:00 [" + level + "] " + source + " " + message + Environment.NewLine;

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

    /// <summary>A bracketed-key localization fake (canon across the VM facts).</summary>
    private sealed class TestLocalization : ILocalizationManager
    {
        public string CurrentLanguage => "en";

        public IReadOnlyList<LanguageInfo> AvailableLanguages { get; } = [];

        public IObservable<string> LanguageChanged => throw new NotSupportedException();

        public Task SetLanguageAsync(
            string cultureCode,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public string GetString(string key) => GetString(key, []);

        public string GetString(string key, params object[] args) =>
            args.Length == 0 ? $"[{key}]" : $"[{key}({string.Join('|', args)})]";

        public IObservable<string> GetObservable(string key) => throw new NotSupportedException();
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
                "zettatrade-viewer-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DirectoryPath);
        }

        internal string DirectoryPath { get; }

        internal string WriteFile(string name, params string[] lines)
        {
            var path = System.IO.Path.Combine(DirectoryPath, name);
            File.WriteAllText(path, string.Concat(lines));

            return path;
        }

        internal void Append(string path, params string[] lines) =>
            File.AppendAllText(path, string.Concat(lines));

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

    #endregion
}
