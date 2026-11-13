namespace ZettaTrade.App.ViewModels;

using System;
using System.Globalization;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Livery.Contracts;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.UI.Extensions;
// RxVoid rides an alias (not a plain ReactiveUI.Primitives import): plain both
// imports would make every RxVoid Subscribe ambiguous against System's one.
using RxVoid = ReactiveUI.Primitives.RxVoid;

/// <summary>
/// ViewModel owning the workspace's multi-chart layout (T-201): how many
/// chart panels the chart column tiles (the single panel every session
/// had, the 1x2 primary+slot pair or the 2x2 four-panel grid) and the
/// seams' persisted geometry. The mode and the seam sizes are persisted
/// state, mirroring the <see cref="WorkspaceLayoutViewModel"/> discipline:
/// the startup walk restores <c>UI.ChartLayout</c>,
/// <c>UI.ChartSlotColumnWidth</c> and <c>UI.ChartGridTopRowHeight</c>
/// before any window opens (no relayout flash), a settled drag persists
/// once per burst, the restore and the changes-stream echo never rewrite
/// the value they just applied, and external writers apply live.
/// The slots are lazy full chart ViewModels: a pick of a tiled layout (or
/// its restore) creates the slot VMs it needs and their init walks
/// serialize behind one task-based queue — each slot restores its own
/// <c>UI.ChartSlot{N}SymbolId</c> / <c>UI.ChartSlot{N}TimeFrame</c> and
/// streams its own symbol. Slots are never torn down within the session:
/// a switch back re-shows them in-session. The primary keeps the
/// workspace's shared keys — the order book, the tape and the order
/// dialog follow the primary's pick, never a slot's.
/// </summary>
public sealed class WorkspaceChartsViewModel : ReactiveObject, IAsyncInitializable
{
    #region Constants

    /// <summary>Thickness of the grid's fixed seam track columns and rows (the resize handles ride them).</summary>
    private const double SeamThickness = 8;

    /// <summary>
    /// How much size difference counts as "same" (a float round-trip
    /// through layout rounding never escalates into a settings rewrite).
    /// </summary>
    private const double SizeSameEpsilon = 0.01;

    /// <summary>
    /// How long the persistence waits after the last drag move before it
    /// writes: one write per settled drag burst instead of one per pixel.
    /// </summary>
    private static readonly TimeSpan DragQuietTime = TimeSpan.FromMilliseconds(250);

    /// <summary>The slot count each tiled layout needs (the primary is outside this count).</summary>
    private static int SlotCount(ChartLayoutMode mode) => mode switch
    {
        ChartLayoutMode.OneByTwo => 1,
        ChartLayoutMode.TwoByTwo => 3,
        _ => 0,
    };

    #endregion

    #region Private Fields

    private readonly ISettingsService _settings;
    private readonly ILogger<WorkspaceChartsViewModel> _logger;

    /// <summary>
    /// The slot factory: builds a full chart ViewModel whose persistence
    /// keys derive from the given slot number ("1".."3"). The composition
    /// owns it — the slots resolve the same seams the primary singleton
    /// rides, never each other.
    /// </summary>
    private readonly Func<string?, ChartViewModel> _slotFactory;

    /// <summary>The layout mode the ViewModel last applied. Programmatic writes (restore, external echo) update this BEFORE the property changes, so the persistence pipeline filters them out and only real menu picks push differences through.</summary>
    private ChartLayoutMode _appliedLayout = ChartLayoutMode.Single;

    /// <summary>The slot column width the ViewModel last applied; the persistence pipeline only fires on a real difference against this cache (restore, external echo and layout rebuilds absorbed).</summary>
    private GridLength _appliedSlotColumnWidth = new(UiSettingsKeys.DefaultChartSlotColumnWidth);

    /// <summary>See <see cref="_appliedSlotColumnWidth"/>; the 2x2 top row's own cache.</summary>
    private GridLength _appliedTopRowHeight = new(UiSettingsKeys.DefaultChartGridTopRowHeight);

    /// <summary>The created slot VMs by slot number (index 0 = slot 1). Entries persist for the session once created.</summary>
    private readonly ChartViewModel?[] _slots = new ChartViewModel?[3];

