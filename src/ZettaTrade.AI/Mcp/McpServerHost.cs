namespace ZettaTrade.AI.Mcp;

using System.Text.Json;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// MCP Server host that exposes ZettaTrade functionality to AI models via stdio.
/// Presents itself as "Zetta" for a more personal interaction.
/// Uses SemaphoreSlim for thread-safe operations.
/// </summary>
public sealed class McpServerHost : IMcpServerHost, IAsyncDisposable
{
    #region Constants

    /// <summary>Server name presented to MCP clients.</summary>
    public const string ServerName = "Zetta";

    /// <summary>Server version reported in the initialize response.</summary>
    public const string ServerVersion = "0.1.0";

    /// <summary>Local stdio transport address exposed to the host application.</summary>
    private const string StdioAddress = "stdio://local";

    /// <summary>MCP protocol revision implemented by this host.</summary>
    private const string ProtocolVersion = "2024-11-05";

    /// <summary>JSON-RPC envelope version.</summary>
    private const string JsonRpcVersion = "2.0";

    /// <summary>JSON-RPC error code for unknown tools and resources.</summary>
    private const int UnknownRequestErrorCode = -32601;

    /// <summary>JSON-RPC method name for the MCP initialize handshake.</summary>
    private const string InitializeMethod = "initialize";

    /// <summary>JSON-RPC method name listing registered tools.</summary>
    private const string ToolsListMethod = "tools/list";

    /// <summary>JSON-RPC method name invoking a tool.</summary>
    private const string ToolsCallMethod = "tools/call";

    /// <summary>JSON-RPC method name listing registered resources.</summary>
    private const string ResourcesListMethod = "resources/list";

    /// <summary>JSON-RPC method name reading a resource.</summary>
    private const string ResourcesReadMethod = "resources/read";

    /// <summary>JSON-RPC method name listing registered prompts.</summary>
    private const string PromptsListMethod = "prompts/list";

    #endregion

    private readonly ILogger<McpServerHost> _logger;
    private readonly Dictionary<string, McpToolDefinition> _tools = new();
    private readonly Dictionary<string, McpResourceDefinition> _resources = new();
    private readonly Dictionary<string, McpPromptDefinition> _prompts = new();
    private readonly SemaphoreSlim _lock = new(1, 1);
    private CancellationTokenSource? _cts;
    private Task? _runLoopTask;
    private bool _isRunning;
    private string? _address;
    private bool _disposed;

    /// <summary>
    /// Create a new McpServerHost instance.
    /// </summary>
    public McpServerHost(ILogger<McpServerHost> logger)
    {
        _logger = logger;
    }

    public bool IsRunning => _isRunning;

    public string? Address => _address;

