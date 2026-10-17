namespace ZettaTrade.Core.Interfaces;

/// <summary>
/// MCP server host. Exposes ZettaTrade's trading context to AI models.
/// </summary>
public interface IMcpServerHost
{
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
/// </summary>
public sealed record McpToolDefinition
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required object InputSchema { get; init; }
    public required Func<object?, CancellationToken, Task<McpToolResult>> Handler { get; init; }
}

/// <summary>
/// Definition of an MCP resource exposed by ZettaTrade.
/// </summary>
public sealed record McpResourceDefinition
{
    public required string Uri { get; init; }
    public required string Name { get; init; }
    public string? MimeType { get; init; } = "application/json";
    public required Func<CancellationToken, Task<string>> Reader { get; init; }
}

/// <summary>
/// Definition of an MCP prompt template.
/// </summary>
public sealed record McpPromptDefinition
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required IReadOnlyList<McpPromptArgument> Arguments { get; init; }
    public required Func<Dictionary<string, string>, CancellationToken, Task<string>> Handler { get; init; }
}

public sealed record McpPromptArgument(string Name, string Description, bool Required = false);
