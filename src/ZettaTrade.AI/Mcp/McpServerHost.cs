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
            _address = "stdio://local";

            _logger.LogInformation("MCP Server 'Zetta' started with {Tools} tools, {Resources} resources, {Prompts} prompts",
                _tools.Count, _resources.Count, _prompts.Count);

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
        var id = doc.RootElement.TryGetProperty("id", out var idP) ? idP.GetString() : null;

        string? response = method switch
        {
            "initialize" => """{"jsonrpc":"2.0","id":"1","result":{"protocolVersion":"2024-11-05","capabilities":{"tools":{}},"serverInfo":{"name":"Zetta","version":"0.1.0"}}}""",
            "tools/list" => await HandleToolsListAsync(id, ct),
            "tools/call" => await HandleToolCallAsync(doc.RootElement, id, ct),
            "resources/list" => await HandleResourcesListAsync(id, ct),
            "resources/read" => await HandleResourceReadAsync(doc.RootElement, id, ct),
            "prompts/list" => await HandlePromptsListAsync(id, ct),
            _ => null
        };

        if (response != null && id != null)
        {
            await Console.Out.WriteLineAsync(response.AsMemory(), ct);
            await Console.Out.FlushAsync(ct);
        }
    }

    private async Task<string> HandleToolsListAsync(string? id, CancellationToken ct)
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
            return JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result = new { tools } });
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<string> HandleToolCallAsync(JsonElement root, string? id, CancellationToken ct)
    {
        var name = root.GetProperty("params").GetProperty("name").GetString() ?? "";

        await _lock.WaitAsync(ct);
        try
        {
            if (!_tools.TryGetValue(name, out var tool))
            {
                return JsonSerializer.Serialize(new { jsonrpc = "2.0", id, error = new { code = -32601, message = "Unknown tool" } });
            }

            var arguments = root.GetProperty("params").TryGetProperty("arguments", out var args)
                ? args
                : (JsonElement?)null;

            var result = await tool.Handler(arguments, ct);
            return JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result = new { content = result.Content } });
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<string> HandleResourcesListAsync(string? id, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var resources = _resources.Values.Select(r => new { r.Uri, r.Name, r.MimeType });
            return JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result = new { resources } });
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<string> HandleResourceReadAsync(JsonElement root, string? id, CancellationToken ct)
    {
        var uri = root.GetProperty("params").GetProperty("uri").GetString() ?? "";

        await _lock.WaitAsync(ct);
        try
        {
            if (!_resources.TryGetValue(uri, out var resource))
            {
                return JsonSerializer.Serialize(new { jsonrpc = "2.0", id, error = new { code = -32601, message = "Unknown resource" } });
            }

            var content = await resource.Reader(ct);
            return JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result = new { contents = content } });
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<string> HandlePromptsListAsync(string? id, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var prompts = _prompts.Values.Select(p => new { p.Name, p.Description, p.Arguments });
            return JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result = new { prompts } });
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
