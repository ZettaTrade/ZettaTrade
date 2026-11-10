namespace ZettaTrade.App.Services;

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Livery.Contracts;
using Microsoft.Extensions.Logging;
using ZettaTrade.App.Views.Panels;
using ZettaTrade.Core.Interfaces;
// RxVoid rides an alias (not a plain ReactiveUI.Primitives import): plain both
// imports would make every RxVoid Subscribe ambiguous against System's one.
using RxVoid = ReactiveUI.Primitives.RxVoid;

/// <summary>
/// The detached-panel window hosts the coordinator builds from (T-103):
/// one window factory per workspace panel, supplied by the DI extension —
/// the coordinator stays view-agnostic, the windows stay trivial shells.
/// </summary>
/// <param name="Factories">The panel-to-window factory map.</param>
public sealed record DetachedPanelHosts(
    IReadOnlyDictionary<PanelDetachId, Func<Window>> Factories)
{
    /// <summary>Whether the host map lists every detacheable panel exactly once.</summary>
    public bool IsComplete =>
        Factories.Count == 3 &&
        Enum.GetValues<PanelDetachId>().All(panel => Factories.ContainsKey(panel));
}

/// <summary>
/// One open detached window's wiring the coordinator keeps (T-103): the
/// window itself, the panel it hosts, and the closing route reference the
/// explicit-attach unwires before it closes the window by route.
/// </summary>
/// <param name="Window">The open detached window.</param>
/// <param name="Panel">Which panel it hosts.</param>
/// <param name="ClosingRoute">The closing route reference (unwired on an explicit attach so the close never double-runs).</param>
internal sealed record HostedPanelWindow(
    Window Window,
    PanelDetachId Panel,
    EventHandler<CancelEventArgs> ClosingRoute);

/// <summary>
/// The detached-panel runtime (T-103): the single authority of which
/// workspace panels live in their own windows, the physical move of every
/// panel control (ONE control instance per panel — the detach moves the
/// control cross-window, an attach moves it back, so every subscription
/// the window code-behind armed once keeps working on either host), the
/// per-window placement capture/compose legs, and the persisted detached
/// record under <c>UI.DetachedPanels</c>.
/// The record stores only the detached windows' placement FRACTIONS
/// against their display samples (the T-103 geometry canon), so a DPI or
/// resolution shift between sessions composes the same relative box —
/// never a stale pixel geometry.
/// </summary>
/// <remarks>
/// Every mutating entry rides the UI thread (a hop when the caller
/// resumed elsewhere); the persisted writes serialize through one
/// Concat pipeline — the same discipline the width persistence rides.
/// The runtime never throws for a missing or hostile environment: a
/// window whose screens cannot be read stays centered, a record that
/// cannot parse restores nothing, and the placement capture absorbs
/// its own applied box.
/// </remarks>
/// <param name="settings">The app settings service (the record lives under <c>UI.DetachedPanels</c>).</param>
/// <param name="hosts">The per-panel window factories.</param>
/// <param name="logger">The class logger.</param>
public sealed class DetachedPanelCoordinator : IDetachedPanelCoordinator
{
    #region Private Fields

    private readonly ISettingsService _settings;
    private readonly DetachedPanelHosts _hosts;
    private readonly ILogger<DetachedPanelCoordinator> _logger;

    /// <summary>The registered panel slots (the view lane the main window hands over at its open).</summary>
    private Dictionary<PanelDetachId, DetachedPanelSlot> _slots = [];

    /// <summary>The currently open detached windows.</summary>
    private readonly Dictionary<PanelDetachId, HostedPanelWindow> _openHosts = [];

    /// <summary>The latest placement each panel carried across the attach/detach cycle (a re-detach re-uses the last box; a fresh session's restore seeds it from the stored record).</summary>
    private readonly Dictionary<PanelDetachId, DetachedPanelEntry> _placements = [];

    /// <summary>The detached/attached-state notices (the runtime's whole-lifetime sources).</summary>
    private readonly Subject<PanelDetachId> _panelDetachedSources = new();

    private readonly Subject<PanelDetachId> _panelAttachedSources = new();

    /// <summary>The serialized persist writes feed on: every request collapses into the next single write reading the CURRENT record.</summary>
    private readonly Subject<RxVoid> _saveRequests = new();

    /// <summary>Whether the app is shutting down: closing panel windows restore their content but never rewrite the record (the detached layout persists exactly as the user left it).</summary>
    private volatile bool _shutdownRequested;

