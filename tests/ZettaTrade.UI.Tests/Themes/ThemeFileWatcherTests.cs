namespace ZettaTrade.UI.Tests.Themes;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using FluentAssertions;
using Livery.Themes;
using Microsoft.Extensions.Logging;
using Xunit;

/// <summary>
/// The watcher restart behavior (BUG-046): a watcher stream error — an
/// internal buffer overflow permanently stops the current
/// <see cref="System.IO.FileSystemWatcher"/>'s raise loop — must not end the
/// pipeline: the watch re-subscribes a fresh source per lap and the restart
/// is logged. The FileSystemWatcher bridge itself runs against the real
/// file system in the ThemeManager hot-reload tests, so these pins drive a
/// controlled source factory over the restart laps.
/// </summary>
public sealed class ThemeFileWatcherTests
{
    [Fact]
    public async Task RestartForever_AnErrorRewatchesAndLogsTheRestart()
    {
        var logger = new RecordingLogger();
        var laps = 0;
        var firstLap = new Subject<ThemeFileChange>();
        var received = new List<ThemeFileChange>();

        IObservable<ThemeFileChange> WatchOne()
        {
            laps++;
            return laps == 1
                ? firstLap
                : Observable.Return(new ThemeFileChange("restarted.json", IsBuiltIn: false));
        }

        // The real path: ThemeFileWatcher.FileChanged subscribes this chain
        // once; each internal lap is a fresh FileSystemWatcher.
        using var subscription = ThemeFileWatcher.RestartForever(WatchOne, "themes", logger).Subscribe(received.Add);

        firstLap.OnNext(new ThemeFileChange("night-shade.json", IsBuiltIn: true));
        received.Should().ContainSingle(t => t.FilePath == "night-shade.json");

        // The watcher dies (buffer overflow, root deletion): one lap ends,
        // the next lap starts with a fresh source, and the failure is logged
        // — a silent watcher death is exactly what counted as the bug.
        firstLap.OnError(new InvalidOperationException("watcher buffer overflow"));

        await WaitUntilAsync(() => received.Count >= 2);
        received.Skip(1).Should().ContainSingle(t => t.FilePath == "restarted.json")
            .Which.IsBuiltIn.Should().BeFalse();
        logger.Warnings.Should().Contain(w => w.Contains("themes"));
    }

    /// <summary>
    /// Wait (bounded) until the restarted lap has subscribed and published;
    /// the resubscription happens on the error thread and the value rides a
    /// fresh observable — the assert only needs the settle point.
    /// </summary>
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition() && (DateTime.UtcNow < deadline))
        {
            await Task.Delay(10);
        }

        condition().Should().BeTrue("the restarted watch was expected to publish within the wait window");
    }

    /// <summary>
    /// A raw formatter-recording logger: the restart hint must be visible as
    /// a warning naming the watched directory (the acceptance of the restart
    /// is "logged, not silent").
    /// </summary>
    private sealed class RecordingLogger : ILogger
    {
        public List<string> Warnings { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull =>
            new NullDisposal();

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

        private sealed class NullDisposal : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
