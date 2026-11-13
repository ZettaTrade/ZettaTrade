namespace ZettaTrade.AI.Mcp;

using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// MCP Server host that exposes ZettaTrade functionality to AI models via stdio.
/// Presents itself as "Zetta" for a more personal interaction.
/// The registries are concurrent collections (an upsert never blocks the
/// stop window); the semaphore still serializes the start/stop lifecycle and
/// every request-handler read of the registries.
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

    /// <summary>JSON-RPC error code for a faulted request handler (a server
    /// bug must never hang the client — the honest internal-error envelope).</summary>
    private const int InternalErrorErrorCode = -32603;

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

    /// <summary>JSON-RPC method name fetching one prompt's rendered messages.</summary>
    private const string PromptsGetMethod = "prompts/get";

    #endregion

    private readonly ILogger<McpServerHost> _logger;
    private readonly ConcurrentDictionary<string, McpToolDefinition> _tools = new();
    private readonly ConcurrentDictionary<string, McpResourceDefinition> _resources = new();
    private readonly ConcurrentDictionary<string, McpPromptDefinition> _prompts = new();
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
        // Lockless registration: the registries are concurrent collections, so
        // an upsert never awaits the host's semaphore — nothing blocks even in
        // the StopAsync window where the semaphore is held across its bounded
        // run-loop wait. (The register-before-start contract still stands.)
        _tools[tool.Name] = tool;
        _logger.LogDebug("Registered MCP tool: {Name}", tool.Name);
    }

    public void RegisterResource(McpResourceDefinition resource)
    {
        _resources[resource.Uri] = resource;
        _logger.LogDebug("Registered MCP resource: {Uri}", resource.Uri);
    }

    public void RegisterPrompt(McpPromptDefinition prompt)
    {
        _prompts[prompt.Name] = prompt;
        _logger.LogDebug("Registered MCP prompt: {Name}", prompt.Name);
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
                    PromptsGetMethod => await HandlePromptsGetAsync(doc.RootElement, requestId, ct),
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

            // Every registry surface the host implements is advertised
            // honestly: tools, resources and prompts (T-208) - a client that
            // trusts the advertisement then walks the surface and finds every
            // category answered, never a promised-absent one.
            capabilities = new { tools = new { }, resources = new { }, prompts = new { } },
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

        // A handler fault is a server bug, but the client must never hang on
        // one: the fault earns the JSON-RPC internal-error envelope and the
        // honest log (the well-behaved handler answers IsError results and
        // never reaches this net).
        McpToolResult result;
        try
        {
            result = await tool.Handler(arguments, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MCP tool '{Name}' handler faulted", name);
            return BuildErrorResponse(id, InternalErrorErrorCode, "Internal error");
        }

        return JsonSerializer.Serialize(new { jsonrpc = JsonRpcVersion, id, result = new { content = result.Content } });
    }

    private async Task<string> HandleResourcesListAsync(JsonElement? id, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            // The wire shape is the MCP camelCase one (uri, name, mimeType):
            // the records' default PascalCase serialization would hand a
            // spec-trusting client keys it cannot read (T-208).
            var resources = _resources.Values.Select(r => new
            {
                uri = r.Uri,
                name = r.Name,
                mimeType = r.MimeType,
            });
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

        IReadOnlyList<McpContent> content;
        try
        {
            content = await resource.Reader(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MCP resource '{Uri}' reader faulted", uri);
            return BuildErrorResponse(id, InternalErrorErrorCode, "Internal error");
        }

        // The wire shape is the MCP camelCase content block (type, text,
        // data, mimeType): the record's PascalCase default would hand a
        // spec-trusting client keys it cannot read (T-208).
        var contents = content
            .Select(c => new
            {
                type = c.Type,
                text = c.TextValue,
                data = c.Data,
                mimeType = c.MimeType,
            })
            .ToList();

        return JsonSerializer.Serialize(new { jsonrpc = JsonRpcVersion, id, result = new { contents } });
    }

    private async Task<string> HandlePromptsListAsync(JsonElement? id, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            // CamelCase on the wire again: a prompt argument rides the
            // protocol's name/description/required keys (T-208).
            var prompts = _prompts.Values.Select(p => new
            {
                name = p.Name,
                description = p.Description,
                arguments = p.Arguments.Select(a => new
                {
                    name = a.Name,
                    description = a.Description,
                    required = a.Required,
                }),
            });
            return JsonSerializer.Serialize(new { jsonrpc = JsonRpcVersion, id, result = new { prompts } });
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Fetch one prompt's rendered messages (T-208): the named prompt's
    /// handler runs with the request's string arguments and the rendered
    /// text rides back as ONE user message — the 2024-11-05 shape. The
    /// registry read holds the lock; the handler runs OUTSIDE it (the same
    /// release-before-await discipline the tool call keeps, BUG-020). A
    /// missing name or a non-string argument member is invalid params; an
    /// unregistered prompt is the unknown-request error; a required
    /// argument the request omits is invalid params naming it (the honest
    /// refusal — a silently substituted argument would render a prompt the
    /// template never wrote).
    /// </summary>
    private async Task<string> HandlePromptsGetAsync(JsonElement root, JsonElement? id, CancellationToken ct)
    {
        if (!root.TryGetProperty("params", out var @params))
            return BuildErrorResponse(id, InvalidParamsErrorCode, "Invalid params: missing params");

        if (!@params.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String)
            return BuildErrorResponse(id, InvalidParamsErrorCode, "Invalid params: missing prompt name");

        var name = nameEl.GetString() ?? "";

        Dictionary<string, string> arguments = new(StringComparer.Ordinal);
        if (@params.TryGetProperty("arguments", out var argsEl))
        {
            if (argsEl.ValueKind != JsonValueKind.Object)
                return BuildErrorResponse(id, InvalidParamsErrorCode, "Invalid params: arguments must be an object");

            foreach (var arg in argsEl.EnumerateObject())
            {
                if (arg.Value.ValueKind != JsonValueKind.String)
                    return BuildErrorResponse(
                        id,
                        InvalidParamsErrorCode,
                        $"Invalid params: the argument '{arg.Name}' must be a string");

                arguments[arg.Name] = arg.Value.GetString()!;
            }
        }

        McpPromptDefinition? prompt;
        await _lock.WaitAsync(ct);
        try
        {
            _prompts.TryGetValue(name, out prompt);
        }
        finally
        {
            _lock.Release();
        }

        if (prompt is null)
            return BuildErrorResponse(id, UnknownRequestErrorCode, "Unknown prompt");

        var missing = prompt.Arguments
            .Where(a => a.Required && !arguments.ContainsKey(a.Name))
            .Select(a => a.Name)
            .FirstOrDefault();
        if (missing is not null)
            return BuildErrorResponse(
                id,
                InvalidParamsErrorCode,
                $"Invalid params: missing required argument '{missing}'");

        string rendered;
        try
        {
            rendered = await prompt.Handler(arguments, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MCP prompt '{Name}' handler faulted", name);
            return BuildErrorResponse(id, InternalErrorErrorCode, "Internal error");
        }

        return JsonSerializer.Serialize(new
        {
            jsonrpc = JsonRpcVersion,
            id,
            result = new
            {
                description = prompt.Description,
                messages = new[]
                {
                    new
                    {
                        role = "user",
                        content = new { type = "text", text = rendered },
                    },
                },
            },
        });
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
