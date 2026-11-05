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
    #region Constants

    private const int HttpTimeoutSeconds = 30;

    /// <summary>The initialize request's id (its reply is matched by id like any other).</summary>
    private const string InitializeRequestId = "init-1";

    /// <summary>The bounded wait for the stderr drain to leave its (uninterruptible) read.</summary>
    private static readonly TimeSpan DrainStopWait = TimeSpan.FromSeconds(2);

    #endregion

    private readonly ILogger<McpClientService> _logger;
    private readonly ISecretStore _secrets;
    private Process? _serverProcess;
    private bool _serverStarted;
    private Task? _stderrDrain;
    private HttpClient? _httpClient;
    private McpServerConfig? _config;
    private bool _isConnected;
    private bool _disposed;

    /// <summary>
    /// Create a new McpClientService instance.
    /// </summary>
    /// <param name="logger">The class logger.</param>
    /// <param name="secrets">The secret store every HTTP auth reference resolves from (BUG-024).</param>
    public McpClientService(ILogger<McpClientService> logger, ISecretStore secrets)
    {
        _logger = logger;
        _secrets = secrets;
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

        // Reconnecting while connected tears the previous connection down
        // first: the pre-fix code overwrote _serverProcess/_httpClient and
        // leaked the previous child process (BUG-022).
        if (_isConnected)
        {
            await DisconnectAsync(ct);
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

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        await TeardownAsync();

        _logger.LogInformation("Disconnected from MCP server {Name}", _config?.Name);
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

        var requestId = Guid.NewGuid().ToString();

        var request = new
        {
            jsonrpc = "2.0",
            id = requestId,
            method = "tools/call",
            @params = new
            {
                name = toolName,
                arguments = parameters,
            },
        };

        var json = JsonSerializer.Serialize(request);

        if (_config?.Transport == McpTransport.Stdio && _serverProcess is { } process)
        {
            await process.StandardInput.WriteLineAsync(json.AsMemory(), ct);
            await process.StandardInput.FlushAsync(ct);

            var reply = await ReadMatchingReplyAsync(process.StandardOutput, requestId, ct);
            return reply ?? new McpToolResult(IsError: true, Content: [McpContent.CreateText("No response from server")]);
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

    /// <summary>
    /// Read stdout lines until the reply whose id matches the request: a
    /// non-JSON line, a notification or another request's reply is SKIPPED —
    /// the reply matching tolerates stream desynchronization (BUG-023). The
    /// child's pipe closing (EOF) yields null. Internal for the framing
    /// test suite (InternalsVisibleTo ZettaTrade.App.Tests).
    /// </summary>
    internal async Task<McpToolResult?> ReadMatchingReplyAsync(
        StreamReader stdout,
        string requestId,
        CancellationToken ct)
    {
        while (true)
        {
            var line = await stdout.ReadLineAsync(ct);
            if (line is null)
            {
                return null;
            }

            var reply = ExtractReply(line, requestId);
            if (reply is not null)
            {
                return reply;
            }

            _logger.LogDebug("MCP: skipped a non-matching stdout line while waiting for {Id}", requestId);
        }
    }

    /// <summary>Extract a reply for one request id from one envelope line; null for strangers.</summary>
    private McpToolResult? ExtractReply(string line, string requestId)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return null;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("id", out var idEl) ||
                !string.Equals(idEl.GetString(), requestId, StringComparison.Ordinal))
            {
                return null;
            }

            if (root.TryGetProperty("error", out var error))
            {
                return new McpToolResult(IsError: true, Content: [McpContent.CreateText(error.ToString())]);
            }

            var result = root.TryGetProperty("result", out var r) ? r : (JsonElement?)null;
            var content = result.HasValue && result.Value.TryGetProperty("content", out var c)
                ? c.ToString()
                : "";
            return new McpToolResult(IsError: false, Content: [McpContent.CreateText(content)]);
        }
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

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        // The async teardown keeps disposal silent for every failure mode the
        // teardown already guards (a never-started process must not make
        // Kill throw out of disposal — BUG-022); the stderr drain gets a
        // bounded wait (BUG-021).
        await TeardownAsync();
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
        _serverStarted = true;

        StartStderrDrain(config.Name);

        await SendInitializeAsync(ct);
    }

    /// <summary>
    /// Start the stderr drain the connection owns: the child's stderr pipe
    /// must never fill (a filled pipe blocks the server process), so the
    /// drain runs for the connection's whole life and AWAITS its
    /// cancellation or completion at teardown — the pre-fix fire-and-forget
    /// task was unobserved and could die silently (BUG-021).
    /// </summary>
    private void StartStderrDrain(string serverName)
    {
        if (_serverProcess is not { } process)
        {
            return;
        }

        _stderrDrain = Task.Run(async () =>
        {
            try
            {
                // The loop reads until the pipe closes: the teardown kills
                // the child FIRST, which closes the pipe and ends the loop,
                // so no cancellation token rides a half-consumed line here.
                while (await process.StandardError.ReadLineAsync() is { } line)
                {
                    _logger.LogDebug("[MCP:{Name} stderr] {Line}", serverName, line);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[MCP:{Name} stderr] drain ended unexpectedly", serverName);
            }
        });
    }

    /// <summary>
    /// Tear the current connection down: kill a STARTED child (a
    /// never-started process must not make Kill throw out of disposal —
    /// BUG-022), wait for the stderr drain bounded (BUG-021), and dispose the
    /// HTTP client. Every step is idempotent.
    /// </summary>
    private async Task TeardownAsync()
    {
        if (_serverProcess is { } process)
        {
            if (_serverStarted && !process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "The MCP child process could not be killed");
                }
            }

            process.Dispose();
            _serverProcess = null;
        }

        _serverStarted = false;

        if (_stderrDrain is { } drain)
        {
            try
            {
                // The bounded wait: the pipe's close usually ends the reader
                // instantly; a wedged reader never holds the teardown.
                await Task.WhenAny(drain, Task.Delay(DrainStopWait));
            }
            finally
            {
                _stderrDrain = null;
            }
        }

        _httpClient?.Dispose();
        _httpClient = null;
        _isConnected = false;
    }

    private async Task ConnectStreamableHttpAsync(McpServerConfig config, CancellationToken ct)
    {
        var (endpoint, timeoutSeconds) = ValidateHttpOptions(config);

        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(endpoint),
            Timeout = TimeSpan.FromSeconds(timeoutSeconds),
        };

        if (!string.IsNullOrEmpty(config.AuthSecretRef))
        {
            // The secret travels only through the secret store — never as a
            // process argument, never a hardcoded placeholder (BUG-024). A
            // missing secret warns and connects unauthenticated (the server
            // then rejects with its own error).
            var secret = await _secrets.GetAsync(config.AuthSecretRef, ct);
            if (string.IsNullOrEmpty(secret))
            {
                _logger.LogWarning(
                    "MCP server {Name}: the secret {Ref} is unavailable; connecting without authentication",
                    config.Name,
                    config.AuthSecretRef);
            }
            else
            {
                _httpClient.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", secret);
            }
        }

        _logger.LogInformation("Streamable HTTP transport connecting to {Url}", config.Url);
        await SendHttpInitializeAsync(ct);
    }

    /// <summary>
    /// Validate the HTTP entry before anything dials: an absolute
    /// http/https URL (a bad endpoint is a LOUD argument exception the
    /// caller pins as the entry's own error state, never a silent
    /// substitution) and a positive timeout override.
    /// </summary>
    /// <returns>The resolved endpoint and the effective timeout in seconds.</returns>
    private (string Endpoint, int TimeoutSeconds) ValidateHttpOptions(McpServerConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.Url))
        {
            throw new ArgumentException("URL is required for Streamable HTTP transport");
        }

        if (!Uri.TryCreate(config.Url, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException(
                $"MCP server '{config.Name}': '{config.Url}' is not a usable http/https endpoint");
        }

        var timeout = config.HttpTimeoutSeconds ?? HttpTimeoutSeconds;
        if (timeout <= 0)
        {
            throw new ArgumentException(
                $"MCP server '{config.Name}': the HTTP timeout {timeout}s is not a positive value");
        }

        return (endpoint.ToString(), timeout);
    }

    private async Task SendInitializeAsync(CancellationToken ct)
    {
        if (_serverProcess is null) return;

        var initRequest = new
        {
            jsonrpc = "2.0",
            id = InitializeRequestId,
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

        var reply = await ReadMatchingReplyAsync(_serverProcess.StandardOutput, InitializeRequestId, ct);
        _logger.LogDebug("MCP init response received: {Received}", reply is not null);

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

        // The initialize reply is the connect's whole handshake: a status
        // error or a malformed body is a LOUD failure (the manager pins it
        // as the entry's Failed state), never a silent "connection looks
        // fine" the tools would later face-fail on.
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"MCP server '{_config?.Name}' replied {response.StatusCode} to the initialize request");
        }

        var responseJson = await response.Content.ReadAsStringAsync(ct);

        try
        {
            using var document = JsonDocument.Parse(responseJson);
            _logger.LogDebug("MCP HTTP init response parsed: {Response}", responseJson);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"MCP server '{_config?.Name}' replied to the initialize request with malformed JSON",
                ex);
        }

        var notification = new { jsonrpc = "2.0", method = "notifications/initialized" };
        var notifJson = JsonSerializer.Serialize(notification);
        var notifContent = new StringContent(notifJson, Encoding.UTF8, "application/json");
        var notifResponse = await _httpClient.PostAsync("", notifContent, ct);

        if (!notifResponse.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"MCP server '{_config?.Name}' replied {notifResponse.StatusCode} to the initialized notification");
        }
    }
}
