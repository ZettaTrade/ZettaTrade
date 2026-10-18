namespace ZettaTrade.Core.Interfaces;

using System.Text.Json;

/// <summary>
/// MCP server host. Exposes ZettaTrade's trading context to AI models.
/// </summary>
public interface IMcpServerHost
{
    /// <summary>Whether the server is currently running.</summary>
    bool IsRunning { get; }
    
    /// <summary>Server address (for HTTP transports).</summary>
    string? Address { get; }

    /// <summary>Start the MCP server (listen for incoming connections).</summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>Stop the MCP server.</summary>
    Task StopAsync(CancellationToken ct = default);

    /// <summary>Register a tool that AI can call.</summary>
    void RegisterTool(McpToolDefinition tool);

    /// <summary>Register a resource that AI can read.</summary>
    void RegisterResource(McpResourceDefinition resource);

    /// <summary>Register a prompt template.</summary>
    void RegisterPrompt(McpPromptDefinition prompt);
}

/// <summary>
/// Definition of an MCP tool exposed by ZettaTrade.
/// Handler receives JsonElement for type-safe parameter access.
/// </summary>
public sealed record McpToolDefinition
{
    /// <summary>Tool name.</summary>
    public required string Name { get; init; }
    
    /// <summary>Tool description.</summary>
    public required string Description { get; init; }
    
    /// <summary>JSON schema for tool input.</summary>
    public required JsonElement InputSchema { get; init; }
    
    /// <summary>Tool handler function.</summary>
    public required Func<JsonElement?, CancellationToken, Task<McpToolResult>> Handler { get; init; }
}

/// <summary>
/// Definition of an MCP resource exposed by ZettaTrade.
/// </summary>
public sealed record McpResourceDefinition
{
    /// <summary>Resource URI.</summary>
    public required string Uri { get; init; }
    
    /// <summary>Resource name.</summary>
    public required string Name { get; init; }
    
    /// <summary>MIME type.</summary>
    public string? MimeType { get; init; } = "application/json";
    
    /// <summary>Resource reader function.</summary>
    public required Func<CancellationToken, Task<IReadOnlyList<McpContent>>> Reader { get; init; }
}

/// <summary>
/// Definition of an MCP prompt template.
/// </summary>
public sealed record McpPromptDefinition
{
    /// <summary>Prompt name.</summary>
    public required string Name { get; init; }
    
    /// <summary>Prompt description.</summary>
    public required string Description { get; init; }
    
    /// <summary>Prompt arguments.</summary>
    public required IReadOnlyList<McpPromptArgument> Arguments { get; init; }
    
    /// <summary>Prompt handler function.</summary>
    public required Func<Dictionary<string, string>, CancellationToken, Task<string>> Handler { get; init; }
}

/// <summary>
/// Argument for an MCP prompt.
/// </summary>
public sealed record McpPromptArgument(string Name, string Description, bool Required = false);