    /// <summary>Every created slot lands here; the single serialized queue initializes each one on the UI thread.</summary>
    private readonly Subject<ChartViewModel> _slotCreated = new();

    private ChartLayoutMode _layout = ChartLayoutMode.Single;
    private GridLength _slotColumnWidth = new(UiSettingsKeys.DefaultChartSlotColumnWidth);
    private GridLength _topRowHeight = new(UiSettingsKeys.DefaultChartGridTopRowHeight);

    #endregion

    #region Constructor

    /// <summary>Create a new WorkspaceChartsViewModel instance.</summary>
    /// <param name="settings">The app settings service; the startup walk
    /// restores the mode and the seam sizes from it, a settled drag
    /// persists there and the changes stream carries external writers.</param>
    /// <param name="slotFactory">The slot builder: one full chart ViewModel
    /// per slot number, sharing every seam the primary rides.</param>
    /// <param name="logger">The file logger.</param>
    public WorkspaceChartsViewModel(
        ISettingsService settings,
        Func<string?, ChartViewModel> slotFactory,
        ILogger<WorkspaceChartsViewModel> logger)
    {
        _settings = settings;
        _slotFactory = slotFactory;
        _logger = logger;

        // The View menu's layout pick (T-201): one command, the mode rides
        // the parameter. The slots exist BEFORE the property raise (the
        // grid's rebuild never sees a missing slot), and the assignment
        // rides the persistence pipeline unguarded — the user's pick is
        // what it writes. A fault logs through ThrownExceptions, never a
        // silent death.
        SetLayoutCommand = ReactiveCommand.Create<ChartLayoutMode, RxVoid>(mode =>
        {
            EnsureSlots(mode);
            Layout = mode;
            return RxVoid.Default;
        });
        SetLayoutCommand.ThrownExceptions.Subscribe(
            ex => _logger.LogError(ex, "The chart layout pick failed"));

        // The layout persists: every USER pick writes the enum name into
        // the UI settings section (the debounced settings service calls
        // the file I/O). The subscribe-time bootstrap emission is skipped,
        // and the restore's and the echo's own assignments sit behind the
        // applied lead — reads are never re-written.
        this.WhenAnyValue(x => x.Layout)
            .Skip(1)
            .Where(mode => mode != _appliedLayout)
            .SelectMany(mode => Observable
                .FromAsync(() => _settings.SetAsync(
                    UiSettingsKeys.Section,
                    UiSettingsKeys.ChartLayout,
                    mode.ToString()))
                .Select(_ => mode))
            .Subscribe(
                // The cache follows the issued write (the workspace's
                // width discipline): a user pick of the mode the LAST
                // WRITE carried is absorbed (a duplicate write carries no
                // information), a pick of any other mode pushes through —
                // so a pick back to the mode a previous session restored
                // still persists.
                issuedMode => _appliedLayout = issuedMode,
                ex => _logger.LogError(ex, "Failed to persist the chart layout"));

        // The seam sizes persist every settled drag (the debounce keeps a
        // drag burst from writing the settings file per pixel; Concat
        // serializes the writes). The SlotColumnWidth / TopRowHeight
        // properties are the writeback targets of the grid's seam rows and
        // columns, so the pipelines fire exactly on those splitter writes;
        // the restore and the external echo never cross the difference
        // guards.
        this.WhenAnyValue(x => x.SlotColumnWidth)
            .Skip(1)
            .Where(width => !IsSameSize(width, _appliedSlotColumnWidth))
            .Throttle(DragQuietTime)
            .Select(width => Observable
                .FromAsync(() => _settings.SetAsync(
                    UiSettingsKeys.Section,
                    UiSettingsKeys.ChartSlotColumnWidth,
                    ClampStoredSize(
                        width.Value,
                        UiSettingsKeys.MinChartSlotColumnWidth,
                        UiSettingsKeys.DefaultChartSlotColumnWidth)))
                .Select(_ => width))
            .Concat()
            .Subscribe(
                appliedWidth => _appliedSlotColumnWidth = appliedWidth,
                ex => _logger.LogError(ex, "Failed to persist the grid slot column width"));
        this.WhenAnyValue(x => x.TopRowHeight)
            .Skip(1)
            .Where(height => !IsSameSize(height, _appliedTopRowHeight))
            .Throttle(DragQuietTime)
            .Select(height => Observable
                .FromAsync(() => _settings.SetAsync(
                    UiSettingsKeys.Section,
                    UiSettingsKeys.ChartGridTopRowHeight,
                    ClampStoredSize(
                        height.Value,
                        UiSettingsKeys.MinChartGridTopRowHeight,
                        UiSettingsKeys.DefaultChartGridTopRowHeight)))
                .Select(_ => height))
            .Concat()
            .Subscribe(
                appliedHeight => _appliedTopRowHeight = appliedHeight,
                ex => _logger.LogError(ex, "Failed to persist the grid top row height"));

        // External writers apply live: the applied leads update BEFORE the
        // property changes, so the persistence pipelines see an
        // already-applied value and skip it.
        _settings.Changes
            .Where(change => change.Section == UiSettingsKeys.Section &&
                             change.Key == UiSettingsKeys.ChartLayout)
            .Select(change => change.NewValue)
            .OfType<string>()
            .Subscribe(
                stored =>
                {
                    var mode = ParseStoredLayout(stored);
                    if (!string.Equals(mode.ToString(), stored, StringComparison.OrdinalIgnoreCase))
                    {
                        // An unrecognized externally written mode keeps the
                        // standing one and logs — never a silent substitution
                        // (a mid-session collapse would throw the user's
                        // tiles away over a hand-edited file).
                        _logger.LogWarning(
                            "Externally written chart layout '{PersistedLayout}' is not a supported layout; keeping '{Layout}'",
                            stored,
                            _layout);
                        return;
                    }

                    ApplyProgrammaticLayout(mode);
                },
                ex => _logger.LogError(ex, "The chart layout stream stopped unexpectedly"));

        _settings.Changes
            .Where(change => change.Section == UiSettingsKeys.Section &&
                             change.Key == UiSettingsKeys.ChartSlotColumnWidth)
            .Select(change => change.NewValue)
            .OfType<double>()
            .Subscribe(
                width =>
                {
                    // The same external-writer discipline as the layout
                    // side: the value becomes the slot column truth, the
                    // cache leads the property change, everything is
                    // applied live.
                    var clamped = ClampStoredSize(
                        width,
                        UiSettingsKeys.MinChartSlotColumnWidth,
                        UiSettingsKeys.DefaultChartSlotColumnWidth);
                    _appliedSlotColumnWidth = new GridLength(clamped);
                    SlotColumnWidth = _appliedSlotColumnWidth;
                },
                ex => _logger.LogError(ex, "The slot column width stream stopped unexpectedly"));

        _settings.Changes
            .Where(change => change.Section == UiSettingsKeys.Section &&
                             change.Key == UiSettingsKeys.ChartGridTopRowHeight)
            .Select(change => change.NewValue)
            .OfType<double>()
            .Subscribe(
                height =>
                {
                    // The same external-writer discipline as above.
                    var clamped = ClampStoredSize(
                        height,
                        UiSettingsKeys.MinChartGridTopRowHeight,
                        UiSettingsKeys.DefaultChartGridTopRowHeight);
                    _appliedTopRowHeight = new GridLength(clamped);
                    TopRowHeight = _appliedTopRowHeight;
                },
                ex => _logger.LogError(ex, "The top row height stream stopped unexpectedly"));

        // The slots' init walks serialize behind one task-based queue: a
        // mode pick creating three slots starts exactly one walk at a
        // time (each slot restores its own keys before the next starts),
        // and a walk's failures land in the file log. The walk runs on the
        // UI thread — the restore's property assignments must drive the
        // bound faces from the same thread the primary's startup walk
        // does; a queue resumed on the settings I/O's completion thread
        // would raise into the bindings cross-thread.
        _slotCreated
            .SubscribeAsync(
                async slot =>
                {
                    if (Dispatcher.UIThread.CheckAccess())
                    {
                        await slot.InitializeAsync();
                        return;
                    }

                    await Dispatcher.UIThread.InvokeAsync(async () =>
                    {
                        await slot.InitializeAsync();
                        return RxVoid.Default;
                    });
                },
                onError: ex => _logger.LogError(ex, "A grid slot's init walk failed"));
    }

