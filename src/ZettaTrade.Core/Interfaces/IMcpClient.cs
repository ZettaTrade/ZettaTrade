namespace ZettaTrade.Core.Interfaces;

using System.Text.Json;

/// <summary>
/// MCP (Model Context Protocol) client.
/// Connects to external MCP servers to get additional context for AI.
/// Supports Streamable HTTP transport (replaces deprecated SSE).
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
    Task<McpToolResult> CallToolAsync(string toolName, JsonElement? parameters = null, CancellationToken ct = default);

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
    /// <summary>Server name.</summary>
    public required string Name { get; init; }

    /// <summary>Transport type (Stdio or StreamableHttp).</summary>
    public required McpTransport Transport { get; init; }

    /// <summary>For stdio transport: command to run.</summary>
    public string? Command { get; init; }

    /// <summary>For stdio transport: command arguments.</summary>
    public string[]? Args { get; init; }

    /// <summary>For Streamable HTTP transport: server URL.</summary>
    public string? Url { get; init; }

    /// <summary>Reference to a secret for authentication (e.g., "secret://api-key").</summary>
    public string? AuthSecretRef { get; init; }

    /// <summary>Whether this server is enabled.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Additional environment variables for stdio transport.</summary>
    public Dictionary<string, string>? Environment { get; init; }
}

/// <summary>
/// MCP transport type.
/// Note: SSE is deprecated in favor of Streamable HTTP.
/// </summary>
public enum McpTransport
{
    /// <summary>Standard input/output transport.</summary>
    Stdio = 0,
    /// <summary>Streamable HTTP transport.</summary>
    StreamableHttp = 1,
}

/// <summary>
/// Information about an MCP tool.
/// </summary>
public sealed record McpToolInfo(string Name, string Description, JsonElement? InputSchema);

/// <summary>
/// Result of an MCP tool call.
/// Supports both text and binary content.
/// </summary>
public sealed record McpToolResult(bool IsError, IReadOnlyList<McpContent> Content);

/// <summary>
/// Content block in MCP response.
/// Supports text, image, and other binary formats.
/// </summary>
public sealed record McpContent
{
    /// <summary>Content type (e.g., "text", "image", "binary").</summary>
    public required string Type { get; init; }

    /// <summary>Text content (for text type).</summary>
    public string? TextValue { get; init; }

    /// <summary>Binary data (for binary/image types).</summary>
    public byte[]? Data { get; init; }

    /// <summary>MIME type (for binary/image types).</summary>
    public string? MimeType { get; init; }

    /// <summary>Create a text content block.</summary>
    public static McpContent CreateText(string text) => new() { Type = "text", TextValue = text };

    /// <summary>Create a binary content block.</summary>
    public static McpContent CreateBinary(byte[] data, string mimeType) =>
        new() { Type = "binary", Data = data, MimeType = mimeType };
}

/// <summary>
/// Information about an MCP resource.
/// </summary>
public sealed record McpResourceInfo(string Uri, string Name, string? MimeType);

/// <summary>
/// Content of an MCP resource.
/// </summary>
public sealed record McpResourceContent(string Uri, IReadOnlyList<McpContent> Content);

/// <summary>
/// Update for an MCP resource.
/// </summary>
public sealed record McpResourceUpdate(string Uri, IReadOnlyList<McpContent> Content);
