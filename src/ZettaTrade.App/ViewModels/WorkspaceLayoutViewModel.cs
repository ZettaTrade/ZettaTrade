namespace ZettaTrade.App.ViewModels;

using System;
using System.Reactive.Linq;
using System.Threading;
using Avalonia.Controls;
using Livery.Contracts;
using Livery.Extensions;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ZettaTrade.App.Services;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.UI.Extensions;
// RxVoid rides an alias (not a plain ReactiveUI.Primitives import): plain both
// imports would make every RxVoid Subscribe ambiguous against System's one.
using RxVoid = ReactiveUI.Primitives.RxVoid;

/// <summary>
/// ViewModel owning the persisted column geometry of the main window's
/// draggable splitters (the chart/book, book/tape, tape/watchlist and
/// book/assistant pairs).
/// The column widths are persisted state, mirroring the
/// <see cref="ChatViewModel"/> visibility discipline: the startup walk
/// restores <c>UI.BookPanelWidth</c> / <c>UI.AiPanelWidth</c> before any
/// window opens (no relayout flash), a settled drag persists once per burst
/// (the debounce keeps a drag from writing the settings file per pixel),
/// the restore and the changes-stream echo never rewrite the value they just
/// applied, and external writers apply live.
/// The assistant column additionally follows the panel visibility: a hidden
/// panel collapses its column and its splitter track to zero width (no dead
/// grab handle parked in an empty gutter) and the persisted width returns
/// untouched on the next show.
/// The detachable panels (T-103) mirror the runtime's detached state from
/// the <see cref="IDetachedPanelCoordinator"/> and drive the View menu's
/// detach/attach toggles through it; a hidden assistant that still lives in
/// its own window attaches back first, so the collapse keeps one truth —
/// the panel is inline whenever its column collapses.
/// </summary>
public sealed class WorkspaceLayoutViewModel : ReactiveObject, IAsyncInitializable
{
    #region Constants

    /// <summary>Width of the fixed splitter track columns (the bound AI-side track swings between this and zero).</summary>
    private const double SplitterThickness = 8;

    /// <summary>
    /// How much width difference counts as "same" (a float round-trip through
    /// layout rounding never escalates into a settings rewrite).
    /// </summary>
    private const double WidthSameEpsilon = 0.01;

    /// <summary>
    /// How long the persistence waits after the last drag move before it
    /// writes: one write per settled drag burst instead of one per pixel.
    /// </summary>
    private static readonly TimeSpan DragQuietTime = TimeSpan.FromMilliseconds(250);

    /// <summary>The fixed splitter track as the grid expects it.</summary>
    private static readonly GridLength SplitterTrack = new(SplitterThickness);

    /// <summary>The zero-width shape of the collapsed assistant geometry.</summary>
    private static readonly GridLength Collapsed = new(0);

    #endregion

    #region Private Fields

    private readonly ISettingsService _settings;
    private readonly ILogger<WorkspaceLayoutViewModel> _logger;

    /// <summary>The detached-panel runtime the mirror reflects and the View menu's toggle routes through (T-103).</summary>
    private readonly IDetachedPanelCoordinator _detacher;

    /// <summary>
    /// The book width the detach captures and a later attach restores (the
    /// user-visible truth — drags may outpace the debounced persistence, so
    /// the capture, not any persisted value, is what a show puts back).
    /// </summary>
    private GridLength _bookWidthAtDetach = new(UiSettingsKeys.DefaultBookPanelWidth);

    /// <summary>
    /// The width the book-side ViewModel last applied to the bound column.
    /// Programmatic writes (restore, detach, attach, external echoes) update
    /// this BEFORE the property changes, so the persistence pipeline filters
    /// them out and only real splitter drags push differences through. The
    /// detached book's collapsed shape rides the same guard.
    /// </summary>
    private GridLength _appliedBookDisplayWidth = new(UiSettingsKeys.DefaultBookPanelWidth);

    /// <summary>Whether the book-side display geometry currently shows the panel (drives the detach capture).</summary>
    private bool _bookColumnShown = true;

    /// <summary>The applied persisted tape width. The persistence pipeline only fires on a real difference against this cache, so both the startup restore and the changes-stream echo are absorbed without a settings rewrite.</summary>
    private double _persistedTapeWidth = UiSettingsKeys.DefaultTapePanelWidth;