    #endregion

    #region Constructor

    /// <summary>Create a new DetachedPanelCoordinator instance.</summary>
    /// <param name="settings">The app settings service (the record lives under <c>UI.DetachedPanels</c>).</param>
    /// <param name="hosts">The per-panel window factories.</param>
    /// <param name="logger">The class logger.</param>
    public DetachedPanelCoordinator(
        ISettingsService settings,
        DetachedPanelHosts hosts,
        ILogger<DetachedPanelCoordinator> logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _hosts = hosts is { IsComplete: true } hostsValue
            ? hostsValue
            : throw new ArgumentException(
                "The detached-window host map must list every panel exactly once.",
                nameof(hosts));

        // The serialized write pipeline rides from the construction: every
        // save request collapses into the next single write reading the
        // CURRENT record (the width-persistence discipline).
        _ = _saveRequests
            .Select(_ => Observable.FromAsync(async () =>
            {
                await _settings.SetAsync(
                    UiSettingsKeys.Section,
                    UiSettingsKeys.DetachedPanels,
                    DetachedPanelsContract.Serialize(PersistedRecord));
                return RxVoid.Default;
            }))
            .Concat()
            .Subscribe(
                _ => { },
                ex => _logger.LogError(ex, "Failed to persist the detached-panel record"));
    }

    #endregion

    #region Properties

    /// <inheritdoc/>
    public IObservable<PanelDetachId> PanelDetached => _panelDetachedSources;

    /// <inheritdoc/>
    public IObservable<PanelDetachId> PanelAttached => _panelAttachedSources;

    #endregion

    #region Public Methods

    /// <inheritdoc/>
    public bool IsDetached(PanelDetachId panel) => _openHosts.ContainsKey(panel);

    /// <inheritdoc/>
    public void RegisterPanels(IReadOnlyList<DetachedPanelSlot> slots)
    {
        ArgumentNullException.ThrowIfNull(slots);
        if (!Dispatcher.UIThread.CheckAccess())
        {
            throw new InvalidOperationException(
                "The detached-panel slots must register on the UI thread (the view lane belongs to it).");
        }

        _slots = slots.ToDictionary(slot => slot.Panel, slot => slot);
        foreach (var slot in _slots.Values)
        {
            // The initial inline attach: the panel content joins its host
            // exactly the way the XAML would have placed it.
            slot.InlineHost.Child = slot.PanelContent;
        }

        _logger.LogInformation(
            "The detached-panel slots registered: {Count} panel(s) ({Panels})",
            _slots.Count,
            string.Join(", ", _slots.Values.Select(slot => slot.Panel)));
    }

    /// <inheritdoc/>
    public async Task RestoreDetachedAsync(CancellationToken cancellationToken)
    {
        await RunOnUiAsync(async () =>
        {
            var json = await _settings.GetAsync<string>(
                UiSettingsKeys.Section,
                UiSettingsKeys.DetachedPanels,
                UiSettingsKeys.DefaultDetachedPanels);
            var entries = DetachedPanelsContract.Parse(json);

            foreach (var entry in entries)
            {
                // The stored entry seeds the placement map, and the
                // ordinary detach leg opens the window against it.
                _placements[entry.Panel] = entry;
                await DetachPanelWindowAsync(entry.Panel, cancellationToken);
            }

            if (entries.Count > 0)
            {
                _logger.LogInformation(
                    "Restored {Count} detached panel window(s): {Panels}",
                    entries.Count,
                    string.Join(", ", entries.Select(entry => entry.Panel)));
            }

            return RxVoid.Default;
        });
    }

    /// <inheritdoc/>
    public async Task ToggleDetachAsync(PanelDetachId panel, CancellationToken cancellationToken)
    {
        if (IsDetached(panel))
        {
            await AttachPanelWindowAsync(panel, cancellationToken);
        }
        else
        {
            await DetachPanelWindowAsync(panel, cancellationToken);
        }
    }

