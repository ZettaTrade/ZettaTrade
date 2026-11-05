namespace ZettaTrade.App.ViewModels;

using System;
using System.Reactive.Linq;
using Avalonia.Controls;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// ViewModel owning the persisted column geometry of the main window's
/// draggable splitters (the chart/book pair and the book/assistant pair).
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

    /// <summary>
    /// The applied persisted book width. The persistence pipeline only fires
    /// on a real difference against this cache, so both the startup restore
    /// and the changes-stream echo are absorbed without a settings rewrite.
    /// </summary>
    private double _persistedBookWidth = UiSettingsKeys.DefaultBookPanelWidth;

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
    private GridLength _aiColumnWidth = new(UiSettingsKeys.DefaultAiPanelWidth);
    private double _aiColumnMinWidth = UiSettingsKeys.MinAiPanelWidth;
    private GridLength _aiSplitterColumnWidth = SplitterTrack;

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
    /// <param name="logger">The class logger.</param>
    public WorkspaceLayoutViewModel(
        ISettingsService settings,
        ChatViewModel chat,
        ILogger<WorkspaceLayoutViewModel> logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        chat = chat ?? throw new ArgumentNullException(nameof(chat));

        // The assistant column follows the panel: hidden → the column, its
        // splitter track and its min clamp all collapse to zero; shown → the
        // captured width returns. (The book side has no hidden state.)
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

        // External writers apply live: the display lead (the AI side) or the
        // cache (the book side) is updated BEFORE the property changes, so
        // the persistence pipelines see an already-applied value and skip it.
        _settings.Changes
            .Where(change => change.Section == UiSettingsKeys.Section &&
                             change.Key == UiSettingsKeys.BookPanelWidth)
            .Select(change => change.NewValue)
            .OfType<double>()
            .Subscribe(
                width =>
                {
                    // The external value becomes the book width truth; the
                    // cache leads the property change, so the changed value
                    // the pipelines would observe is already absorbed.
                    _persistedBookWidth = ClampStoredWidth(
                        width,
                        UiSettingsKeys.MinBookPanelWidth,
                        UiSettingsKeys.DefaultBookPanelWidth);
                    BookColumnWidth = new GridLength(_persistedBookWidth);
                },
                ex => _logger.LogError(ex, "The book width stream stopped unexpectedly"));

        _settings.Changes
            .Where(change => change.Section == UiSettingsKeys.Section &&
                             change.Key == UiSettingsKeys.AiPanelWidth)
            .Select(change => change.NewValue)
            .OfType<double>()
            .Subscribe(
                width => ApplyExternalAiWidth(width),
                ex => _logger.LogError(ex, "The AI panel width stream stopped unexpectedly"));

        // Persist every settled drag (the debounce keeps a drag burst from
        // writing the settings file per pixel; Concat serializes the
        // writes). The BookColumnWidth property is the binding writeback
        // target of the grid's book column, so this pipeline fires exactly
        // on those splitter writes; the restore and the external echo never
        // cross the difference guard.
        this.WhenAnyValue(x => x.BookColumnWidth)
            .Skip(1)
            .Where(width => !IsSameWidth(width, _persistedBookWidth))
            .Throttle(DragQuietTime)
            .Select(width => Observable
                .FromAsync(() => _settings.SetAsync(
                    UiSettingsKeys.Section,
                    UiSettingsKeys.BookPanelWidth,
                    ClampStoredWidth(width.Value, UiSettingsKeys.MinBookPanelWidth, UiSettingsKeys.DefaultBookPanelWidth)))
                .Select(_ => width))
            .Concat()
            .Subscribe(
                issuedWidth => _persistedBookWidth = issuedWidth.Value,
                ex => _logger.LogError(ex, "Failed to persist the book panel width"));

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
        _persistedBookWidth = book;
        BookColumnWidth = new GridLength(book);
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
            "Restored persisted workspace widths: book {BookWidth:F0}px, assistant {AiWidth:F0}px ({AiState})",
            book,
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

    #region Helpers

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