    /// <summary>See <see cref="_persistedTapeWidth"/>; the watchlist column's own cache.</summary>
    private double _persistedWatchlistWidth = UiSettingsKeys.DefaultWatchlistPanelWidth;

    /// <summary>
    /// The book width the user saw before a hide (drags may outpace the
    /// debounced persistence, so the capture — not the persisted value — is
    /// what a show restores).
    /// </summary>
    private GridLength _aiWidthAtHide = new(UiSettingsKeys.DefaultAiPanelWidth);

    /// <summary>
    /// The width the assistant-side ViewModel last applied to the bound
    /// column. Programmatic writes (restore, hide, show, external echoes)
    /// update this BEFORE the property changes, so the persistence pipeline
    /// filters them out and only real splitters drag differences through.
    /// </summary>
    private GridLength _appliedAiDisplayWidth = new(UiSettingsKeys.DefaultAiPanelWidth);

    /// <summary>Whether the assistant-side display geometry currently shows the panel (drives the hide capture).</summary>
    private bool _aiColumnShown = true;

    private GridLength _bookColumnWidth = new(UiSettingsKeys.DefaultBookPanelWidth);
    private GridLength _bookSplitterColumnWidth = SplitterTrack;
    private double _bookColumnMinWidth = UiSettingsKeys.MinBookPanelWidth;
    private GridLength _tapeColumnWidth = new(UiSettingsKeys.DefaultTapePanelWidth);
    private GridLength _watchlistColumnWidth = new(UiSettingsKeys.DefaultWatchlistPanelWidth);
    private GridLength _aiColumnWidth = new(UiSettingsKeys.DefaultAiPanelWidth);
    private double _aiColumnMinWidth = UiSettingsKeys.MinAiPanelWidth;
    private GridLength _aiSplitterColumnWidth = SplitterTrack;
    private bool _isChartDetached;
    private bool _isBookDetached;
    private bool _isAssistantDetached;

    #endregion

    #region Constructor

