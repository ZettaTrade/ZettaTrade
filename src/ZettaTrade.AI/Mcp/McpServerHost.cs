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

    /// <summary>JSON-RPC error code for a malformed envelope (bad JSON).</summary>
    private const int ParseErrorCode = -32700;

    /// <summary>JSON-RPC error code for an invalid request (missing envelope members).</summary>
    private const int InvalidRequestErrorCode = -32600;

    /// <summary>JSON-RPC error code for invalid params (e.g. a missing tool name).</summary>
    private const int InvalidParamsErrorCode = -32602;

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

    /// <summary>The bounded wait for the run loop to leave its (uninterruptible) console read.</summary>
    private static readonly TimeSpan RunLoopStopWait = TimeSpan.FromSeconds(2);

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

            // The console stdio reader cannot be interrupted by the token: the
            // stop path gives the loop a BOUNDED wait to come home (between
            // reads it exits promptly) and only then tears the CTS down — the
            // pre-fix stop disposed while the loop could still be blocked
            // inside ReadLineAsync (BUG-056). A loop left blocked ends on its
            // own (the cancelled token is checked on every iteration); a
            // faulted loop is already logged by the OnlyOnFaulted continuation.
            if (_runLoopTask is { } loop)
            {
                await Task.WhenAny(loop, Task.Delay(RunLoopStopWait, ct));
            }

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
        string? response = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var requestId = TryGetRequestId(doc.RootElement);

            if (!doc.RootElement.TryGetProperty("method", out var methodEl))
            {
                // A malformed request carrying an id must be answered: the
                // pre-fix loop dropped it silently and the client waited
                // forever (BUG-056). A notification stays silent per JSON-RPC.
                response = requestId.HasValue
                    ? BuildErrorResponse(requestId, InvalidRequestErrorCode, "Invalid request: missing method")
                    : null;
            }
            else
            {
                var method = methodEl.GetString();
                response = method switch
                {
                    InitializeMethod => BuildInitializeResponse(requestId),
                    ToolsListMethod => await HandleToolsListAsync(requestId, ct),
                    ToolsCallMethod => await HandleToolCallAsync(doc.RootElement, requestId, ct),
                    ResourcesListMethod => await HandleResourcesListAsync(requestId, ct),
                    ResourcesReadMethod => await HandleResourceReadAsync(doc.RootElement, requestId, ct),
                    PromptsListMethod => await HandlePromptsListAsync(requestId, ct),
                    // An unknown method with an id must be answered too; a
                    // notification stays silent per JSON-RPC.
                    _ => requestId.HasValue
                        ? BuildErrorResponse(requestId, UnknownRequestErrorCode, $"Unknown method: {method}")
                        : null,
                };

                // The handlers build their envelopes unconditionally (the id
                // travels verbatim); an id-less line is a JSON-RPC
                // notification — processed, never answered — so its envelope
                // is swallowed here. The error branches above already guard on
                // HasValue, and an explicit id:null still echoes, per spec.
                if (!requestId.HasValue)
                {
                    response = null;
                }
            }
        }
        catch (JsonException ex)
        {
            // The failure goes to the honest logging channel AND the client
            // gets the spec's parse-error reply (id null, since bad JSON
            // cannot reliably carry an id); the loop stays alive for the
            // next request line.
            _logger.LogError(ex, "MCP request parse error");
            response = BuildErrorResponse(null, ParseErrorCode, "Parse error");
        }

        if (response != null)
        {
            await Console.Out.WriteLineAsync(response.AsMemory(), ct);
            await Console.Out.FlushAsync(ct);
        }
    }

    /// <summary>
    /// The request id travels raw and verbatim: the string, numeric and
    /// explicit-null shapes all echo byte-exact (the loose GetString()
    /// returned null for a numeric id, which both mislabeled the request a
    /// notification and corrupted the echo). Clone() detaches the token from
    /// the disposed document; a missing id is a JSON-RPC notification.
    /// </summary>
    private static JsonElement? TryGetRequestId(JsonElement root) =>
        root.TryGetProperty("id", out var idP) ? idP.Clone() : null;

    /// <summary>Build a JSON-RPC error envelope for one id.</summary>
    private static string BuildErrorResponse(JsonElement? id, int code, string message) =>
        JsonSerializer.Serialize(new { jsonrpc = JsonRpcVersion, id, error = new { code, message } });

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
        if (!root.TryGetProperty("params", out var @params) || !@params.TryGetProperty("name", out var nameEl))
            return BuildErrorResponse(id, InvalidParamsErrorCode, "Invalid params: missing tool name");

        var name = nameEl.GetString() ?? "";

        // The lock covers ONLY the synchronous registration-table read: the
        // tool handler runs OUTSIDE it (a tool may await a human confirmation
        // dialog), so a UI-thread registration never waits on a tool call
        // (BUG-020). The parsed document outlives this whole call, so the
        // arguments element stays valid across the await.
        JsonElement? arguments;
        McpToolDefinition? tool;
        await _lock.WaitAsync(ct);
        try
        {
            _tools.TryGetValue(name, out tool);
            arguments = @params.TryGetProperty("arguments", out var args)
                ? args
                : null;
        }
        finally
        {
            _lock.Release();
        }

        if (tool is null)
        {
            return BuildErrorResponse(id, UnknownRequestErrorCode, "Unknown tool");
        }

        var result = await tool.Handler(arguments, ct);
        return JsonSerializer.Serialize(new { jsonrpc = JsonRpcVersion, id, result = new { content = result.Content } });
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
        if (!root.TryGetProperty("params", out var @params) || !@params.TryGetProperty("uri", out var uriEl))
            return BuildErrorResponse(id, InvalidParamsErrorCode, "Invalid params: missing resource uri");

        var uri = uriEl.GetString() ?? "";

        McpResourceDefinition? resource;
        await _lock.WaitAsync(ct);
        try
        {
            _resources.TryGetValue(uri, out resource);
        }
        finally
        {
            _lock.Release();
        }

        // The registered reader may await slower backends: it runs OUTSIDE
        // the registration lock — the same release-before-await discipline
        // the tool call keeps (BUG-020).
        if (resource is null)
            return BuildErrorResponse(id, UnknownRequestErrorCode, "Unknown resource");

        var content = await resource.Reader(ct);
        return JsonSerializer.Serialize(new { jsonrpc = JsonRpcVersion, id, result = new { contents = content } });
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