    #endregion

    #region Properties

    /// <summary>
    /// The workspace's current chart layout: the single panel (the
    /// pre-grid default), the 1x2 primary+slot pair or the 2x2
    /// four-panel grid. The View menu's radio items mirror it; the
    /// grid control rebuilds on every change.
    /// </summary>
    public ChartLayoutMode Layout
    {
        get => _layout;
        private set
        {
            if (_layout == value)
            {
                return;
            }

            _layout = value;
            this.RaisePropertyChanged(nameof(Layout));
            this.RaisePropertyChanged(nameof(IsSingleLayout));
            this.RaisePropertyChanged(nameof(IsOneByTwoLayout));
            this.RaisePropertyChanged(nameof(IsTwoByTwoLayout));
        }
    }

    /// <summary>Whether the workspace shows the single chart panel (the radio item's checked state).</summary>
    public bool IsSingleLayout => Layout == ChartLayoutMode.Single;

    /// <summary>Whether the workspace shows the 1x2 primary+slot pair (the radio item's checked state).</summary>
    public bool IsOneByTwoLayout => Layout == ChartLayoutMode.OneByTwo;

    /// <summary>Whether the workspace shows the 2x2 four-panel grid (the radio item's checked state).</summary>
    public bool IsTwoByTwoLayout => Layout == ChartLayoutMode.TwoByTwo;

