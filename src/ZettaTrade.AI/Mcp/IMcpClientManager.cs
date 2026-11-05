namespace ZettaTrade.AI.Mcp;

using ZettaTrade.Core.Interfaces;

/// <summary>
/// Owns the terminal's external MCP client connections: the settings tab
/// persists a server list and the manager runs every entry's connection —
/// startup connects the enabled entries (each through the same
/// <c>McpClientService.ConnectAsync</c> path), the settings save applies the
/// new list live (changed/removed/disabled entries disconnect, enabled ones
/// dial), and every entry's lifecycle surfaces as an honest state row the
/// tab renders. A connect failure never throws to the caller: it pins the
/// entry's Failed state and logs the reason (the tab never hangs on a dead
/// server — connects run off the startup walk).
/// </summary>
public interface IMcpClientManager : IAsyncInitializable
{
    /// <summary>
    /// The per-entry connection states as last observed — an immutable
    /// snapshot (BUG-047 discipline: the connect pipeline mutates only by
    /// reference replace).
    /// </summary>
    IReadOnlyList<McpClientState> States { get; }

    /// <summary>
    /// Stream of state snapshots; the current one replays on subscribe and
    /// every entry lifecycle change publishes a fresh snapshot.
    /// </summary>
    IObservable<IReadOnlyList<McpClientState>> StatesChanged { get; }

    /// <summary>
    /// Apply a new server list live: entries removed or disabled
    /// disconnect; unchanged enabled entries keep their connections; new
    /// (or changed) enabled entries connect in the background — the method
    /// returns after starting the work, the state rows land when the
    /// transports answer.
    /// </summary>
    Task ApplyAsync(IReadOnlyList<McpServerConfig> servers, CancellationToken ct = default);
}
