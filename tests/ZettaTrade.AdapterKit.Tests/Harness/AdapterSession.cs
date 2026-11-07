namespace ZettaTrade.AdapterKit.Tests.Harness;

using Microsoft.Extensions.DependencyInjection;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// One open conformance session: the adapter under test plus the rigs it
/// rides. The battery opens exactly one session per fact and disposes it
/// afterwards, so facts never share adapter state with each other — the
/// battery's judgment stays per-fact, the way a fresh adapter start behaves.
/// </summary>
public sealed class AdapterSession : IAsyncDisposable
{
    #region Fields

    /// <summary>The service provider the canned REST disguise lives in, if this pack's stack needs one.</summary>
    private readonly ServiceProvider? _restProvider;

    /// <summary>The loopback rig a transcript pack dials, if this pack's stack needs one.</summary>
    private readonly KitLoopbackServer? _server;

    /// <summary>Whether the adapter passed to DisposeAsync was already consumed (it is disposed exactly once).</summary>
    private bool _disposed;

    #endregion

    #region Constructor

    private AdapterSession(
        IMarketDataAdapter adapter,
        KitLoopbackServer? server,
        ServiceProvider? restProvider)
    {
        Adapter = adapter;
        _server = server;
        _restProvider = restProvider;
    }

    #endregion

    #region Properties

    /// <summary>The adapter under judgment.</summary>
    public IMarketDataAdapter Adapter { get; }

    /// <summary>The loopback rig the transcript legs ride; absent for packs without transport (the demo's inline surface).</summary>
    public KitLoopbackServer? Server => _server;

    #endregion

    #region Methods: Factory

    /// <summary>Assemble a conformance session from already-built parts.</summary>
    /// <param name="adapter">The adapter under judgment.</param>
    /// <param name="server">The rig, when the pack has a transport leg.</param>
    /// <param name="restProvider">The canned-REST provider, when the pack's stack needs one.</param>
    public static AdapterSession Open(
        IMarketDataAdapter adapter,
        KitLoopbackServer? server = null,
        ServiceProvider? restProvider = null) => new(adapter, server, restProvider);

    #endregion

    #region IAsyncDisposable

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await Adapter.DisposeAsync();
        if (_server is not null)
        {
            await _server.DisposeAsync();
        }

        _restProvider?.Dispose();
    }

    #endregion
}