    /// <summary>Create a new WorkspaceLayoutViewModel instance.</summary>
    /// <param name="settings">The app settings service; the startup walk
    /// restores both widths from it, a settled drag persists there and the
    /// changes stream carries external writers.</param>
    /// <param name="chat">The chat ViewModel whose <see cref="ChatViewModel.IsAiChatVisible"/>
    /// drives the assistant column collapse: the subscription runs in the
    /// ctor so a visibility restore (the chat walks first — registration
    /// order) applies the collapsed geometry in the same step.</param>
    /// <param name="detacher">The detached-panel runtime the mirror reflects
    /// and the View menu's toggle routes through (T-103).</param>
    /// <param name="logger">The class logger.</param>
    public WorkspaceLayoutViewModel(
        ISettingsService settings,
        ChatViewModel chat,
        IDetachedPanelCoordinator detacher,
        ILogger<WorkspaceLayoutViewModel> logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        chat = chat ?? throw new ArgumentNullException(nameof(chat));
        _detacher = detacher ?? throw new ArgumentNullException(nameof(detacher));

        // The detach mirror (T-103): the View menu's checkboxes bind these
        // properties; the runtime's own emits (every detach leg including
        // the restore) drive them. The runtime emits on the UI thread, and
        // ObserveOnUiThread keeps the hop explicit — the mirror never
        // crosses threads.
        IsChartDetached = _detacher.IsDetached(PanelDetachId.Chart);
        IsBookDetached = _detacher.IsDetached(PanelDetachId.Book);
        IsAssistantDetached = _detacher.IsDetached(PanelDetachId.Assistant);

        // The book column frees with its panel (T-173): a book already
        // detached at construction (the restore walk re-opened its window
        // before the layout resolved) starts with the collapsed geometry
        // from the first frame; the runtime's later emits drive the same
        // flip. The collapsed writes run before the persistence pipelines
        // subscribe, and their display lead keeps the restore silent too.
        if (IsBookDetached)
        {
            HideBookColumn();
        }

        _detacher.PanelDetached
            .ObserveOnUiThread()
            .Subscribe(
                panel => SetDetachMirror(panel, detached: true),
                ex => _logger.LogError(ex, "The detached-panel stream stopped unexpectedly"));
        _detacher.PanelAttached
            .ObserveOnUiThread()
            .Subscribe(
                panel => SetDetachMirror(panel, detached: false),
                ex => _logger.LogError(ex, "The attached-panel stream stopped unexpectedly"));

        // The View menu's detach/attach toggle (T-103): one command, the
        // panel id rides the parameter. The runtime answers every flip;
        // a fault logs through ThrownExceptions, never a silent death.
        ToggleDetachCommand = ReactiveCommand.CreateFromTask<PanelDetachId, RxVoid>(
            async (panel, ct) =>
            {
                await _detacher.ToggleDetachAsync(panel, ct);
                return RxVoid.Default;
            });
        ToggleDetachCommand.ThrownExceptions.Subscribe(
            ex => _logger.LogError(ex, "The detached-panel toggle failed"));

        // The assistant column follows the panel: hidden → the column, its
        // splitter track and its min clamp all collapse to zero; shown → the
        // captured width returns. (The book side has no hidden state.) A
        // hidden panel that still lives in its own window attaches back
        // FIRST (SubscribeAsync keeps the leg task-based): the collapse and
        // the inline note never disagree.
        chat.WhenAnyValue(x => x.IsAiChatVisible)
            .Subscribe(
                visible =>
                {
                    if (visible)
                    {
                        ShowAiColumn();
                    }
                    else
                    {
                        HideAiColumn();
                    }
                },
                ex => _logger.LogError(ex, "The AI panel visibility stream stopped unexpectedly"));
        chat.WhenAnyValue(x => x.IsAiChatVisible)
            .Where(visible => !visible)
            .Where(_ => _detacher.IsDetached(PanelDetachId.Assistant))
            .SubscribeAsync(
                _ => _detacher.AttachPanelWindowAsync(PanelDetachId.Assistant, CancellationToken.None),
                onError: ex => _logger.LogError(ex, "The assistant attach-on-hide leg failed"));

        // External writers apply live: the display lead is updated BEFORE
        // the property changes, so the persistence pipelines see an
        // already-applied value and skip it (the AI side leads the display,
        // the tape and watchlist lead their caches).
        _settings.Changes
            .Where(change => change.Section == UiSettingsKeys.Section &&
                             change.Key == UiSettingsKeys.BookPanelWidth)
            .Select(change => change.NewValue)
            .OfType<double>()
            .Subscribe(
                ApplyExternalBookWidth,
                ex => _logger.LogError(ex, "The book width stream stopped unexpectedly"));

        _settings.Changes
            .Where(change => change.Section == UiSettingsKeys.Section &&
                             change.Key == UiSettingsKeys.AiPanelWidth)
            .Select(change => change.NewValue)
            .OfType<double>()
            .Subscribe(
                width => ApplyExternalAiWidth(width),
                ex => _logger.LogError(ex, "The AI panel width stream stopped unexpectedly"));

        _settings.Changes
            .Where(change => change.Section == UiSettingsKeys.Section &&
                             change.Key == UiSettingsKeys.TapePanelWidth)
            .Select(change => change.NewValue)
            .OfType<double>()
            .Subscribe(
                width =>
                {
                    // The same external-writer discipline as the book side:
                    // the value becomes the tape width truth, the cache
                    // leads the property change, everything is applied live.
                    _persistedTapeWidth = ClampStoredWidth(
                        width,
                        UiSettingsKeys.MinTapePanelWidth,
                        UiSettingsKeys.DefaultTapePanelWidth);
                    TapeColumnWidth = new GridLength(_persistedTapeWidth);
                },
                ex => _logger.LogError(ex, "The tape width stream stopped unexpectedly"));

        _settings.Changes
            .Where(change => change.Section == UiSettingsKeys.Section &&
                             change.Key == UiSettingsKeys.WatchlistPanelWidth)
            .Select(change => change.NewValue)
            .OfType<double>()
            .Subscribe(
                width =>
                {
                    // The same external-writer discipline as the book side:
                    // the value becomes the watchlist width truth, the cache
                    // leads the property change, everything is applied live.
                    _persistedWatchlistWidth = ClampStoredWidth(
                        width,
                        UiSettingsKeys.MinWatchlistPanelWidth,
                        UiSettingsKeys.DefaultWatchlistPanelWidth);
                    WatchlistColumnWidth = new GridLength(_persistedWatchlistWidth);
                },
                ex => _logger.LogError(ex, "The watchlist width stream stopped unexpectedly"));

        // Persist every settled drag (the debounce keeps a drag burst from
        // writing the settings file per pixel; Concat serializes the
        // writes). The BookColumnWidth property is the binding writeback
        // target of the grid's book column, so this pipeline fires exactly
        // on those splitter writes; the restore, the detach collapse and
        // the external echo never cross the difference guard.
        this.WhenAnyValue(x => x.BookColumnWidth)
            .Skip(1)
            .Where(width => !IsSameWidth(width, _appliedBookDisplayWidth))
            .Throttle(DragQuietTime)
            .Select(width => Observable
                .FromAsync(() => _settings.SetAsync(
                    UiSettingsKeys.Section,
                    UiSettingsKeys.BookPanelWidth,
                    ClampStoredWidth(width.Value, UiSettingsKeys.MinBookPanelWidth, UiSettingsKeys.DefaultBookPanelWidth)))
                .Select(_ => width))
            .Concat()
            .Subscribe(
                issuedWidth => _appliedBookDisplayWidth = issuedWidth,
                ex => _logger.LogError(ex, "Failed to persist the book panel width"));

        this.WhenAnyValue(x => x.TapeColumnWidth)
            .Skip(1)
            .Where(width => !IsSameWidth(width, _persistedTapeWidth))
            .Throttle(DragQuietTime)
            .Select(width => Observable
                .FromAsync(() => _settings.SetAsync(
                    UiSettingsKeys.Section,
                    UiSettingsKeys.TapePanelWidth,
                    ClampStoredWidth(width.Value, UiSettingsKeys.MinTapePanelWidth, UiSettingsKeys.DefaultTapePanelWidth)))
                .Select(_ => width))
            .Concat()
            .Subscribe(
                issuedWidth => _persistedTapeWidth = issuedWidth.Value,
                ex => _logger.LogError(ex, "Failed to persist the tape panel width"));

        // The watchlist rides the tape's shape: one bound column, one
        // debounced persistence pipeline, the same difference guard.
        this.WhenAnyValue(x => x.WatchlistColumnWidth)
            .Skip(1)
            .Where(width => !IsSameWidth(width, _persistedWatchlistWidth))
            .Throttle(DragQuietTime)
            .Select(width => Observable
                .FromAsync(() => _settings.SetAsync(
                    UiSettingsKeys.Section,
                    UiSettingsKeys.WatchlistPanelWidth,
                    ClampStoredWidth(
                        width.Value,
                        UiSettingsKeys.MinWatchlistPanelWidth,
                        UiSettingsKeys.DefaultWatchlistPanelWidth)))
                .Select(_ => width))
            .Concat()
            .Subscribe(
                issuedWidth => _persistedWatchlistWidth = issuedWidth.Value,
                ex => _logger.LogError(ex, "Failed to persist the watchlist panel width"));

        this.WhenAnyValue(x => x.AiColumnWidth)
            .Skip(1)
            .Where(width => !IsSameWidth(width, _appliedAiDisplayWidth))
            .Throttle(DragQuietTime)
            .Select(width => Observable
                .FromAsync(() => _settings.SetAsync(
                    UiSettingsKeys.Section,
                    UiSettingsKeys.AiPanelWidth,
                    ClampStoredWidth(width.Value, UiSettingsKeys.MinAiPanelWidth, UiSettingsKeys.DefaultAiPanelWidth)))
                .Select(_ => width))
            .Concat()
            .Subscribe(
                issuedWidth => _appliedAiDisplayWidth = issuedWidth,
                ex => _logger.LogError(ex, "Failed to persist the AI panel width"));
    }

