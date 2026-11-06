namespace ZettaTrade.AI.Mcp;

using ZettaTrade.Core.Interfaces;

/// <summary>
/// The lifecycle states one configured external MCP server rides, as the
/// settings tab renders them honestly:
/// <see cref="Disabled"/> — the entry is saved but switched off,
/// <see cref="Connecting"/> — the transport handshake is in flight,
/// <see cref="Connected"/> — the transport is live,
/// <see cref="Failed"/> — the last connect could not complete (the detail
/// names the reason; the saved choice stays in force for the next apply).
/// </summary>
public enum McpClientStateKind
{
    /// <summary>The entry is saved but not enabled.</summary>
    Disabled,

    /// <summary>The transport handshake is running.</summary>
    Connecting,

    /// <summary>The transport is live.</summary>
    Connected,

    /// <summary>The connect did not complete; the reason sits in the detail.</summary>
    Failed,
}

/// <summary>
/// One external MCP server's lifecycle row: the server's name (the key the
/// settings entries and the manager's connections share), its persisted
/// enable choice, the lifecycle state and the failure detail when state is
/// Failed.
/// </summary>
public sealed record McpClientState(
    string Name,
    bool Enabled,
    McpClientStateKind State,
    string? Detail);
