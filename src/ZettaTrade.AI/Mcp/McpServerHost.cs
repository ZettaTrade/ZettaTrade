namespace ZettaTrade.AI.Mcp;

using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// MCP Server host that exposes ZettaTrade functionality to AI models via HTTP.
/// Uses localhost with bearer token authentication.
/// </summary>
public sealed class McpServerHost : IMcpServerHost, IAsyncDisposable
{
    private readonly ILogger<McpServerHost> _logger;
    private readonly Dictionary<string, McpToolDefinition> _tools = new();
    private readonly Dictionary<string, McpResourceDefinition> _resources = new();
    private readonly Dictionary<string, McpPromptDefinition> _prompts = new();
    private readonly SemaphoreSlim _lock = new(1, 1);
    private WebApplication? _app;
    private bool _isRunning;
    private string? _address;
    private string? _authToken;
    private bool _disposed;

    public McpServerHost(ILogger<McpServerHost> logger)
    {
        _logger = logger;
    }

    public bool IsRunning => _isRunning;
    public string? Address => _address;

    public async Task StartAsync(int port = 5050, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_isRunning)
            {
                _logger.LogWarning("MCP Server is already running");
                return;
            }

            // Generate auth token
            _authToken = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls($"http://localhost:{port}");

            _app = builder.Build();

            // Authentication middleware
            _app.Use(async (context, next) =>
            {
                var authHeader = context.Request.Headers.Authorization.ToString();
                if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer ") || authHeader[7..] != _authToken)
                {
                    context.Response.StatusCode = 401;
                    await context.Response.WriteAsync("Unauthorized");
                    return;
                }
                await next();
            });

            // MCP endpoints
            _app.MapGet("/mcp/tools", async (HttpContext context) =>
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
                    await context.Response.WriteAsJsonAsync(new { tools });
                }
                finally
                {
                    _lock.Release();
                }
            });

            _app.MapPost("/mcp/tools/{name}", async (HttpContext context, string name) =>
            {
                var args = await JsonSerializer.DeserializeAsync<JsonElement>(context.Request.Body, cancellationToken: ct);

                await _lock.WaitAsync(ct);
                try
                {
                    if (!_tools.TryGetValue(name, out var tool))
                    {
                        context.Response.StatusCode = 404;
                        await context.Response.WriteAsJsonAsync(new { error = "Tool not found" });
                        return;
                    }

                    var result = await tool.Handler(args, ct);
                    await context.Response.WriteAsJsonAsync(result);
                }
                finally
                {
                    _lock.Release();
                }
            });

            _app.MapGet("/mcp/resources", async (HttpContext context) =>
            {
                await _lock.WaitAsync(ct);
                try
                {
                    var resources = _resources.Values.Select(r => new { r.Uri, r.Name, r.MimeType });
                    await context.Response.WriteAsJsonAsync(new { resources });
                }
                finally
                {
                    _lock.Release();
                }
            });

            _app.MapGet("/mcp/resources/{*uri}", async (HttpContext context, string uri) =>
            {
                await _lock.WaitAsync(ct);
                try
                {
                    if (!_resources.TryGetValue(uri, out var resource))
                    {
                        context.Response.StatusCode = 404;
                        await context.Response.WriteAsJsonAsync(new { error = "Resource not found" });
                        return;
                    }

                    var content = await resource.Reader(ct);
                    await context.Response.WriteAsJsonAsync(new { contents = content });
                }
                finally
                {
                    _lock.Release();
                }
            });

            await _app.StartAsync(ct);

            _isRunning = true;
            _address = $"http://localhost:{port}";

            _logger.LogInformation("MCP Server 'Zetta' started at {Address} with token: {Token}", _address, _authToken);
            _logger.LogWarning("Use this token for authentication: {Token}", _authToken);
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

            if (_app is not null)
            {
                await _app.StopAsync(ct);
                await _app.DisposeAsync();
                _app = null;
            }

            _isRunning = false;
            _address = null;
            _authToken = null;

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

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_app is not null)
        {
            await _app.DisposeAsync();
            _app = null;
        }

        _lock.Dispose();
    }
}
