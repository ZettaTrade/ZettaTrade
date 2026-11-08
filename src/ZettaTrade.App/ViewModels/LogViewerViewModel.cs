namespace ZettaTrade.App.ViewModels;

using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Livery.Extensions;
using Livery.Localization;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ZettaTrade.App.Services;
/// <summary>The half-day-agnostic status byte ReactiveUI.Primitives names RxVoid.</summary>
using RxVoid = ReactiveUI.Primitives.RxVoid;

/// <summary>
/// The log viewer window's view model (T-101, transient per open): the
/// window activation attaches the tail's live slice lane FIRST and seeds
/// the view from the ring's history snapshot right behind it (the
/// attach / seed + value-identity dedupe discipline — slices crossing the
/// attach render exactly once). The surface is a fixed-capacity
/// virtualized list: the ring's evictions arrive in every slice, the
/// drained batches apply as single multi-element collection events, so
/// tens of thousands of tail lines never storm the list.
/// <para>
/// Level and source filters rebuild the bound surface from the viewer's
/// own ring mirror synchronously (the mirror receives every slice the
/// service publishes, so a filter flip never re-reads the disk). The
/// export hands the user-picked path to <see cref="LogExcerptWriter"/>
/// with the newest bounded slice of the matching rows — redacted lines
/// only, by construction.
/// </para>
/// </summary>
public sealed class LogViewerViewModel : ReactiveObject
{
    #region Resx Keys

    /// <summary>The level-label key stem every mapped level reads from.</summary>
    private const string LevelKeyStem = "Log.Level.";

    /// <summary>The "{0} lines" count label.</summary>
    private const string LinesKey = "Log.Lines";

    /// <summary>The "{0}" current-log-file label.</summary>
    private const string TailFileKey = "Log.Tail.File";

    /// <summary>The no-log-file-yet label.</summary>
    private const string TailFileNoneKey = "Log.Tail.NoFile";

    /// <summary>The "{0} lines exported to {1}" export-confirm recipe.</summary>
    private const string ExcerptSavedKey = "Log.Excerpt.Saved";

    /// <summary>The "{0}" export fault recipe.</summary>
    private const string ExcerptFailedKey = "Log.Excerpt.Failed";

    #endregion Resx Keys

    #region Constants

    /// <summary>The minimum level each filter slot shows (index 0 shows everything).</summary>
    private static readonly IReadOnlyList<LogLevel?> LevelFilterThresholds =
    [
        null,
        LogLevel.Information,
        LogLevel.Warning,
        LogLevel.Error,
    ];

    /// <summary>The export's bound: at most this many rows (the newest slice
    /// of the matching surface) land in one excerpt file.</summary>
    internal const int MaxExcerptLines = 2_000;

    #endregion Constants

    #region Private Fields

    private readonly LogTailService _tail;
    private readonly ILocalizationManager _localization;
    private readonly ILogger<LogViewerViewModel> _logger;

    /// <summary>The ring mirror: every slice the service published, evictions
    /// mirrored too — the filters read this, never the disk.</summary>
    private readonly List<LogViewerRow> _ring = new(LogTailService.MaxHistoryLines);

    /// <summary>The attach/seed dedupe: lines already rendered once through
    /// the seed or a slice never render twice (value identity — the record
    /// equality IS the line identity).</summary>
    private readonly HashSet<LogLine> _seeded = new();

    /// <summary>Whether the seed ran; slices drain only after it (an event
    /// crossing the attach either lies in the snapshot or re-arrives —
    /// nothing is lost, nothing is doubled).</summary>
    private bool _isSeeded;

    private IDisposable? _laneSubscription;

    private int _levelFilterIndex;
    private string _sourceFilter = string.Empty;
    private string? _statusNote;
    private string? _errorMessage;
    private string _linesLabel = string.Empty;
    private string _tailFileLabel = string.Empty;

    #endregion Private Fields

    #region Properties

    /// <summary>The bound surface (the filter's projection of the ring mirror).</summary>
    public LogViewerRowList Rows { get; } = new();

    /// <summary>Whether the surface carries anything to show (the empty state's flag).</summary>
    public bool HasRows { get; private set; }