    #endregion

    #region Properties

    /// <summary>
    /// Width of the order book column (logical pixels). The grid's book
    /// column binds it TwoWay: the splitter's dragged widths flow in and
    /// persist through the pipeline once the drag settles.
    /// </summary>
    public GridLength BookColumnWidth
    {
        get => _bookColumnWidth;
        set => this.RaiseAndSetIfChanged(ref _bookColumnWidth, value);
    }

    /// <summary>
    /// Minimum width of the book column: the usable-minimum clamp while the
    /// panel is inline, zero while detached (the collapsed column must be
    /// allowed to be zero). The grid column binds it OneWay.
    /// </summary>
    public double BookColumnMinWidth
    {
        get => _bookColumnMinWidth;
        private set => this.RaiseAndSetIfChanged(ref _bookColumnMinWidth, value);
    }

    /// <summary>
    /// Width of the book side's splitter tracks: the grab handles ride them.
    /// They vanish with the panel so a detached book never leaves a dead
    /// grab handle parked in an empty gutter. Both book-side tracks (the
    /// chart/book and the book/tape boundary) bind it OneWay — with the
    /// panel away, neither boundary has the neighbor it serves.
    /// </summary>
    public GridLength BookSplitterColumnWidth
    {
        get => _bookSplitterColumnWidth;
        private set => this.RaiseAndSetIfChanged(ref _bookSplitterColumnWidth, value);
    }

