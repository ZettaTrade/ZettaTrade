namespace ZettaTrade.AI.Mcp;

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// MCP client implementation.
/// Connects to external MCP servers via stdio or SSE transport.
/// </summary>
public sealed class McpClientService : IMcpClient
{
    private readonly ILogger<McpClientService> _logger;
    private Process? _serverProcess;
    private McpServerConfig? _config;
    private bool _disposed;

    public McpClientService(ILogger<McpClientService> logger)
    {
        _logger = logger;
    }

    public string Name => _config?.Name ?? "Not connected";
    public bool IsConnected => _serverProcess is { HasExited: false };

    public async Task ConnectAsync(McpServerConfig config, CancellationToken ct = default)
    {
        _config = config;

        if (!config.Enabled)
        {
            _logger.LogInformation("MCP server {Name} is disabled, skipping", config.Name);
            return;
        }

        switch (config.Transport)
        {
            case McpTransport.Stdio:
                await ConnectStdioAsync(config, ct);
                break;
            case McpTransport.Sse:
                await ConnectSseAsync(config, ct);
                break;
            default:
                throw new NotSupportedException($"Transport {config.Transport} is not supported");
        }

        _logger.LogInformation("Connected to MCP server {Name} via {Transport}", config.Name, config.Transport);
    }

    public Task DisconnectAsync(CancellationToken ct = default)
    {
        if (_serverProcess is { HasExited: false })
        {
            _serverProcess.Kill(entireProcessTree: true);
            _serverProcess.Dispose();
            _serverProcess = null;
        }

        _logger.LogInformation("Disconnected from MCP server {Name}", _config?.Name);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken ct = default)
    {
        // In production: send JSON-RPC "tools/list" request to server
        // Return parsed response
        _logger.LogDebug("Listing tools from MCP server {Name}", _config?.Name);
        return Task.FromResult<IReadOnlyList<McpToolInfo>>([]);
    }

    public async Task<McpToolResult> CallToolAsync(string toolName, object? parameters = null, CancellationToken ct = default)
    {
        _logger.LogDebug("Calling tool {Tool} on MCP server {Name}", toolName, _config?.Name);

        if (_serverProcess?.HasExited != false)
            return new McpToolResult(IsError: true, Content: "Not connected to MCP server");

        // Build JSON-RPC request
        var request = new
        {
            jsonrpc = "2.0",
            id = Guid.NewGuid().ToString(),
            method = "tools/call",
            @params = new
            {
                name = toolName,
                arguments = parameters,
            },
        };

        var json = JsonSerializer.Serialize(request);
        var message = json + "\n";

        // Send to server's stdin
        await _serverProcess.StandardInput.WriteAsync(message.AsMemory(), ct);
        await _serverProcess.StandardInput.FlushAsync(ct);

        // Read response from stdout
        var responseLine = await _serverProcess.StandardOutput.ReadLineAsync(ct);
        if (responseLine is null)
            return new McpToolResult(IsError: true, Content: "No response from server");

        // Parse JSON-RPC response
        using var doc = JsonDocument.Parse(responseLine);
        if (doc.RootElement.TryGetProperty("error", out var error))
        {
            return new McpToolResult(IsError: true, Content: error.ToString());
        }

        var result = doc.RootElement.GetProperty("result");
        var content = result.TryGetProperty("content", out var c) ? c.ToString() : "";

        return new McpToolResult(IsError: false, Content: content);
    }

    public Task<IReadOnlyList<McpResourceInfo>> ListResourcesAsync(CancellationToken ct = default)
    {
        // JSON-RPC "resources/list"
        return Task.FromResult<IReadOnlyList<McpResourceInfo>>([]);
    }

    public Task<McpResourceContent> ReadResourceAsync(string uri, CancellationToken ct = default)
    {
        // JSON-RPC "resources/read"
        return Task.FromResult(new McpResourceContent(uri, "", "application/json"));
    }

    public async IAsyncEnumerable<McpResourceUpdate> SubscribeResourceAsync(
        string uri,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        // JSON-RPC "resources/subscribe" + stream notifications
        await Task.CompletedTask;
        yield break;
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;

        _serverProcess?.Kill(entireProcessTree: true);
        _serverProcess?.Dispose();

        return ValueTask.CompletedTask;
    }

    private Task ConnectStdioAsync(McpServerConfig config, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(config.Command))
            throw new ArgumentException("Command is required for stdio transport");

        _serverProcess = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = config.Command,
                Arguments = string.Join(" ", config.Args ?? []),
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            },
        };

        // Set environment variables
        if (config.Environment is not null)
        {
            foreach (var (key, value) in config.Environment)
                _serverProcess.StartInfo.Environment[key] = value;
        }

        _serverProcess.Start();

        // Log stderr in background
        _ = Task.Run(async () =>
        {
            while (await _serverProcess.StandardError.ReadLineAsync() is { } line)
            {
                _logger.LogDebug("[MCP:{Name} stderr] {Line}", config.Name, line);
            }
        }, ct);

        // Send MCP initialize handshake
        return SendInitializeAsync(ct);
    }

    private Task ConnectSseAsync(McpServerConfig config, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(config.Url))
            throw new ArgumentException("URL is required for SSE transport");

        // In production: use HttpClient with SSE support
        // Connect to config.Url, handle Server-Sent Events
        _logger.LogInformation("SSE transport connecting to {Url}", config.Url);

        return Task.CompletedTask;
    }

    private async Task SendInitializeAsync(CancellationToken ct)
    {
        if (_serverProcess is null) return;

        var initRequest = new
        {
            jsonrpc = "2.0",
            id = "init-1",
            method = "initialize",
            @params = new
            {
                protocolVersion = "2024-11-05",
                capabilities = new { },
                clientInfo = new
                {
                    name = "ZettaTrade",
                    version = "0.1.0",
                },
            },
        };

        var json = JsonSerializer.Serialize(initRequest);
        await _serverProcess.StandardInput.WriteLineAsync(json.AsMemory(), ct);
        await _serverProcess.StandardInput.FlushAsync(ct);

        // Read init response
        var response = await _serverProcess.StandardOutput.ReadLineAsync(ct);
        _logger.LogDebug("MCP init response: {Response}", response);

        // Send initialized notification
        var notification = """{"jsonrpc":"2.0","method":"notifications/initialized"}""";
        await _serverProcess.StandardInput.WriteLineAsync(notification.AsMemory(), ct);
        await _serverProcess.StandardInput.FlushAsync(ct);
    }
}
