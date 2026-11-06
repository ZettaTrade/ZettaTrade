namespace ZettaTrade.AI.Mcp;

using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// Owns the terminal's external MCP client connections: the settings tab
/// persists a server list and the manager runs every entry's connection —
/// startup connects the enabled entries (each through the same
/// <c>McpClientService.ConnectAsync</c> path), the settings save applies the
/// new list live (changed/removed/disabled entries disconnect, enabled ones
/// dial), and every entry's lifecycle surfaces as an honest state row the
/// tab renders. A connect failure never throws to the caller: it pins the
/// entry's Failed state and logs the reason. The connect work runs off the
/// startup walk behind a bounded grace await, so a dead server pins its
/// Failed state without ever hanging the boot.
/// </summary>
public sealed class McpClientManager : IMcpClientManager
{
    #region Constants

    /// <summary>
    /// The settings section the persisted external-server list lives under
    /// (a section of its own, per the T-068 config contract). The settings
    /// tab writes it with the same constants.
    /// </summary>
    public const string ServersSection = "McpClients";

    /// <summary>The settings key of the persisted list value (the compact JSON the config list parses).</summary>
    public const string ServersKey = "Servers";

    /// <summary>The persisted value applied until a stored list is loaded: no entries.</summary>
    public const string DefaultServersJson = "[]";

    /// <summary>
    /// The bounded grace the apply waits for in-flight connects: a healthy
    /// loopback/stdio handshake lands inside it (its state is deterministic
    /// right after the apply returns), a dead server's own timeout outlives
    /// the grace and lands its Failed row in the background later.
    /// </summary>
    private static readonly TimeSpan ConnectGrace = TimeSpan.FromSeconds(2);

    #endregion

    #region Private Fields

    private readonly ISettingsService _settings;
    private readonly ISecretStore _secrets;
    private readonly ILogger<McpClientManager> _logger;
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>The live clients by entry name.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, McpClientService> _clients = new(StringComparer.Ordinal);

    /// <summary>The last connection handles (the tasks are fully handled: the body catches every failure and lands the terminal state itself).</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task> _connects = new(StringComparer.Ordinal);

    /// <summary>
    /// Serializes the per-entry state amendments: two connects landing
    /// concurrently each rebuild the snapshot from the last published one —
    /// a gate keeps those read-modify-publish cycles from losing an update.
    /// </summary>
    private readonly SemaphoreSlim _stateGate = new(1, 1);

    /// <summary>The last state snapshot (reference-swapped only).</summary>
    private volatile IReadOnlyList<McpClientState> _states =
        (IReadOnlyList<McpClientState>)Array.Empty<McpClientState>();

    private readonly BehaviorSubject<IReadOnlyList<McpClientState>> _statesChanged;

    /// <summary>The configuration each connection was built from; the reconcile compares against it.</summary>
    private System.Collections.Concurrent.ConcurrentDictionary<string, McpServerConfig> _applied = new(StringComparer.Ordinal);

    #endregion

    #region Constructor