    /// <summary>
    /// Width of the trade tape column (logical pixels). The grid's tape
    /// column binds it TwoWay: the splitter's dragged widths flow in and
    /// persist through the pipeline once the drag settles.
    /// </summary>
    public GridLength TapeColumnWidth
    {
        get => _tapeColumnWidth;
        set => this.RaiseAndSetIfChanged(ref _tapeColumnWidth, value);
    }

    /// <summary>
    /// Width of the watchlist column (logical pixels). The grid's watchlist
    /// column binds it TwoWay: the splitter's dragged widths flow in and
    /// persist through the pipeline once the drag settles.
    /// </summary>
    public GridLength WatchlistColumnWidth
    {
        get => _watchlistColumnWidth;
        set => this.RaiseAndSetIfChanged(ref _watchlistColumnWidth, value);
    }

    /// <summary>
    /// Width of the AI assistant column (logical pixels) while its panel is
    /// visible; zero while the panel is hidden — the TwoWay grid binding
    /// feeds it from the persisted-setting bindings and the splitters.
    /// </summary>
    public GridLength AiColumnWidth
    {
        get => _aiColumnWidth;
        set => this.RaiseAndSetIfChanged(ref _aiColumnWidth, value);
    }

    /// <summary>
    /// Minimum width of the AI assistant column: the usable-minimum clamp
    /// while the panel is visible, zero while hidden (the collapsed column
    /// must be allowed to be zero). The grid column binds it OneWay.
    /// </summary>
    public double AiColumnMinWidth
    {
        get => _aiColumnMinWidth;
        private set => this.RaiseAndSetIfChanged(ref _aiColumnMinWidth, value);
    }

    /// <summary>
    /// Width of the AI splitter track column: the grab handle rides this.
    /// It vanishes with the panel so a hidden assistant never leaves a dead
    /// grab handle parked in an empty gutter. The grid column binds it OneWay.
    /// </summary>
    public GridLength AiSplitterColumnWidth
    {
        get => _aiSplitterColumnWidth;
        private set => this.RaiseAndSetIfChanged(ref _aiSplitterColumnWidth, value);
    }

    /// <summary>
    /// Whether the chart lives in its own detached window right now (T-103).
    /// The View menu's checkbox binds it; the runtime's own emits drive it.
    /// </summary>
    public bool IsChartDetached
    {
        get => _isChartDetached;
        private set => this.RaiseAndSetIfChanged(ref _isChartDetached, value);
    }

    /// <summary>
    /// Whether the order book lives in its own detached window right now
    /// (T-103). The View menu's checkbox binds it; the runtime's own emits
    /// drive it.
    /// </summary>
    public bool IsBookDetached
    {
        get => _isBookDetached;
        private set => this.RaiseAndSetIfChanged(ref _isBookDetached, value);
    }

    /// <summary>
    /// Whether the assistant lives in its own detached window right now
    /// (T-103). The View menu's checkbox binds it; the runtime's own emits
    /// drive it.
    /// </summary>
    public bool IsAssistantDetached
    {
        get => _isAssistantDetached;
        private set => this.RaiseAndSetIfChanged(ref _isAssistantDetached, value);
    }

    /// <summary>
    /// The View menu's detach/attach toggle (T-103): a detached panel
    /// attaches back inline, an inline one detaches into its own window.
    /// The menu checkboxes bind it with the panel id parameter.
    /// </summary>
    public ReactiveCommand<PanelDetachId, RxVoid> ToggleDetachCommand { get; }

    #endregion

    #region Public Methods