    /// <summary>
    /// Width of the grid's slot column (logical pixels): the fixed column
    /// the slot panels ride, shared by the 1x2 pair's seam and the 2x2
    /// grid's aligned slot columns so both rows' seams stay one line. The
    /// grid's slot columns bind it; the splitter's dragged widths flow in
    /// and persist through the pipeline once the drag settles.
    /// </summary>
    public GridLength SlotColumnWidth
    {
        get => _slotColumnWidth;
        set => this.RaiseAndSetIfChanged(ref _slotColumnWidth, value);
    }

    /// <summary>
    /// Height of the 2x2 grid's top row (logical pixels): the fixed row
    /// the primary and slot 1 ride; the bottom row takes the rest. The
    /// grid's top row binds it; the horizontal splitter's dragged heights
    /// flow in and persist through the pipeline once the drag settles.
    /// </summary>
    public GridLength TopRowHeight
    {
        get => _topRowHeight;
        set => this.RaiseAndSetIfChanged(ref _topRowHeight, value);
    }

    /// <summary>The first slot's chart (created lazily by the 1x2 or 2x2 pick; null while the layout never showed it).</summary>
    public ChartViewModel? Slot1 { get; private set; }

    /// <summary>The second slot's chart (the 2x2 bottom-left face; null while the layout never showed it).</summary>
    public ChartViewModel? Slot2 { get; private set; }

    /// <summary>The third slot's chart (the 2x2 bottom-right face; null while the layout never showed it).</summary>
    public ChartViewModel? Slot3 { get; private set; }

    /// <summary>The View menu's layout pick (T-201): the mode rides the parameter, the slots exist before the raise.</summary>
    public ReactiveCommand<ChartLayoutMode, RxVoid> SetLayoutCommand { get; }

    #endregion

    #region Public Methods

