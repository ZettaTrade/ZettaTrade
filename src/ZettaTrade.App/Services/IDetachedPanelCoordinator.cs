namespace ZettaTrade.App.Services;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// One workspace panel's detach seam (T-103): the panel's content control,
/// the inline Border host it swaps with, and the panel's own focus
/// affordance. The main window registers these once at its open; the
/// coordinator then owns the physical detach moves, window lifetime and
/// placement.
/// </summary>
/// <param name="Panel">Which workspace panel the slot carries.</param>
/// <param name="PanelContent">The panel's content control (its DataContext is set by the window before registration).</param>
/// <param name="InlineHost">The inline Border the panel lives in while attached (the note takes its place while detached).</param>
/// <param name="FocusAffordance">The panel's own deferred-focus affordance (the chat input's deferred focus), runnable against whatever window currently hosts the control; null when the panel has none.</param>
public sealed record DetachedPanelSlot(
    PanelDetachId Panel,
    Avalonia.Controls.Control PanelContent,
    Avalonia.Controls.Border InlineHost,
    Action? FocusAffordance);

/// <summary>
/// The detached-panel orchestration seam (T-103): the single runtime
/// authority of which workspace panels live in their own owned windows
/// right now. The <see cref="ViewModels.WorkspaceLayoutViewModel"/> mirrors
/// the detached state from this seam's observables and drives the menu
/// toggle through it; the main window hands the view lane over at its
/// open and the coordinator keeps every window's placement persisted.
/// </summary>
public interface IDetachedPanelCoordinator
{
    /// <summary>The panel moved into its own window (raised on the UI thread, for every detach leg including the restore).</summary>
    IObservable<PanelDetachId> PanelDetached { get; }

    /// <summary>The panel returned inline: the window closed (its title-bar close or a route-driven attach). Raised on the UI thread.</summary>
    IObservable<PanelDetachId> PanelAttached { get; }

    /// <summary>Whether the panel currently lives in its own window.</summary>
    bool IsDetached(PanelDetachId panel);

    /// <summary>
    /// Hand the view lane over (the main window's panel slots). Must run
    /// before any other call; the coordinator then answers every detach
    /// leg physically.
    /// </summary>
    void RegisterPanels(IReadOnlyList<DetachedPanelSlot> slots);

    /// <summary>
    /// Restore the detached layout the stored record carries: every panel
    /// the record lists detaches into its own window at its stored
    /// placement. Runs from the main window's open, after
    /// <see cref="RegisterPanels"/>.
    /// </summary>
    Task RestoreDetachedAsync(CancellationToken cancellationToken);

    /// <summary>The detach/attach toggle: a detached panel attaches, an inline one detaches.</summary>
    Task ToggleDetachAsync(PanelDetachId panel, CancellationToken cancellationToken);

    /// <summary>The explicit detach: the panel moves into its own window at its last placement (or centered when it never had one).</summary>
    Task DetachPanelWindowAsync(PanelDetachId panel, CancellationToken cancellationToken);

    /// <summary>The explicit attach: the window closes gently and the panel returns inline, preserving its content and state.</summary>
    Task AttachPanelWindowAsync(PanelDetachId panel, CancellationToken cancellationToken);

    /// <summary>
    /// The panel's focus affordance: an open panel window activates first,
    /// then the deferred focus leg runs; an inline panel runs its own
    /// affordance right away.
    /// </summary>
    Task FocusPanelAsync(PanelDetachId panel, CancellationToken cancellationToken);

    /// <summary>
    /// The app shutdown arm (raised from the lifetime's ShutdownRequested
    /// before the windows close): the closing panel windows return their
    /// content but never rewrite the stored record — the detached layout
    /// persists exactly as the user left it.
    /// </summary>
    void MarkShutdownRequested();
}
