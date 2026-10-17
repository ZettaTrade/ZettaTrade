namespace ZettaTrade.AI.Mcp;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// MCP Server host that exposes ZettaTrade functionality to AI models.
/// Presents itself as "Zetta" for a more personal interaction.
/// </summary>
public sealed class McpServerHost : IMcpServerHost
{
    private readonly ILogger<McpServerHost> _logger;
    private readonly Dictionary<string, McpToolDefinition> _tools = [];
    private readonly Dictionary<string, McpResourceDefinition> _resources = [];
    private CancellationTokenSource? _cts;

    public McpServerHost(ILogger<McpServerHost> logger) => _logger = logger;

    public Task StartAsync(CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _logger.LogInformation("MCP Server 'Zetta' started with {Tools} tools", _tools.Count);
        _ = Task.Run(() => RunLoopAsync(_cts.Token));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct = default) { _cts?.Cancel(); return Task.CompletedTask; }
    public void RegisterTool(McpToolDefinition tool) { _tools[tool.Name] = tool; }
    public void RegisterResource(McpResourceDefinition resource) { _resources[resource.Uri] = resource; }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var line = await Console.In.ReadLineAsync(ct);
            if (line is null) break;
            try { await HandleRequestAsync(line, ct); } catch (Exception ex) { _logger.LogError(ex, "MCP request error"); }
        }
    }

    private async Task HandleRequestAsync(string json, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(json);
        var method = doc.RootElement.GetProperty("method").GetString();
        var id = doc.RootElement.TryGetProperty("id", out var idP) ? idP.GetString() : null;
        
        // Present as "Zetta" for friendly AI interaction
        string? response = method switch
        {
            "initialize" => """{"jsonrpc":"2.0","id":"1","result":{"protocolVersion":"2024-11-05","capabilities":{"tools":{}},"serverInfo":{"name":"Zetta","version":"0.1.0"}}}""",
            "tools/list" => JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result = new { tools = _tools.Values.Select(t => new { t.Name, t.Description }) } }),
            "tools/call" => await HandleToolCall(doc.RootElement, id, ct),
            _ => null
        };
        if (response != null && id != null) { await Console.Out.WriteLineAsync(response.AsMemory(), ct); await Console.Out.FlushAsync(ct); }
    }

    private async Task<string> HandleToolCall(JsonElement root, string? id, CancellationToken ct)
    {
        var name = root.GetProperty("params").GetProperty("name").GetString() ?? "";
        if (!_tools.TryGetValue(name, out var tool)) return JsonSerializer.Serialize(new { jsonrpc = "2.0", id, error = "Unknown tool" });
        var result = await tool.Handler(null, ct);
        return JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result = new { content = result.Content } });
    }
}