    /// <inheritdoc/>
    public async Task DetachPanelWindowAsync(PanelDetachId panel, CancellationToken cancellationToken) =>
        await RunOnUiAsync(async () =>
        {
            if (!_slots.TryGetValue(panel, out var slot))
            {
                _logger.LogWarning(
                    "The detached-panel slot {Panel} was never registered; the detach leg stands down",
                    panel);
                return RxVoid.Default;
            }

            if (_openHosts.ContainsKey(panel))
            {
                return RxVoid.Default;
            }

            var window = _hosts.Factories[panel]();
            var host = WireWindow(window, panel);
            _openHosts[panel] = host;

            // The content move happens BEFORE the window shows: the inline
            // host swaps the panel for the note FIRST (the swap releases the
            // panel — a control can never hold two visual parents), then the
            // window takes it. One layout pass, no flash, and the control
            // keeps the DataContext the main window set on it.
            slot.InlineHost.Child = new PanelDetachedNote();
            window.Content = slot.PanelContent;

            ApplyStoredPlacement(window, panel);
            window.Show();
            window.Activate();

            _panelDetachedSources.OnNext(panel);
            _logger.LogInformation(
                "The panel {Panel} detached into its own window",
                panel);

            return RxVoid.Default;
        });

    /// <inheritdoc/>
    public async Task AttachPanelWindowAsync(PanelDetachId panel, CancellationToken cancellationToken) =>
        await RunOnUiAsync(async () =>
        {
            if (!_openHosts.Remove(panel, out var host))
            {
                return RxVoid.Default;
            }

            // The explicit attach closes the window with the closing
            // route unwired: the window releases the panel first (a
            // control can never hold two visual parents), the inline host
            // takes it back, and the route can never double-run.
            host.Window.Closing -= host.ClosingRoute;
            host.Window.Content = null;
            AttachPanelToSlot(host);
            host.Window.Close();

            RequestSave();
            _panelAttachedSources.OnNext(panel);
            _logger.LogInformation(
                "The panel {Panel} attached back inline",
                panel);

            return RxVoid.Default;
        });

    /// <inheritdoc/>
    public async Task FocusPanelAsync(PanelDetachId panel, CancellationToken cancellationToken) =>
        await RunOnUiAsync(async () =>
        {
            var slot = _slots.GetValueOrDefault(panel);
            if (slot?.FocusAffordance is null)
            {
                return RxVoid.Default;
            }

            if (_openHosts.TryGetValue(panel, out var host))
            {
                host.Window.Activate();
            }

            // The deferred-focus canon (T-012): a focus arrives below the
            // render priority, so the affected control gets its arranged
            // pass first — on whichever window currently hosts the panel.
            await Dispatcher.UIThread.InvokeAsync(
                slot.FocusAffordance,
                DispatcherPriority.Background);

            return RxVoid.Default;
        });

    /// <inheritdoc/>
    public void MarkShutdownRequested()
    {
        _shutdownRequested = true;
        _logger.LogInformation(
            "The app shutdown requested; the detached-panel windows will close quietly (the stored record stands as the user left it)");
    }

    #endregion

    #region Window Wiring

    /// <summary>Wire a fresh window into the runtime: the closing route (the user-close leg) and the placement capture legs on both the position and the size events. Returns the host record the explicit attach unwires from.</summary>
    private HostedPanelWindow WireWindow(
        Window window,
        PanelDetachId panel)
    {
        var closingRoute = new EventHandler<CancelEventArgs>((_, _) =>
            OnPanelWindowClosing(panel));
        window.Closing += closingRoute;
        window.PositionChanged += (_, _) => CapturePlacement(window, panel);
        window.Resized += (_, _) => CapturePlacement(window, panel);
        return new HostedPanelWindow(window, panel, closingRoute);
    }

    /// <summary>The user's title-bar close (or any window close the runtime did not route): the content returns inline, the record drops, the state emits — unless the app is shutting down, where the content returns and the record stands as stored.</summary>
    private void OnPanelWindowClosing(PanelDetachId panel)
    {
        if (!_openHosts.Remove(panel, out var host))
        {
            return;
        }

        // The window releases the panel before the inline host takes it
        // back (a control can never hold two visual parents).
        host.Window.Content = null;
        AttachPanelToSlot(host);

        if (!_shutdownRequested)
        {
            RequestSave();
            _panelAttachedSources.OnNext(panel);
            _logger.LogInformation(
                "The panel {Panel} window closed; the panel attached back inline",
                panel);
        }
    }

    /// <summary>Move the panel content back into its inline slot; the host window has already released the content (the close or the teardown follows right after).</summary>
    private void AttachPanelToSlot(HostedPanelWindow host)
    {
        if (_slots.TryGetValue(host.Panel, out var slot))
        {
            slot.InlineHost.Child = slot.PanelContent;
        }
    }