    /// <inheritdoc/>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var book = ClampStoredWidth(
            await _settings.GetAsync<double>(
                UiSettingsKeys.Section,
                UiSettingsKeys.BookPanelWidth,
                UiSettingsKeys.DefaultBookPanelWidth),
            UiSettingsKeys.MinBookPanelWidth,
            UiSettingsKeys.DefaultBookPanelWidth);
        var tape = ClampStoredWidth(
            await _settings.GetAsync<double>(
                UiSettingsKeys.Section,
                UiSettingsKeys.TapePanelWidth,
                UiSettingsKeys.DefaultTapePanelWidth),
            UiSettingsKeys.MinTapePanelWidth,
            UiSettingsKeys.DefaultTapePanelWidth);
        var watchlist = ClampStoredWidth(
            await _settings.GetAsync<double>(
                UiSettingsKeys.Section,
                UiSettingsKeys.WatchlistPanelWidth,
                UiSettingsKeys.DefaultWatchlistPanelWidth),
            UiSettingsKeys.MinWatchlistPanelWidth,
            UiSettingsKeys.DefaultWatchlistPanelWidth);
        var ai = ClampStoredWidth(
            await _settings.GetAsync<double>(
                UiSettingsKeys.Section,
                UiSettingsKeys.AiPanelWidth,
                UiSettingsKeys.DefaultAiPanelWidth),
            UiSettingsKeys.MinAiPanelWidth,
            UiSettingsKeys.DefaultAiPanelWidth);

        // The displays lead the property assignments on purpose: a drag the
        // persistence pipelines would observe sits behind the difference
        // guards, so a restore never rewrites the value it just read.
        _bookWidthAtDetach = new GridLength(book);
        if (_bookColumnShown)
        {
            _appliedBookDisplayWidth = new GridLength(book);
            BookColumnWidth = new GridLength(book);
        }
        else
        {
            // A book already detached at construction: the stored width
            // becomes only the attach-back truth, the column stays
            // collapsed (the re-assignments restate the ctor's geometry).
            _appliedBookDisplayWidth = Collapsed;
            BookColumnWidth = Collapsed;
        }
        _persistedTapeWidth = tape;
        TapeColumnWidth = new GridLength(tape);
        _persistedWatchlistWidth = watchlist;
        WatchlistColumnWidth = new GridLength(watchlist);
        _aiWidthAtHide = new GridLength(ai);
        if (_aiColumnShown)
        {
            _appliedAiDisplayWidth = _aiWidthAtHide;
            AiColumnWidth = _aiWidthAtHide;
        }
        else
        {
            _appliedAiDisplayWidth = Collapsed;
            AiColumnWidth = Collapsed;
        }

        _logger.LogInformation(
            "Restored persisted workspace widths: book {BookWidth:F0}px ({BookState}), tape {TapeWidth:F0}px, watchlist {WatchlistWidth:F0}px, assistant {AiWidth:F0}px ({AiState})",
            book,
            _bookColumnShown ? "inline" : "detached",
            tape,
            watchlist,
            ai,
            _aiColumnShown ? "visible" : "hidden");
    }

    #endregion

    #region AI Column Geometry

    /// <summary>Apply the visible geometry: the captured width comes back untouched.</summary>
    private void ShowAiColumn()
    {
        var restored = new GridLength(
            ClampStoredWidth(_aiWidthAtHide.Value, UiSettingsKeys.MinAiPanelWidth, UiSettingsKeys.DefaultAiPanelWidth));
        _aiWidthAtHide = restored;
        _aiColumnShown = true;
        _appliedAiDisplayWidth = restored;
        AiColumnMinWidth = UiSettingsKeys.MinAiPanelWidth;
        AiSplitterColumnWidth = SplitterTrack;
        AiColumnWidth = restored;
    }

    /// <summary>Apply the collapsed geometry; the capture keeps only a width the user actually saw.</summary>
    private void HideAiColumn()
    {
        if (_aiColumnShown)
        {
            // A drag may still ride the debounce (≤ DragQuietTime old), so
            // the displayed value — not the persisted cache — is the truth
            // a later show must restore.
            _aiWidthAtHide = _aiColumnWidth;
            _aiColumnShown = false;
        }

        _appliedAiDisplayWidth = Collapsed;
        AiColumnMinWidth = 0;
        AiSplitterColumnWidth = Collapsed;
        AiColumnWidth = Collapsed;
    }

    /// <summary>
    /// An external writer set <c>UI.AiPanelWidth</c>: the value becomes the
    /// panel truth (displayed live while the panel is visible), and the
    /// display lead is set first so the persistence pipeline stays silent.
    /// </summary>
    private void ApplyExternalAiWidth(double width)
    {
        var applied = new GridLength(
            ClampStoredWidth(width, UiSettingsKeys.MinAiPanelWidth, UiSettingsKeys.DefaultAiPanelWidth));
        _aiWidthAtHide = applied;
        _appliedAiDisplayWidth = applied;
        if (_aiColumnShown)
        {
            AiColumnWidth = applied;
        }
    }

    #endregion

    #region Book Column Geometry

    /// <summary>Apply the inline geometry: the captured width comes back untouched.</summary>
    private void ShowBookColumn()
    {
        var restored = new GridLength(
            ClampStoredWidth(_bookWidthAtDetach.Value, UiSettingsKeys.MinBookPanelWidth, UiSettingsKeys.DefaultBookPanelWidth));
        _bookWidthAtDetach = restored;
        _bookColumnShown = true;
        _appliedBookDisplayWidth = restored;
        BookColumnMinWidth = UiSettingsKeys.MinBookPanelWidth;
        BookSplitterColumnWidth = SplitterTrack;
        BookColumnWidth = restored;
    }

    /// <summary>Apply the collapsed geometry; the capture keeps only a width the user actually saw.</summary>
    private void HideBookColumn()
    {
        if (_bookColumnShown)
        {
            // A drag may still ride the debounce (≤ DragQuietTime old), so
            // the displayed value — not the persisted cache — is the truth
            // a later attach must restore.
            _bookWidthAtDetach = _bookColumnWidth;
            _bookColumnShown = false;
        }

        _appliedBookDisplayWidth = Collapsed;
        BookColumnMinWidth = 0;
        BookSplitterColumnWidth = Collapsed;
        BookColumnWidth = Collapsed;
    }

    /// <summary>
    /// An external writer set <c>UI.BookPanelWidth</c>: the value becomes the
    /// book width truth (displayed live while the panel is inline, the
    /// attach-back truth while it is detached), and the display lead is set
    /// first so the persistence pipeline stays silent.
    /// </summary>
    private void ApplyExternalBookWidth(double width)
    {
        var applied = new GridLength(
            ClampStoredWidth(width, UiSettingsKeys.MinBookPanelWidth, UiSettingsKeys.DefaultBookPanelWidth));
        _bookWidthAtDetach = applied;
        _appliedBookDisplayWidth = applied;
        if (_bookColumnShown)
        {
            BookColumnWidth = applied;
        }
    }

    #endregion

    #region Helpers

    /// <summary>Apply one detach-mirror flip: the panel whose detached state changed (the runtime's closed enum — an unknown id faults loud, never silently drops).</summary>
    private void SetDetachMirror(PanelDetachId panel, bool detached)
    {
        switch (panel)
        {
            case PanelDetachId.Chart:
                IsChartDetached = detached;
                break;
            case PanelDetachId.Book:
                IsBookDetached = detached;
                if (detached)
                {
                    HideBookColumn();
                }
                else
                {
                    ShowBookColumn();
                }

                break;
            case PanelDetachId.Assistant:
                IsAssistantDetached = detached;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(panel), panel, "The panel id has no detach mirror.");
        }
    }

    /// <summary>Clamp a stored width: garbage (non-finite — an out-of-range JSON magnitude, e.g. <c>1e999</c>) falls back to the shipped default, anything else keeps above the usable minimum.</summary>
    private static double ClampStoredWidth(double width, double min, double fallback) =>
        double.IsFinite(width) ? Math.Max(width, min) : fallback;

    /// <summary>Whether an absolute <see cref="GridLength"/> carries this width (within the rounding epsilon).</summary>
    private static bool IsSameWidth(GridLength length, double width) =>
        length.IsAbsolute && Math.Abs(length.Value - width) < WidthSameEpsilon;

    /// <summary>Whether two <see cref="GridLength"/>s carry the same width.</summary>
    private static bool IsSameWidth(GridLength length, GridLength other) =>
        length.IsAbsolute && other.IsAbsolute && Math.Abs(length.Value - other.Value) < WidthSameEpsilon;

    #endregion
}

