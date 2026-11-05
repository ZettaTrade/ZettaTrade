namespace Livery.Themes;

using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;

/// <summary>
/// Watches the built-in and the user themes directories for theme file
/// changes and publishes debounced reload requests. Renames publish the new
/// path (editors often save by renaming a temp file over the theme); deleted
/// files are never published, so a deletion keeps the last loaded version.
/// </summary>
public sealed class ThemeFileWatcher : IDisposable
{
    #region Constants

    /// <summary>Quiet period required before a burst of file events is published.</summary>
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(DebounceMilliseconds);

    /// <summary>Length of the debounce quiet period in milliseconds.</summary>
    private const int DebounceMilliseconds = 150;

    #endregion Constants

    #region Private Fields

    private readonly ILogger _logger;
    private readonly IScheduler _scheduler;
    private readonly Subject<ThemeFileChange> _changes = new();
    private readonly List<FileSystemWatcher> _watchers = new();
    private bool _disposed;

    #endregion Private Fields

    /// <summary>
    /// Watch both theme directories. A missing directory is skipped with a
    /// warning instead of throwing, so a broken deployment keeps the theme
    /// manager usable and only loses hot-reload.
    /// </summary>
    public ThemeFileWatcher(
        ILogger logger,
        string builtInDirectory,
        string userDirectory,
        IScheduler? scheduler = null)
    {
        _logger = logger;
        _scheduler = scheduler ?? Scheduler.Default;
        Watch(builtInDirectory, isBuiltIn: true);
        Watch(userDirectory, isBuiltIn: false);
    }

    /// <summary>Debounced stream of changed theme files.</summary>
    public IObservable<ThemeFileChange> FileChanged => _changes
        .Throttle(DebounceDelay, _scheduler);

    #region IDisposable

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }

        _watchers.Clear();
        _changes.Dispose();
    }

    #endregion IDisposable

    #region Private Methods

    private void Watch(
        string directory,
        bool isBuiltIn)
    {
        if (!Directory.Exists(directory))
        {
            _logger.LogWarning("Theme hot-reload: directory not found, watching skipped: {Directory}", directory);
            return;
        }

        var watcher = new FileSystemWatcher(directory, "*.json")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
        };
        watcher.Created += (_, e) => Publish(e.FullPath, isBuiltIn);
        watcher.Changed += (_, e) => Publish(e.FullPath, isBuiltIn);
        watcher.Renamed += (_, e) => Publish(e.FullPath, isBuiltIn);
        watcher.Error += (_, e) =>
            _logger.LogWarning(e.GetException(), "Theme hot-reload watcher error in {Directory}", directory);
        watcher.EnableRaisingEvents = true;
        _watchers.Add(watcher);
    }

    private void Publish(
        string filePath,
        bool isBuiltIn)
    {
        if (_disposed) return;
        _changes.OnNext(new ThemeFileChange(filePath, isBuiltIn));
    }

    #endregion Private Methods
}

/// <summary>
/// One debounced theme file event: the file path and whether it came from the
/// shipped built-in theme directory or the user themes directory.
/// </summary>
public sealed record ThemeFileChange(
    string FilePath,
    bool IsBuiltIn);