    /// <summary>
    /// Create a new McpClientManager instance. The startup walk calls
    /// <see cref="InitializeAsync"/> after the settings service loaded.
    /// </summary>
    /// <param name="settings">The app settings service; the walk reads the server list from it.</param>
    /// <param name="secrets">The secret store every HTTP auth reference resolves from.</param>
    /// <param name="logger">The manager's logger.</param>
    /// <param name="loggerFactory">The per-client logger source.</param>
    public McpClientManager(
        ISettingsService settings,
        ISecretStore secrets,
        ILogger<McpClientManager> logger,
        ILoggerFactory loggerFactory)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _statesChanged = new BehaviorSubject<IReadOnlyList<McpClientState>>(_states);
    }

    #endregion

    #region Properties

    /// <inheritdoc />
    public IReadOnlyList<McpClientState> States => _states;

    /// <inheritdoc />
    public IObservable<IReadOnlyList<McpClientState>> StatesChanged => _statesChanged;

    #endregion

    #region Public Methods

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var persisted = await _settings.GetAsync<string>(
            ServersSection,
            ServersKey,
            DefaultServersJson);
        var (servers, problems) = McpServerConfigList.Parse(persisted);

        foreach (var problem in problems)
        {
            _logger.LogWarning("The persisted MCP client list skipped a row: {Problem}", problem);
        }

        _logger.LogInformation(
            "The MCP client list loaded: {Count} entries, {Enabled} enabled",
            servers.Count,
            servers.Count(server => server.Enabled));

        await ApplyAsync(servers, ct);
    }

    /// <inheritdoc />
    public async Task ApplyAsync(IReadOnlyList<McpServerConfig> servers, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(servers);

        var next = new System.Collections.Concurrent.ConcurrentDictionary<string, McpServerConfig>(StringComparer.Ordinal);
        foreach (var server in servers)
        {
            if (!next.TryAdd(server.Name, server))
            {
                _logger.LogWarning(
                    "Two MCP client entries share the name {Name}; the duplicate is dropped (the first one wins)",
                    server.Name);
            }
        }

        var keepsConnection = new HashSet<string>(StringComparer.Ordinal);
        var states = new List<McpClientState>(next.Count);

        foreach (var (name, server) in next)
        {
            if (!server.Enabled)
            {
                states.Add(new McpClientState(name, false, McpClientStateKind.Disabled, Detail: null));
                continue;
            }

            if (KeepsConnection(name, server))
            {
                keepsConnection.Add(name);
                states.Add(new McpClientState(name, true, McpClientStateKind.Connected, Detail: null));
                continue;
            }

            states.Add(new McpClientState(name, true, McpClientStateKind.Connecting, Detail: null));
        }

        // Disconnect everything the new list dropped, disabled or changed —
        // an unchanged enabled entry keeps its live connection.
        foreach (var (name, client) in _clients)
        {
            if (!keepsConnection.Contains(name))
            {
                await TearDownAsync(name, client);
            }
        }

        _applied = next;
        await PublishStatesAsync(states);

        // Connect (or reconnect) the newly enabled and changed entries in
        // the background; the apply waits a bounded grace so a fast, healthy
        // handshake lands its Connected row before the call returns, while a
        // dead server pends past the grace and lands its Failed row later
        // (the caller never blocks on it).
        var connecting = new List<Task>();
        foreach (var (name, server) in next)
        {
            if (server.Enabled && !keepsConnection.Contains(name))
            {
                connecting.Add(BeginConnect(name, server));
            }
        }

        if (connecting.Count > 0)
        {
            await Task.WhenAny(Task.WhenAll(connecting), Task.Delay(ConnectGrace));
        }
    }

    #endregion

    #region Implementation Details

    /// <summary>
    /// Whether the current applied config already holds this exact
    /// connection with a live client and the honest Connected state: such an
    /// entry survives a save unchanged instead of redialing.
    /// </summary>
    private bool KeepsConnection(string name, McpServerConfig server) =>
        _applied.TryGetValue(name, out var appliedServer) &&
        SameConnection(appliedServer, server) &&
        _clients.ContainsKey(name) &&
        _states.FirstOrDefault(state => string.Equals(state.Name, name, StringComparison.Ordinal))
            is { State: McpClientStateKind.Connected };

    /// <summary>
    /// Start one entry's connect backgrounded and return its tracked handle:
    /// the body catches every failure the client's connect can raise and
    /// lands the entry's terminal state itself, so the tracked task can
    /// never fault out of sight.
    /// </summary>
    private Task BeginConnect(string name, McpServerConfig server)
    {
        var client = _clients.GetOrAdd(
            name,
            _ => CreateClient());

        _logger.LogInformation("The MCP client {Name} is connecting via {Transport}", name, server.Transport);

        var connect = Task.Run(
            async () =>
            {
                try
                {
                    await client.ConnectAsync(server);
                    await AmendStateAsync(
                        name,
                        client,
                        server.Enabled,
                        client.IsConnected ? McpClientStateKind.Connected : McpClientStateKind.Failed,
                        client.IsConnected ? null : "The transport did not come up");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "The MCP client {Name} failed to connect", name);
                    await AmendStateAsync(name, client, server.Enabled, McpClientStateKind.Failed, ex.Message);
                }
            },
            CancellationToken.None);

        _connects[name] = connect;
        return connect;
    }

    private McpClientService CreateClient() =>
        new(
            _loggerFactory.CreateLogger<McpClientService>(),
            _secrets);

    /// <summary>Disconnect and dispose one entry's client; a failure never throws to the caller.</summary>
    private async Task TearDownAsync(string name, McpClientService client)
    {
        _clients.TryRemove(name, out _);
        _connects.TryRemove(name, out _);

        try
        {
            await client.DisconnectAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The MCP client {Name} could not disconnect cleanly", name);
        }

        try
        {
            await client.DisposeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The MCP client {Name} could not dispose cleanly", name);
        }
    }

    /// <summary>
    /// Whether two configs describe the same connection under one name: the
    /// reconcile keeps such entries' live clients (an unchanged save never
    /// redials).
    /// </summary>
    private static bool SameConnection(McpServerConfig left, McpServerConfig right) =>
        string.Equals(left.Name, right.Name, StringComparison.Ordinal) &&
        left.Transport == right.Transport &&
        string.Equals(left.Command, right.Command, StringComparison.Ordinal) &&
        string.Equals(left.Url, right.Url, StringComparison.Ordinal);

    /// <summary>
    /// Amend one entry's row inside the snapshot (by reference swap) and
    /// publish: serialized under the state gate so concurrent amends never
    /// lose an update, and dropped when it belongs to a torn-down connect
    /// (the client is no longer the entries' current one — the saved stale
    /// terminal must never overwrite a fresh connection's state).
    /// </summary>
    private async Task AmendStateAsync(
        string name,
        McpClientService client,
        bool enabled,
        McpClientStateKind kind,
        string? detail)
    {
        await _stateGate.WaitAsync();
        try
        {
            var current = _clients.TryGetValue(name, out var held) ? held : null;

            if (!ReferenceEquals(current, client))
            {
                return;
            }

            var states = _states.ToDictionary(
                state => state.Name,
                state => state,
                StringComparer.Ordinal);
            states[name] = new McpClientState(name, enabled, kind, detail);
            Publish(states.Values.ToList());
        }
        finally
        {
            _stateGate.Release();
        }
    }

    /// <summary>Publish a whole new snapshot (the apply's authoritative rebuild) — under the same gate the amends take.</summary>
    private async Task PublishStatesAsync(IReadOnlyList<McpClientState> states)
    {
        await _stateGate.WaitAsync();
        try
        {
            Publish(states);
        }
        finally
        {
            _stateGate.Release();
        }
    }

    private void Publish(IReadOnlyList<McpClientState> states)
    {
        _states = states;
        _statesChanged.OnNext(states);
    }

    #endregion
}
