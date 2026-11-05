namespace ZettaTrade.App.Services;

using System;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ZettaTrade.App;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// The lifecycle state of the local MCP server, as the settings window's MCP
/// tab renders it honestly:
/// <see cref="Stopped"/> — the server exists but takes no clients,
/// <see cref="Running"/> — the stdio transport is live,
/// <see cref="Failed"/> — an apply could not reach the server (a start leaves
/// the choice stored, the next launch applies it).
/// </summary>
public enum McpHostState
{
    /// <summary>The server is registered but not started.</summary>
    Stopped,

    /// <summary>The server is live.</summary>
    Running,

    /// <summary>The last apply did not reach the server; the state is stored.</summary>
    Failed,
}

/// <summary>
/// Runs the local MCP server per the user's enabled choice: the startup walk
/// reads <c>Mcp:ServerEnabled</c> and starts the host when stored enabled, the
/// settings window's save applies the flip live (start/stop in-process), and
/// every transition (or its failure) surfaces as the state stream's current
/// value. The service owns no business logic of the server itself — the host
/// and its registrar stay untouched.
/// </summary>
public sealed class McpHostService : IAsyncInitializable
{
    #region Private Fields

    private readonly ISettingsService _settings;
    private readonly IMcpServerHost _host;
    private readonly ILogger<McpHostService> _logger;

    /// <summary>
    /// The lifecycle state as last observed. The host is an app-lifetime
    /// singleton, so a replaying subject carries both the surfacing the
    /// settings tab reads and the initial value the first subscriber sees.
    /// </summary>
    private readonly BehaviorSubject<McpHostState> _state;

    #endregion

    #region Constructor

    /// <summary>
    /// Create a new McpHostService instance.
    /// </summary>
    /// <param name="settings">The app settings service; the walk reads the
    /// enable choice from it.</param>
    /// <param name="host">The stdio MCP server host to start and stop.</param>
    /// <param name="logger">The class logger.</param>
    public McpHostService(
        ISettingsService settings,
        IMcpServerHost host,
        ILogger<McpHostService> logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // The host may already hold state (a resumed session); the truth is
        // the host's own flag until the stored choice applies.
        _state = new BehaviorSubject<McpHostState>(
            host.IsRunning ? McpHostState.Running : McpHostState.Stopped);
    }

    #endregion

    #region Properties

    /// <summary>The lifecycle state stream (the current value replays at subscribe).</summary>
    public IObservable<McpHostState> State => _state;

    /// <summary>Whether the server is currently running (the host's own claim).</summary>
    public bool IsRunning => _host.IsRunning;

    #endregion

    #region Public Methods

    /// <inheritdoc/>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var enabled = await _settings.GetAsync<bool>(
            UiSettingsKeys.McpSection,
            UiSettingsKeys.McpServerEnabled,
            UiSettingsKeys.DefaultMcpServerEnabled);

        if (!enabled)
        {
            _state.OnNext(McpHostState.Stopped);
            return;
        }

        await StartHostAsync("startup", ct);
    }

    /// <summary>
    /// Apply the user's enable choice live: start when enabled (the host
    /// no-ops on an unnecessary start), stop when not. A failure never throws
    /// to the caller — the state surfaces it and the log names it; a start
    /// failure leaves the stored choice in force for the next launch.
    /// </summary>
    public async Task ApplyAsync(bool enabled, CancellationToken ct = default)
    {
        if (enabled)
        {
            await StartHostAsync("apply", ct);
            return;
        }

        await StopHostAsync();
    }

    #endregion

    #region Private Methods

    private async Task StartHostAsync(string origin, CancellationToken ct)
    {
        try
        {
            await _host.StartAsync(ct);
            _state.OnNext(McpHostState.Running);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _state.OnNext(McpHostState.Failed);
            _logger.LogError(
                ex,
                "The MCP server could not start at {Origin}; the stored enable choice applies on the next launch",
                origin);
        }
    }

    private async Task StopHostAsync()
    {
        try
        {
            await _host.StopAsync();
            _state.OnNext(McpHostState.Stopped);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A failed stop leaves the server's own claim in charge — the
            // state stays honest even when the stop did not land.
            _state.OnNext(_host.IsRunning ? McpHostState.Running : McpHostState.Stopped);
            _logger.LogWarning(ex, "The MCP server could not stop");
        }
    }

    #endregion
}