    public async Task StartAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_isRunning)
            {
                _logger.LogWarning("MCP Server is already running");
                return;
            }

            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _isRunning = true;
            _address = StdioAddress;

            _logger.LogInformation("MCP Server '{ServerName}' started with {Tools} tools, {Resources} resources, {Prompts} prompts",
                ServerName, _tools.Count, _resources.Count, _prompts.Count);

            // RunLoopAsync catches all non-cancellation failures internally; the continuation
            // below only logs a faulted loop, so no exception escapes unobserved.
            _runLoopTask = Task
                .Run(() => RunLoopAsync(_cts.Token), CancellationToken.None)
                .ContinueWith(
                    faulted => _logger.LogError(faulted.Exception!, "MCP server run loop faulted"),
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted,
                    TaskScheduler.Default);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (!_isRunning)
            {
                _logger.LogWarning("MCP Server is not running");
                return;
            }

            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            _isRunning = false;
            _address = null;

            _logger.LogInformation("MCP Server stopped");
        }
        finally
        {
            _lock.Release();
        }
    }

    public void RegisterTool(McpToolDefinition tool)
    {
        _lock.Wait();
        try
        {
            _tools[tool.Name] = tool;
            _logger.LogDebug("Registered MCP tool: {Name}", tool.Name);
        }
        finally
        {
            _lock.Release();
        }
    }

    public void RegisterResource(McpResourceDefinition resource)
    {
        _lock.Wait();
        try
        {
            _resources[resource.Uri] = resource;
            _logger.LogDebug("Registered MCP resource: {Uri}", resource.Uri);
        }
        finally
        {
            _lock.Release();
        }
    }

    public void RegisterPrompt(McpPromptDefinition prompt)
    {
        _lock.Wait();
        try
        {
            _prompts[prompt.Name] = prompt;
            _logger.LogDebug("Registered MCP prompt: {Name}", prompt.Name);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var line = await Console.In.ReadLineAsync(ct);
                if (line is null) break;

                await HandleRequestAsync(line, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MCP request error");
            }
        }
    }

    private async Task HandleRequestAsync(string json, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(json);
        var method = doc.RootElement.GetProperty("method").GetString();

        // The request id travels raw and verbatim: JSON-RPC 2.0 permits the
        // string, number and null id shapes, and every kind must echo back
        // byte-exact (GetString() returned null for a numeric id, which both
        // mislabeled the request a notification and would have corrupted the
        // echo). Clone() detaches the token from the disposed document; a
        // missing id is a true JSON-RPC notification and gets no reply.
        JsonElement? requestId = doc.RootElement.TryGetProperty("id", out var idP)
            ? idP.Clone()
            : null;

        string? response = method switch
        {
            InitializeMethod => BuildInitializeResponse(requestId),
            ToolsListMethod => await HandleToolsListAsync(requestId, ct),
            ToolsCallMethod => await HandleToolCallAsync(doc.RootElement, requestId, ct),
            ResourcesListMethod => await HandleResourcesListAsync(requestId, ct),
            ResourcesReadMethod => await HandleResourceReadAsync(doc.RootElement, requestId, ct),
            PromptsListMethod => await HandlePromptsListAsync(requestId, ct),
            _ => null
        };

        if (response != null && requestId.HasValue)
        {
            await Console.Out.WriteLineAsync(response.AsMemory(), ct);
            await Console.Out.FlushAsync(ct);
        }
    }

    /// <summary>
    /// Build the MCP initialize response from server constants (single source of truth),
    /// echoing the raw request id the client sent (numeric ids must round-trip).
    /// </summary>
    private static string BuildInitializeResponse(JsonElement? requestId) => JsonSerializer.Serialize(new
    {
        jsonrpc = JsonRpcVersion,
        id = requestId,
        result = new
        {
            protocolVersion = ProtocolVersion,
            capabilities = new { tools = new { } },
            serverInfo = new { name = ServerName, version = ServerVersion },
        },
    });

    private async Task<string> HandleToolsListAsync(JsonElement? id, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var tools = _tools.Values.Select(t => new
            {
                name = t.Name,
                description = t.Description,
                inputSchema = t.InputSchema
            });
            return JsonSerializer.Serialize(new { jsonrpc = JsonRpcVersion, id, result = new { tools } });
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<string> HandleToolCallAsync(JsonElement root, JsonElement? id, CancellationToken ct)
    {
        var name = root.GetProperty("params").GetProperty("name").GetString() ?? "";

        await _lock.WaitAsync(ct);
        try
        {
            if (!_tools.TryGetValue(name, out var tool))
            {
                return JsonSerializer.Serialize(new
                {
                    jsonrpc = JsonRpcVersion,
                    id,
                    error = new { code = UnknownRequestErrorCode, message = "Unknown tool" },
                });
            }

            var arguments = root.GetProperty("params").TryGetProperty("arguments", out var args)
                ? args
                : (JsonElement?)null;

            var result = await tool.Handler(arguments, ct);
            return JsonSerializer.Serialize(new { jsonrpc = JsonRpcVersion, id, result = new { content = result.Content } });
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<string> HandleResourcesListAsync(JsonElement? id, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var resources = _resources.Values.Select(r => new { r.Uri, r.Name, r.MimeType });
            return JsonSerializer.Serialize(new { jsonrpc = JsonRpcVersion, id, result = new { resources } });
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<string> HandleResourceReadAsync(JsonElement root, JsonElement? id, CancellationToken ct)
    {
        var uri = root.GetProperty("params").GetProperty("uri").GetString() ?? "";

        await _lock.WaitAsync(ct);
        try
        {
            if (!_resources.TryGetValue(uri, out var resource))
            {
                return JsonSerializer.Serialize(new
                {
                    jsonrpc = JsonRpcVersion,
                    id,
                    error = new { code = UnknownRequestErrorCode, message = "Unknown resource" },
                });
            }

            var content = await resource.Reader(ct);
            return JsonSerializer.Serialize(new { jsonrpc = JsonRpcVersion, id, result = new { contents = content } });
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<string> HandlePromptsListAsync(JsonElement? id, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var prompts = _prompts.Values.Select(p => new { p.Name, p.Description, p.Arguments });
            return JsonSerializer.Serialize(new { jsonrpc = JsonRpcVersion, id, result = new { prompts } });
        }
        finally
        {
            _lock.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;

        _disposed = true;

        // Stop the run loop before tearing down the primitives it uses.
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;

        _lock.Dispose();

        return ValueTask.CompletedTask;
    }
}
