namespace Livery.Themes;

using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Microsoft.Extensions.Logging;

/// <summary>
/// Describes the watch over the built-in and the user themes directories
/// for theme file changes and publishes debounced reload requests. The
/// debounce is keyed PER FILE (BUG-046): each file path throttles
/// separately, so a burst saving different theme files inside the same
/// window reloads every one of them, while a rapid multi-save of ONE file
/// still collapses into a single refresh. Renames publish the new path
/// (editors often save by renaming a temp file over the theme); deleted
/// files are never published, so a deletion keeps the last loaded version.
/// The watch is self-healing: a watcher error (an internal buffer overflow
/// permanently kills the raise loop of the current watcher) logs and
/// re-subscribes a fresh watcher (BUG-046) — or ends quietly when the
/// watched directory itself disappeared.
/// </summary>
public sealed class ThemeFileWatcher
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
    private readonly string _builtInDirectory;
    private readonly string _userDirectory;

    #endregion Private Fields

    /// <summary>
    /// Describe the watch over both theme directories. The
    /// <see cref="FileSystemWatcher"/> instances are created on every
    /// subscription of <see cref="FileChanged"/> (and re-created on every
    /// error by the restart), so a missing directory is skipped with a
    /// warning instead of throwing — a broken deployment keeps the theme
    /// manager usable and only loses hot-reload.
    /// </summary>
    public ThemeFileWatcher(
        ILogger logger,
        string builtInDirectory,
        string userDirectory,
        IScheduler? scheduler = null)
    {
        _logger = logger;
        _builtInDirectory = builtInDirectory;
        _userDirectory = userDirectory;
        _scheduler = scheduler ?? Scheduler.Default;
    }

    /// <summary>
    /// Debounced stream of changed theme files, one publication per changed
    /// file path: the events throttle per file, so a burst over different
    /// files reloads every one of them (BUG-046) instead of collapsing into
    /// the last-changed file.
    /// </summary>
    public IObservable<ThemeFileChange> FileChanged => WatchDirectory(_builtInDirectory, isBuiltIn: true)
        .Merge(WatchDirectory(_userDirectory, isBuiltIn: false))
        .GroupBy(change => change.FilePath)
        .Select(group => group.Throttle(DebounceDelay, _scheduler))
        .Merge();

    #region Watch Construction

    /// <summary>
    /// The (re-)subscribeable event stream of one watched directory: every
    /// subscription re-checks the directory and creates a fresh watcher;
    /// an error is logged as the restart hint and re-raised, which makes
    /// <see cref="RestartForever"/> subscribe the next lap.
    /// </summary>
    private IObservable<ThemeFileChange> WatchDirectory(
        string directory,
        bool isBuiltIn) =>
        RestartForever(
            () =>
            {
                if (!Directory.Exists(directory))
                {
                    _logger.LogWarning("Theme hot-reload: directory not found, watching skipped: {Directory}", directory);
                    return Observable.Empty<ThemeFileChange>();
                }

                return WatcherEvents(directory, isBuiltIn, _logger);
            },
            directory,
            _logger);

    /// <summary>
    /// The FileSystemWatcher event bridge of one directory: Created, Changed
    /// and Renamed publish a <see cref="ThemeFileChange"/>, an Error ends the
    /// stream with the OS reason. The internal buffer of the watcher can
    /// overflow during a burst of saves and the watcher stops raising events
    /// afterwards — surfacing the error through the stream is the only way
    /// the restart can recover it.
    /// </summary>
    private static IObservable<ThemeFileChange> WatcherEvents(
        string directory,
        bool isBuiltIn,
        ILogger logger) =>
        Observable.Create<ThemeFileChange>(observer =>
        {
            FileSystemWatcher? watcher = null;
            try
            {
                watcher = new FileSystemWatcher(directory, "*.json")
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
                };

                void Publish(FileSystemEventArgs e) => observer.OnNext(new ThemeFileChange(e.FullPath, isBuiltIn));
                watcher.Created += (_, e) => Publish(e);
                watcher.Changed += (_, e) => Publish(e);
                watcher.Renamed += (_, e) => Publish(e);
                watcher.Error += (_, e) => observer.OnError(e.GetException());
                watcher.EnableRaisingEvents = true;

                // The subscription owns the watcher: disposed on unsubscribe
                // and by the restart's subscription teardown before it builds
                // the next watcher.
                return watcher;
            }
            catch (Exception ex)
            {
                watcher?.Dispose();
                logger.LogError(ex, "Theme hot-reload: watching {Directory} failed to start", directory);
                observer.OnError(ex);
                return Disposable.Empty;
            }
        });

    #endregion Watch Construction

    #region Restart

    /// <summary>
    /// Subscribe <paramref name="watchOne"/> forever: an error ends exactly
    /// one lap, logs as the restart hint and the subscription re-enters the
    /// factory for a fresh watcher (BUG-046 — after an internal buffer
    /// overflow the FileSystemWatcher silently stops raising events, the
    /// re-subscription is the only recovery; logging keeps the failure
    /// visible). A stream that COMPLETES (the directory-gone shortcut) ends
    /// the watch without retrying.
    /// </summary>
    internal static IObservable<ThemeFileChange> RestartForever(
        Func<IObservable<ThemeFileChange>> watchOne,
        string directory,
        ILogger logger) =>
        Observable
            .Defer(watchOne)
            .Catch<ThemeFileChange, Exception>(ex =>
            {
                logger.LogWarning(ex, "Theme hot-reload watcher error, restarting the watch: {Directory}", directory);
                return Observable.Throw<ThemeFileChange>(ex);
            })
            .Retry();

    #endregion Restart
}

/// <summary>
/// One debounced theme file event: the file path and whether it came from the
/// shipped built-in theme directory or the user themes directory.
/// </summary>
public sealed record ThemeFileChange(
    string FilePath,
    bool IsBuiltIn);