    /// <summary>The chosen min-level filter slot (0 = everything; see
    /// <see cref="LevelFilterThresholds"/>).</summary>
    public int LevelFilterIndex
    {
        get => _levelFilterIndex;
        set
        {
            if (LevelFilterIndex == value)
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref _levelFilterIndex, value);
            RebuildSurface();
        }
    }

    /// <summary>The source-contains text (exact text, case-insensitive; empty shows everything).</summary>
    public string SourceFilter
    {
        get => _sourceFilter;
        set
        {
            if (SourceFilter == value)
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref _sourceFilter, value);
            RebuildSurface();
        }
    }

    /// <summary>Whether any filter narrows the surface (the hint's flag).</summary>
    public bool IsFiltered => LevelFilterIndex > 0 || SourceFilter.Length > 0;

    /// <summary>The composed "{0} lines" label (dynamic data — recomposed per change like the level names).</summary>
    public string LinesLabel
    {
        get => _linesLabel;
        private set => this.RaiseAndSetIfChanged(ref _linesLabel, value);
    }

    /// <summary>The composed current-log-file label (composed at the seed).</summary>
    public string TailFileLabel
    {
        get => _tailFileLabel;
        private set => this.RaiseAndSetIfChanged(ref _tailFileLabel, value);
    }

    /// <summary>Last export's confirmation line.</summary>
    public string? StatusNote
    {
        get => _statusNote;
        private set
        {
            if (StatusNote == value)
            {
                return;
            }

            _statusNote = value;
            this.RaisePropertyChanged(nameof(StatusNote));
            this.RaisePropertyChanged(nameof(HasStatusNote));
        }
    }

    /// <summary>True while <see cref="StatusNote"/> holds text.</summary>
    public bool HasStatusNote => _statusNote is not null;

    /// <summary>Last export failure, for the footer error line.</summary>
    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (ErrorMessage == value)
            {
                return;
            }

            _errorMessage = value;
            this.RaisePropertyChanged(nameof(ErrorMessage));
            this.RaisePropertyChanged(nameof(HasErrorMessage));
        }
    }

    /// <summary>True while <see cref="ErrorMessage"/> holds text.</summary>
    public bool HasErrorMessage => _errorMessage is not null;

    #endregion Properties

    #region Commands + Interactions

    /// <summary>The path-provider the export command asks; the window answers with the picker (null = cancelled, nothing exported).</summary>
    public Interaction<RxVoid, string?> ExportPathRequested { get; }

    /// <summary>Export the newest bounded slice of the matching rows to the picked file.</summary>
    public ReactiveCommand<RxVoid, RxVoid> ExportCommand { get; }

    /// <summary>Attach the live lane and seed the view once (the window activation drives it).</summary>
    public ReactiveCommand<RxVoid, RxVoid> LoadCommand { get; }

    #endregion Commands + Interactions

    #region Constructor

    /// <summary>
    /// Create the view model: empty until the window activation seeds it.
    /// </summary>
    /// <param name="tail">The tail service (the singleton lane + history ring).</param>
    /// <param name="localization">The localization catalog the labels compose from.</param>
    /// <param name="logger">The view model's own logger.</param>
    public LogViewerViewModel(
        LogTailService tail,
        ILocalizationManager localization,
        ILogger<LogViewerViewModel> logger)
    {
        _tail = tail ?? throw new ArgumentNullException(nameof(tail));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        ExportPathRequested = new Interaction<RxVoid, string?>();
        ExportCommand = ReactiveCommand.CreateFromTask<RxVoid, RxVoid>(
            (_, cancellationToken) => ExportExcerptAsync(cancellationToken));
        LoadCommand = ReactiveCommand.CreateFromTask<RxVoid, RxVoid>(
            (_, cancellationToken) => LoadTailAsync(cancellationToken));
    }

    #endregion Constructor

    #region Lifecycle

    /// <summary>
    /// Attach the live slice lane; the window's activation scope disposes
    /// the return value. Slices arriving BEFORE the seed finish change
    /// nothing (the seed's snapshot either already holds them or they
    /// re-arrive — the lane drops nothing that got lost).
    /// </summary>
    /// <returns>The lane subscription.</returns>
    public IDisposable AttachLiveTail() => _laneSubscription = _tail.Slices
        .ObserveOnUiThread()
        .Subscribe(
            ApplySlice,
            ex => _logger.LogError(ex, "The log tail's slice lane stopped unexpectedly"));

    /// <summary>
    /// Seed the view from the ring's history snapshot (the attach already
    /// ran): every snapshot line renders once — lines a crossing slice
    /// already premuted skip by the value-identity guard.
    /// </summary>
    /// <param name="cancellationToken">The load command's token.</param>
    private async Task<RxVoid> LoadTailAsync(CancellationToken cancellationToken)
    {
        var snapshot = await _tail.HistorySnapshotAsync(cancellationToken);

        foreach (var line in snapshot)
        {
            if (_seeded.Add(line))
            {
                PushIntoRing(BuildRow(line));
            }
        }

        _isSeeded = true;
        TailFileLabel = ComposeTailFileLabel();

        RebuildSurface();
        return RxVoid.Default;
    }

    #endregion Lifecycle

    #region Slice Application

    /// <summary>
    /// Apply one drained slice onto the mirror and the bound surface: the
    /// evicted lines leave the surface's head, the added ones (skipped when
    /// already rendered) append. The surface carries only matching rows, so
    /// the eviction count lands on its head exactly.
    /// </summary>
    /// <param name="slice">The drained slice (added, evicted).</param>
    private void ApplySlice(LogTailSlice slice)
    {
        if (!_isSeeded)
        {
            return;
        }

        foreach (var evicted in slice.Evicted)
        {
            _seeded.Remove(evicted);
        }

        // The mirror drops EXACTLY the lines the service ring evicted (each
        // slice carries them in FIFO order, so the mirror's head span is
        // theirs by construction); the surface removes only the matching
        // head span — its own filtered content (a filter flip cannot
        // interleave: the surface mutates only on the UI thread).
        if (slice.Evicted.Count > 0)
        {
            _ring.RemoveRange(0, slice.Evicted.Count);

            var evictedMatchingCount = CountMatching(slice.Evicted);
            if (evictedMatchingCount > 0)
            {
                Rows.RemoveFromFront(evictedMatchingCount);
            }
        }

        List<LogViewerRow> appended = new();
        foreach (var line in slice.Appended)
        {
            if (!_seeded.Add(line))
            {
                continue;
            }

            var row = BuildRow(line);
            PushIntoRing(row);
            if (MatchesFilter(row))
            {
                appended.Add(row);
            }
        }

        if (_ring.Count > LogTailService.MaxHistoryLines)
        {
            // The ring mirror must never outgrow the service's ring: the
            // slices carry the evictions; this valve only guards the seed's
            // initial bulk (already capped) against a stream the events
            // could miscount. When it trips the surface re-derives from the
            // mirror whole — one Reset event, never a silent desync.
            foreach (var (row, line) in PopOverflowRows(_ring.Count - LogTailService.MaxHistoryLines))
            {
                _seeded.Remove(line);
            }

            RebuildSurface();
            return;
        }

        Rows.AppendBatch(appended);
        RefreshCounters();
    }

    /// <summary>Pop the mirror's oldest rows (the test harness's bulk-safety valve).</summary>
    private IEnumerable<(LogViewerRow Row, LogLine Line)> PopOverflowRows(int count)
    {
        for (var i = 0; i < count && _ring.Count > 0; i++)
        {
            var row = _ring[0];
            _ring.RemoveAt(0);
            yield return (row, row.Line);
        }
    }

    private void PushIntoRing(LogViewerRow row) => _ring.Add(row);

    #endregion Slice Application

    #region Filtering

    /// <summary>The current filter's predicate.</summary>
    private bool MatchesFilter(LogViewerRow row)
    {
        var minLevel = LevelFilterThresholds[LevelFilterIndex];
        if (minLevel is { } threshold && row.Line.Level < threshold)
        {
            return false;
        }

        if (_sourceFilter.Length > 0 &&
            !row.Line.Source.Contains(_sourceFilter, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    /// <summary>Count the matching rows inside one slice's list.</summary>
    private int CountMatching(IReadOnlyList<LogLine> lines)
    {
        var count = 0;

        foreach (var line in lines)
        {
            if (MatchesFilter(BuildRow(line)))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Rebuild the bound surface from the ring mirror (the filter flips and the seed both take this).</summary>
    private void RebuildSurface()
    {
        List<LogViewerRow> matching = new();
        foreach (var row in _ring)
        {
            if (MatchesFilter(row))
            {
                matching.Add(row);
            }
        }

        Rows.Reset(matching);
        RefreshCounters();
    }

    private void RefreshCounters()
    {
        HasRows = Rows.Count > 0;
        this.RaisePropertyChanged(nameof(HasRows));
        LinesLabel = _localization.GetString(LinesKey, Rows.Count);
    }

    #endregion Filtering

    #region Export

    private async Task<RxVoid> ExportExcerptAsync(CancellationToken cancellationToken)
    {
        StatusNote = null;
        ErrorMessage = null;

        var path = await ExportPathRequested.Handle(RxVoid.Default);
        if (string.IsNullOrWhiteSpace(path))
        {
            // A cancelled pick exports nothing (and says nothing).
            return RxVoid.Default;
        }

        List<LogViewerRow> matching = [];
        foreach (var row in _ring)
        {
            if (MatchesFilter(row))
            {
                matching.Add(row);
            }
        }

        // The newest bounded slice: the excerpt proves the filter matrix's
        // tail, never an unbounded dump.
        var start = Math.Max(0, matching.Count - MaxExcerptLines);
        var bounded = matching.Count > MaxExcerptLines
            ? matching.GetRange(start, MaxExcerptLines)
            : matching;

        try
        {
            await LogExcerptWriter.WriteAsync(path, bounded, cancellationToken);
            StatusNote = _localization.GetString(ExcerptSavedKey, bounded.Count, path);
        }
        catch (Exception ex)
        {
            // Broader than IO: the picker's path can name anything — the
            // failed write surfaces on the error line and the surface stays
            // usable (the same posture the notification toggles keep).
            _logger.LogError(ex, "Log viewer: the excerpt export failed to write");
            ErrorMessage = _localization.GetString(ExcerptFailedKey, ex.Message);
        }

        return RxVoid.Default;
    }

    #endregion Export

    #region Row Building

    private LogViewerRow BuildRow(LogLine line) => new(
        line,
        Moment: line.Level is LogLevel.None
            ? string.Empty
            : line.Moment.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture),
        LevelName: line.Level is LogLevel.None
            ? string.Empty
            : _localization.GetString(LevelKeyStem + Enum.GetName(line.Level)));

    private string ComposeTailFileLabel()
    {
        var file = _tail.TailFile;
        return file is not null
            ? _localization.GetString(TailFileKey, file)
            : _localization.GetString(TailFileNoneKey);
    }

    #endregion Row Building
}