    #endregion

    #region Placement

    /// <summary>Apply the stored placement to a not-yet-shown window: an exact or remapped display match composes the box; anything else keeps the window's centered default. The pre-show application means the window never flashes between a default box and its stored one.</summary>
    private void ApplyStoredPlacement(Window window, PanelDetachId panel)
    {
        var entry = _placements.GetValueOrDefault(panel);
        if (entry is null)
        {
            return;
        }

        var samples = ReadDisplaySamples(window);
        if (samples.Count == 0)
        {
            _logger.LogInformation(
                "The panel {Panel} stored placement could not compose (no usable displays); the window stays centered",
                panel);
            return;
        }

        var found = PanelWindowGeometry.FindRestorable(samples, entry);
        if (found is not { } match)
        {
            _logger.LogInformation(
                "The panel {Panel} stored placement matched no display; the window stays centered",
                panel);
            return;
        }

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Position = new PixelPoint(match.PositionLeftPixel, match.PositionTopPixel);
        window.Width = match.WidthLogical;
        window.Height = match.HeightLogical;
        if (entry.Placement.IsMaximized)
        {
            window.WindowState = WindowState.Maximized;
        }
    }

    /// <summary>Read the current displays as the geometry samples the math composes against. An empty read (a headless platform) answers an empty list — the placement legs stand down, never throw.</summary>
    private static IReadOnlyList<PanelDisplaySample> ReadDisplaySamples(Window window)
    {
        var screens = window.Screens?.All;
        if (screens is null || screens.Count == 0)
        {
            return [];
        }

        return [.. screens
            .Select(screen => new PanelDisplaySample(
                screen.WorkingArea.X,
                screen.WorkingArea.Y,
                screen.WorkingArea.Width,
                screen.WorkingArea.Height,
                screen.Scaling))
            .Where(sample => sample.IsUsable)];
    }

    /// <summary>Fold the window's CURRENT box into the placement map. An unchanged record is absorbed — the capture never rewrites what it just applied.</summary>
    private void CapturePlacement(Window window, PanelDetachId panel)
    {
        var sample = ReadDisplaySamples(window)
            .FirstOrDefault(candidate => candidate.Contains(window.Position.X, window.Position.Y));
        if (sample is null)
        {
            // The window's corner sat on no working area the platform
            // reported (a screen left under the window): the capture
            // stands down and the next real move writes it.
            return;
        }

        // A maximized window stores its full-area shape with IsMaximized
        // (Avalonia keeps no public normal-bounds accessor; the compose
        // reapplies the same full-area shape before maximizing, so the
        // restore loses nothing but the pre-maximize fish).
        var positionPixel = window.Position;
        var sizePixel = window.ClientSize * window.RenderScaling;
        var placement = PanelWindowGeometry.Snapshot(
            sample,
            positionPixel.X,
            positionPixel.Y,
            sizePixel.Width,
            sizePixel.Height,
            window.WindowState == WindowState.Maximized);
        var entry = new DetachedPanelEntry(panel, sample, placement);

        if (_placements.GetValueOrDefault(panel) == entry)
        {
            // The compose-back capture of the restore's own box: nothing
            // to write.
            return;
        }

        _placements[panel] = entry;
        RequestSave();
    }

    #endregion

    #region Persistence

    /// <summary>The persisted record: entries for the currently open detached windows only. The in-memory placement map holds the attached panels' last boxes for their next detach; the store answers nothing for them (the record list IS the detached state).</summary>
    private IReadOnlyList<DetachedPanelEntry> PersistedRecord =>
        [.. _openHosts.Keys
            .Select(panel => _placements.GetValueOrDefault(panel))
            .Where(entry => entry is not null)
            .Select(entry => entry!)
            .OrderBy(entry => (int)entry.Panel)];

    /// <summary>Request one serialized settings write carrying the current record (the record derives live from the open hosts and their placements).</summary>
    private void RequestSave() =>
        _saveRequests.OnNext(RxVoid.Default);

    #endregion

    #region Helpers

    /// <summary>Run the body on the UI thread: an already-UI caller runs inline, a background caller hops and awaits the UI run (never blocked, never cross-thread).</summary>
    private static async Task<T> RunOnUiAsync<T>(Func<Task<T>> body) =>
        Dispatcher.UIThread.CheckAccess()
            ? await body()
            : await Dispatcher.UIThread.InvokeAsync(body);

    #endregion
}
