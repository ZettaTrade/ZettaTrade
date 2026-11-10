namespace ZettaTrade.App.Tests.Services;

using System;
using System.Collections.Generic;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using ZettaTrade.App.Services;

/// <summary>
/// The detached-panel coordinator's test double (T-103): records the routes
/// the layout ViewModel drives and replays the detached/attached notices —
/// the real runtime's UI-thread and window discipline stays out of the VM
/// facts, the seam contract stays the fact surface.
/// </summary>
public sealed class FakeDetachedPanelCoordinator : IDetachedPanelCoordinator
{
    private readonly Subject<PanelDetachId> _detached = new();
    private readonly Subject<PanelDetachId> _attached = new();
    private readonly HashSet<PanelDetachId> _open = [];

    /// <summary>Every panel id the toggle routed, in order.</summary>
    public List<PanelDetachId> Toggles { get; } = [];

    /// <inheritdoc/>
    public IObservable<PanelDetachId> PanelDetached => _detached;

    /// <inheritdoc/>
    public IObservable<PanelDetachId> PanelAttached => _attached;

    /// <inheritdoc/>
    public bool IsDetached(PanelDetachId panel) => _open.Contains(panel);

    /// <inheritdoc/>
    public void RegisterPanels(IReadOnlyList<DetachedPanelSlot> slots)
    {
    }

    /// <inheritdoc/>
    public Task RestoreDetachedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task ToggleDetachAsync(PanelDetachId panel, CancellationToken cancellationToken)
    {
        Toggles.Add(panel);
        return IsDetached(panel)
            ? AttachPanelWindowAsync(panel, cancellationToken)
            : DetachPanelWindowAsync(panel, cancellationToken);
    }

    /// <inheritdoc/>
    public Task DetachPanelWindowAsync(PanelDetachId panel, CancellationToken cancellationToken)
    {
        _open.Add(panel);
        _detached.OnNext(panel);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task AttachPanelWindowAsync(PanelDetachId panel, CancellationToken cancellationToken)
    {
        _open.Remove(panel);
        _attached.OnNext(panel);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task FocusPanelAsync(PanelDetachId panel, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc/>
    public void MarkShutdownRequested()
    {
    }
}