    /// <inheritdoc/>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var storedLayout = await _settings.GetAsync<string>(
            UiSettingsKeys.Section,
            UiSettingsKeys.ChartLayout,
            UiSettingsKeys.DefaultChartLayout);
        var slotWidth = ClampStoredSize(
            await _settings.GetAsync<double>(
                UiSettingsKeys.Section,
                UiSettingsKeys.ChartSlotColumnWidth,
                UiSettingsKeys.DefaultChartSlotColumnWidth),
            UiSettingsKeys.MinChartSlotColumnWidth,
            UiSettingsKeys.DefaultChartSlotColumnWidth);
        var topHeight = ClampStoredSize(
            await _settings.GetAsync<double>(
                UiSettingsKeys.Section,
                UiSettingsKeys.ChartGridTopRowHeight,
                UiSettingsKeys.DefaultChartGridTopRowHeight),
            UiSettingsKeys.MinChartGridTopRowHeight,
            UiSettingsKeys.DefaultChartGridTopRowHeight);

        if (string.IsNullOrWhiteSpace(storedLayout))
        {
            // No persisted layout (the shipped default answers for an
            // absent key too): the single panel every session had — no
            // warning for the common fresh-profile shape.
            _logger.LogInformation(
                "No persisted chart layout; starting on '{Layout}'",
                ChartLayoutMode.Single);
            storedLayout = UiSettingsKeys.DefaultChartLayout;
        }

        // An unrecognized stored mode keeps the default and logs — the
        // trading-safety rule: never substitute defaults silently.
        var restored = ParseStoredLayout(storedLayout);
        if (!string.Equals(restored.ToString(), storedLayout, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "Persisted chart layout '{PersistedLayout}' is not a supported layout; starting on '{Layout}'",
                storedLayout,
                restored);
        }

        // The applied leads lead the property assignments on purpose: a
        // pick the persistence pipelines would observe sits behind the
        // difference guards, so a restore never rewrites the value it
        // just read.
        _appliedSlotColumnWidth = new GridLength(slotWidth);
        SlotColumnWidth = _appliedSlotColumnWidth;
        _appliedTopRowHeight = new GridLength(topHeight);
        TopRowHeight = _appliedTopRowHeight;

        ApplyProgrammaticLayout(restored);

        _logger.LogInformation(
            "Restored persisted chart layout: {Layout} (slot column {SlotWidth:F0}px, top row {TopHeight:F0}px)",
            restored,
            slotWidth,
            topHeight);
    }

    #endregion

    #region Layout Mechanics

    /// <summary>
    /// A programmatic mode apply (the startup walk's restore or an
    /// external writer): the slots exist first, then the applied lead and
    /// the property change — the persistence pipeline sees an
    /// already-applied value and stays silent.
    /// </summary>
    private void ApplyProgrammaticLayout(ChartLayoutMode mode)
    {
        EnsureSlots(mode);
        _appliedLayout = mode;
        Layout = mode;
    }

    /// <summary>
    /// Create the slot VMs the mode needs, skipping the ones the session
    /// already built. Creation is synchronous so a pick's property raise
    /// can never rebuild the grid over a missing slot; the init walks
    /// ride the serialized queue asynchronously.
    /// </summary>
    private void EnsureSlots(ChartLayoutMode mode)
    {
        for (var slotNumber = 1; slotNumber <= SlotCount(mode); slotNumber++)
        {
            if (_slots[slotNumber - 1] is { } existing)
            {
                continue;
            }

            var slot = _slotFactory(slotNumber.ToString(CultureInfo.InvariantCulture));
            _slots[slotNumber - 1] = slot;
            switch (slotNumber)
            {
                case 1:
                    Slot1 = slot;
                    break;
                case 2:
                    Slot2 = slot;
                    break;
                case 3:
                    Slot3 = slot;
                    break;
            }

            _logger.LogInformation("Created chart slot {SlotNumber}", slotNumber);
            _slotCreated.OnNext(slot);
        }
    }

    /// <summary>Parse a stored layout mode name; an unknown name yields the single-panel default (logged by the caller).</summary>
    private static ChartLayoutMode ParseStoredLayout(string stored)
    {
        foreach (ChartLayoutMode mode in Enum.GetValues<ChartLayoutMode>())
        {
            if (string.Equals(mode.ToString(), stored, StringComparison.OrdinalIgnoreCase))
            {
                return mode;
            }
        }

        return ChartLayoutMode.Single;
    }

    /// <summary>Clamp a stored seam size: garbage (non-finite — an out-of-range JSON magnitude, e.g. <c>1e999</c>, a hand-edited file) falls back to the shipped default, anything else at least the usable minimum (the star neighbor absorbs the rest).</summary>
    private static double ClampStoredSize(double stored, double min, double fallback) =>
        double.IsFinite(stored) ? Math.Max(stored, min) : fallback;

    /// <summary>Whether two absolute sizes carry the same value (within the rounding epsilon).</summary>
    private static bool IsSameSize(GridLength left, GridLength right) =>
        left.IsAbsolute && right.IsAbsolute && Math.Abs(left.Value - right.Value) < SizeSameEpsilon;

    #endregion
}
