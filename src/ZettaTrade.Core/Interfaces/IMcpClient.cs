namespace ZettaTrade.Core.Interfaces;

/// <summary>
/// MCP (Model Context Protocol) client.
/// Connects to external MCP servers to get additional context for AI.
/// </summary>
public interface IMcpClient : IAsyncDisposable
{
    /// <summary>Name of this MCP connection.</summary>
    string Name { get; }

    /// <summary>Whether the client is connected.</summary>
    bool IsConnected { get; }

    /// <summary>Connect to an MCP server.</summary>
    Task ConnectAsync(McpServerConfig config, CancellationToken ct = default);

    /// <summary>Disconnect from the MCP server.</summary>
    Task DisconnectAsync(CancellationToken ct = default);

    /// <summary>List available tools on the remote server.</summary>
    Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken ct = default);

    /// <summary>Call a tool on the remote server.</summary>
    Task<McpToolResult> CallToolAsync(string toolName, object? parameters = null, CancellationToken ct = default);

    /// <summary>List available resources on the remote server.</summary>
    Task<IReadOnlyList<McpResourceInfo>> ListResourcesAsync(CancellationToken ct = default);

    /// <summary>Read a resource from the remote server.</summary>
    Task<McpResourceContent> ReadResourceAsync(string uri, CancellationToken ct = default);

    /// <summary>Subscribe to resource updates (streaming).</summary>
    IAsyncEnumerable<McpResourceUpdate> SubscribeResourceAsync(string uri, CancellationToken ct = default);
}

/// <summary>
/// Configuration for connecting to an MCP server.
/// </summary>
public sealed record McpServerConfig
{
    public required string Name { get; init; }
    public required McpTransport Transport { get; init; }

    /// <summary>For stdio transport: command to run.</summary>
    public string? Command { get; init; }

    /// <summary>For stdio transport: command arguments.</summary>
    public string[]? Args { get; init; }

    /// <summary>For SSE transport: server URL.</summary>
    public string? Url { get; init; }

    /// <summary>Reference to a secret for authentication (e.g., "secret://api-key").</summary>
    public string? AuthSecretRef { get; init; }

    /// <summary>Whether this server is enabled.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Additional environment variables for stdio transport.</summary>
    public Dictionary<string, string>? Environment { get; init; }
}

public enum McpTransport
{
    Stdio = 0,
    Sse = 1,
}

public sealed record McpToolInfo(string Name, string Description, object? InputSchema);
public sealed record McpToolResult(bool IsError, string Content);
public sealed record McpResourceInfo(string Uri, string Name, string? MimeType);
public sealed record McpResourceContent(string Uri, string Content, string? MimeType);
public sealed record McpResourceUpdate(string Uri, string Content);
