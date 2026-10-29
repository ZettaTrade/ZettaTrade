namespace ZettaTrade.AI.Mcp;

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Constants;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// MCP client implementation.
/// Connects to external MCP servers via stdio or Streamable HTTP transport.
/// </summary>
public sealed class McpClientService : IMcpClient
{
    private const int HttpTimeoutSeconds = 30;

    private readonly ILogger<McpClientService> _logger;
    private Process? _serverProcess;
    private HttpClient? _httpClient;
    private McpServerConfig? _config;
    private bool _isConnected;
    private bool _disposed;

    /// <summary>
    /// Create a new McpClientService instance.
    /// </summary>
    public McpClientService(ILogger<McpClientService> logger)
    {
        _logger = logger;
    }

    public string Name => _config?.Name ?? "Not connected";

    public bool IsConnected => _isConnected;

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
            case McpTransport.StreamableHttp:
                await ConnectStreamableHttpAsync(config, ct);
                break;
            default:
                throw new NotSupportedException($"Transport {config.Transport} is not supported");
        }

        _isConnected = true;
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

        _httpClient?.Dispose();
        _httpClient = null;
        _isConnected = false;

        _logger.LogInformation("Disconnected from MCP server {Name}", _config?.Name);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken ct = default)
    {
        _logger.LogDebug("Listing tools from MCP server {Name}", _config?.Name);
        return Task.FromResult<IReadOnlyList<McpToolInfo>>([]);
    }

    public async Task<McpToolResult> CallToolAsync(string toolName, JsonElement? parameters = null, CancellationToken ct = default)
    {
        _logger.LogDebug("Calling tool {Tool} on MCP server {Name}", toolName, _config?.Name);

        if (!IsConnected)
            return new McpToolResult(IsError: true, Content: [McpContent.CreateText("Not connected to MCP server")]);

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

        if (_config?.Transport == McpTransport.Stdio && _serverProcess is not null)
        {
            await _serverProcess.StandardInput.WriteLineAsync(json.AsMemory(), ct);
            await _serverProcess.StandardInput.FlushAsync(ct);

            var responseLine = await _serverProcess.StandardOutput.ReadLineAsync(ct);
            if (responseLine is null)
                return new McpToolResult(IsError: true, Content: [McpContent.CreateText("No response from server")]);

            using var doc = JsonDocument.Parse(responseLine);
            if (doc.RootElement.TryGetProperty("error", out var error))
                return new McpToolResult(IsError: true, Content: [McpContent.CreateText(error.ToString())]);

            var result = doc.RootElement.GetProperty("result");
            var content = result.TryGetProperty("content", out var c) ? c.ToString() : "";
            return new McpToolResult(IsError: false, Content: [McpContent.CreateText(content)]);
        }

        if (_httpClient is not null && _config?.Url is not null)
        {
            var httpContent = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync(_config.Url, httpContent, ct);
            var responseJson = await response.Content.ReadAsStringAsync(ct);

            using var doc = JsonDocument.Parse(responseJson);
            if (doc.RootElement.TryGetProperty("error", out var error))
                return new McpToolResult(IsError: true, Content: [McpContent.CreateText(error.ToString())]);

            var result = doc.RootElement.GetProperty("result");
            var content = result.TryGetProperty("content", out var c) ? c.ToString() : "";
            return new McpToolResult(IsError: false, Content: [McpContent.CreateText(content)]);
        }

        return new McpToolResult(IsError: true, Content: [McpContent.CreateText("No transport available")]);
    }

    public Task<IReadOnlyList<McpResourceInfo>> ListResourcesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<McpResourceInfo>>([]);

    public Task<McpResourceContent> ReadResourceAsync(string uri, CancellationToken ct = default)
        => Task.FromResult(new McpResourceContent(uri, [McpContent.CreateText("")]));

    public async IAsyncEnumerable<McpResourceUpdate> SubscribeResourceAsync(
        string uri,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield break;
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;

        _serverProcess?.Kill(entireProcessTree: true);
        _serverProcess?.Dispose();
        _httpClient?.Dispose();

        return ValueTask.CompletedTask;
    }

    private async Task ConnectStdioAsync(McpServerConfig config, CancellationToken ct)
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

        if (config.Environment is not null)
        {
            foreach (var (key, value) in config.Environment)
                _serverProcess.StartInfo.Environment[key] = value;
        }

        _serverProcess.Start();

        _ = Task.Run(async () =>
        {
            while (await _serverProcess.StandardError.ReadLineAsync() is { } line)
                _logger.LogDebug("[MCP:{Name} stderr] {Line}", config.Name, line);
        }, ct);

        await SendInitializeAsync(ct);
    }

    private async Task ConnectStreamableHttpAsync(McpServerConfig config, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(config.Url))
            throw new ArgumentException("URL is required for Streamable HTTP transport");

        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(config.Url),
            Timeout = TimeSpan.FromSeconds(HttpTimeoutSeconds),
        };

        if (!string.IsNullOrEmpty(config.AuthSecretRef))
        {
            _httpClient.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "placeholder-token");
        }

        _logger.LogInformation("Streamable HTTP transport connecting to {Url}", config.Url);
        await SendHttpInitializeAsync(ct);
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
                clientInfo = new { name = ApplicationConstants.ApplicationName, version = ApplicationConstants.ApplicationVersion },
            },
        };

        var json = JsonSerializer.Serialize(initRequest);
        await _serverProcess.StandardInput.WriteLineAsync(json.AsMemory(), ct);
        await _serverProcess.StandardInput.FlushAsync(ct);

        var response = await _serverProcess.StandardOutput.ReadLineAsync(ct);
        _logger.LogDebug("MCP init response: {Response}", response);

        var notification = """{"jsonrpc":"2.0","method":"notifications/initialized"}""";
        await _serverProcess.StandardInput.WriteLineAsync(notification.AsMemory(), ct);
        await _serverProcess.StandardInput.FlushAsync(ct);
    }

    private async Task SendHttpInitializeAsync(CancellationToken ct)
    {
        if (_httpClient is null) return;

        var initRequest = new
        {
            jsonrpc = "2.0",
            id = "init-1",
            method = "initialize",
            @params = new
            {
                protocolVersion = "2024-11-05",
                capabilities = new { },
                clientInfo = new { name = ApplicationConstants.ApplicationName, version = ApplicationConstants.ApplicationVersion },
            },
        };

        var json = JsonSerializer.Serialize(initRequest);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await _httpClient.PostAsync("", content, ct);
        var responseJson = await response.Content.ReadAsStringAsync(ct);
        _logger.LogDebug("MCP HTTP init response: {Response}", responseJson);

        var notification = new { jsonrpc = "2.0", method = "notifications/initialized" };
        var notifJson = JsonSerializer.Serialize(notification);
        var notifContent = new StringContent(notifJson, Encoding.UTF8, "application/json");
        await _httpClient.PostAsync("", notifContent, ct);
    }
}
