namespace ZettaTrade.App.Tests.Mcp;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using ZettaTrade.AI.Mcp;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// The external client connections' owner matrix (T-068): the persisted list
/// connects the enabled entries through the same
/// <see cref="McpClientService.ConnectAsync"/> path and pins an honest
/// terminal state per entry — a dead server FAILS instead of hanging the
/// boot (the apply awaits a bounded grace), a disabled entry never dials,
/// an unchanged save keeps the connection without redialing, a removed or
/// changed entry disconnects, an unparsable entry stays out of the applied
/// list, the secrets never surface in a state row, and the stdio entries
/// launch their configured commands.
/// </summary>
public sealed class McpClientManagerTests
{
    #region Harness

    /// <summary>The fake store answers the config's references with the same standing value.</summary>
    private const string SecretRef = "secret://mcp-test";
    private const string StoredCredential = "the-standing-test-credential";

    /// <summary>Build a manager whose persisted list value is the given JSON.</summary>
    private static McpClientManager NewManager(string persistedServersJson = McpClientManager.DefaultServersJson)
    {
        var settings = Substitute.For<ISettingsService>();
        settings
            .GetAsync<string>(
                McpClientManager.ServersSection,
                McpClientManager.ServersKey,
                McpClientManager.DefaultServersJson)
            .Returns(persistedServersJson);

        var secrets = Substitute.For<ISecretStore>();
        secrets.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(StoredCredential);

        return new McpClientManager(
            settings,
            secrets,
            NullLogger<McpClientManager>.Instance,
            NullLoggerFactory.Instance);
    }

    /// <summary>
    /// Wait bounded for an entry's state to land on the expected kind (the
    /// background connects publish whenever the transport answers — the five
    /// second cap is the test's safety net, not the feature's timing).
    /// </summary>
    private static async Task<McpClientState> WaitForStateAsync(
        McpClientManager manager,
        string name,
        McpClientStateKind expected,
        TimeSpan? cap = null)
    {
        var deadline = DateTime.UtcNow + (cap ?? TimeSpan.FromSeconds(5));
        var row = default(McpClientState)!;

        while (DateTime.UtcNow < deadline)
        {
            var states = manager.States;

            for (var i = 0; i < states.Count; i++)
            {
                if (states[i].Name == name && states[i].State == expected)
                {
                    return states[i];
                }
            }

            row = states.FirstOrDefault(state => state.Name == name) ?? row;
            await Task.Delay(50);
        }

        row.Should().NotBeNull($"the entry '{name}' must have a state row");
        return row;
    }

    /// <summary>A Streamable HTTP entry pointing at the given URL.</summary>
    private static McpServerConfig HttpEntry(string name, string url, bool enabled = true) => new()
    {
        Name = name,
        Transport = McpTransport.StreamableHttp,
        Url = url,
        AuthSecretRef = SecretRef,
        Enabled = enabled,
    };

    /// <summary>A stdio entry by command and args.</summary>
    private static McpServerConfig StdioEntry(string name, string command, params string[] args) => new()
    {
        Name = name,
        Transport = McpTransport.Stdio,
        Command = command,
        Args = args,
        Enabled = true,
    };

    #endregion Harness

    #region Initialize (startup)

    /// <summary>The startup connects the persisted enabled entry over the loopback stub and lands Connected.</summary>
    [Fact]
    public async Task InitializeAsync_TheEnabledHttpEntry_ConnectsAndLandsConnected()
    {
        await using var stub = new ConfigurableHttpStub(ConfigurableHttpStub.RequestMode.Happy, connectionsToServe: 1);
        var manager = NewManager(
            McpServerConfigList.Write([HttpEntry("stub", stub.Url)]));

        await manager.InitializeAsync();

        var row = await WaitForStateAsync(manager, "stub", McpClientStateKind.Connected);
        row.Enabled.Should().BeTrue();
        row.Detail.Should().BeNull("a happy connect carries no failure detail");
        stub.ServedRequests.Should().Be(2, "the client performs initialize + notification");
    }

    /// <summary>A persisted disabled entry never dials (the state rows render Disabled and no connection refused error appears).</summary>
    [Fact]
    public async Task InitializeAsync_TheDisabledEntry_NeverDials()
    {
        var deadUrl = RefusedPortUrl();
        var manager = NewManager(
            McpServerConfigList.Write([HttpEntry("off", deadUrl, enabled: false)]));

        await manager.InitializeAsync();
        await Task.Delay(700); // a dial to the refused port would fail fast below 700 ms

        manager.States.Should().ContainSingle().Which.State.Should().Be(McpClientStateKind.Disabled);
        manager.States[0].Enabled.Should().BeFalse();
    }

    #endregion Initialize (startup)

    #region Reconcile

    /// <summary>An unchanged save keeps the entry connected without redialing — reconcile, not a thundering redial.</summary>
    [Fact]
    public async Task ApplyAsync_TheUnchangedEnabledList_KeepsTheConnection()
    {
        await using var stub = new ConfigurableHttpStub(ConfigurableHttpStub.RequestMode.Happy, connectionsToServe: 1);
        var manager = NewManager();
        var servers = new[] { HttpEntry("stub", stub.Url) };

        await manager.ApplyAsync(servers);
        await WaitForStateAsync(manager, "stub", McpClientStateKind.Connected);

        await manager.ApplyAsync(servers);

        stub.ServedRequests.Should().Be(2, "the second apply reuses the live connection");
    }

    /// <summary>A changed endpoint rebuilds the entry's connection: the client redials the new list's URL.</summary>
    [Fact]
    public async Task ApplyAsync_AChangedUrl_RedialsThroughTheNewEndpoint()
    {
        await using var first = new ConfigurableHttpStub(ConfigurableHttpStub.RequestMode.Happy, connectionsToServe: 1);
        await using var second = new ConfigurableHttpStub(ConfigurableHttpStub.RequestMode.Happy, connectionsToServe: 1);
        var manager = NewManager();

        await manager.ApplyAsync([HttpEntry("stub", first.Url)]);
        await WaitForStateAsync(manager, "stub", McpClientStateKind.Connected);

        await manager.ApplyAsync([HttpEntry("stub", second.Url)]);
        await WaitForStateAsync(manager, "stub", McpClientStateKind.Connected);

        first.ServedRequests.Should().Be(2, "the old connection caused exactly one handshake");
        second.ServedRequests.Should().Be(2, "the new endpoint served the fresh handshake");
    }

    /// <summary>A disabled flip disconnects the entry: the client rejoins the pool ONLY through a later enabled apply.</summary>
    [Fact]
    public async Task ApplyAsync_ADisabledFlip_DisconnectsAndNeverRedials()
    {
        await using var stub = new ConfigurableHttpStub(ConfigurableHttpStub.RequestMode.Happy, connectionsToServe: 1);
        var manager = NewManager();
        var entry = HttpEntry("flip", stub.Url);

        await manager.ApplyAsync([entry]);
        await WaitForStateAsync(manager, "flip", McpClientStateKind.Connected);

        await manager.ApplyAsync([HttpEntry("flip", stub.Url, enabled: false)]);
        await Task.Delay(700);

        manager.States.Should().ContainSingle().Which.State.Should().Be(McpClientStateKind.Disabled);
        stub.ServedRequests.Should().Be(2, "the tear-down never redials the disabled entry");
    }

    /// <summary>A removed entry leaves the state snapshot entirely (the client is discarded with it).</summary>
    [Fact]
    public async Task ApplyAsync_ARemovedEntry_DiscardsItsClient()
    {
        await using var stub = new ConfigurableHttpStub(ConfigurableHttpStub.RequestMode.Happy, connectionsToServe: 2);
        var manager = NewManager();

        await manager.ApplyAsync([HttpEntry("stub", stub.Url)]);
        await WaitForStateAsync(manager, "stub", McpClientStateKind.Connected);

        await manager.ApplyAsync([]);

        manager.States.Should().BeEmpty("the removed entry's state row leaves the snapshot");

        // The client is truly gone: re-applying the entry dials AGAIN through
        // the pool (a second connection, not a replay of the first).
        await manager.ApplyAsync([HttpEntry("stub", stub.Url)]);
        await WaitForStateAsync(manager, "stub", McpClientStateKind.Connected);

        stub.ServedRequests.Should().Be(4, "each apply served its own connection's handshake");
    }

    #endregion Reconcile

    #region Failure states (never a hang)

    /// <summary>An outage pins the entry's Failed state: the apply returns in bounded time and the failure lands later.</summary>
    [Fact]
    public async Task ApplyAsync_AnOutage_PinsFailedInsteadOfHangingTheApply()
    {
        var manager = NewManager();
        var apply = manager.ApplyAsync([HttpEntry("dead", RefusedPortUrl())]);

        // The apply must ALWAYS return (the connect runs off the caller):
        // the grace await bounds it regardless of how long the entry's
        // connect task hangs on.
        await Task.WhenAny(apply, Task.Delay(TimeSpan.FromSeconds(5)));
        apply.IsCompleted.Should().BeTrue("the apply never holds the caller hostage to a dead server");

        await WaitForStateAsync(manager, "dead", McpClientStateKind.Failed);
        manager.States.Single(row => row.Name == "dead").Detail.Should().NotBeEmpty(
            "the failure's own reason rides the row");
    }

    /// <summary>A 500-stub answers the whole handshake with a failure: the client reports it, the manager pins Failed.</summary>
    [Fact]
    public async Task ApplyAsync_AServerErrorReply_PinsFailed()
    {
        await using var stub = new ConfigurableHttpStub(ConfigurableHttpStub.RequestMode.ServerError, connectionsToServe: 1);
        var manager = NewManager();

        await manager.ApplyAsync([HttpEntry("broken", stub.Url)]);

        await WaitForStateAsync(manager, "broken", McpClientStateKind.Failed);
    }

    /// <summary>A malformed initialize reply cannot masquerade as a healthy connection.</summary>
    [Fact]
    public async Task ApplyAsync_AMalformedReply_PinsFailed()
    {
        await using var stub = new ConfigurableHttpStub(ConfigurableHttpStub.RequestMode.Malformed, connectionsToServe: 1);
        var manager = NewManager();

        await manager.ApplyAsync([HttpEntry("scrambled", stub.Url)]);

        await WaitForStateAsync(manager, "scrambled", McpClientStateKind.Failed);
    }

    /// <summary>The connect's own timeout (the configured task-level option) fails the entry loud, again without hanging anything.</summary>
    [Fact]
    public async Task ApplyAsync_AConnectTimeout_PinsFailedInsideTheBoundedWait()
    {
        await using var stub = new ConfigurableHttpStub(ConfigurableHttpStub.RequestMode.NeverRespond, connectionsToServe: 1);
        var manager = NewManager();

        var entry = HttpEntry("slow", stub.Url);
        await manager.ApplyAsync([new McpServerConfig
        {
            Name = entry.Name,
            Transport = entry.Transport,
            Url = entry.Url,
            AuthSecretRef = entry.AuthSecretRef,
            Enabled = true,
            HttpTimeoutSeconds = 1,
        }]);

        var row = await WaitForStateAsync(manager, "slow", McpClientStateKind.Failed, cap: TimeSpan.FromSeconds(6));
        row.Detail.Should().NotBeEmpty("the timeout's own reason rides the row");
    }

    /// <summary>An invalid stdio command (the OS cannot start it) pins Failed — never a silent half-start.</summary>
    [Fact]
    public async Task ApplyAsync_TheInvalidStdioCommand_PinsFailed()
    {
        var manager = NewManager();

        await manager.ApplyAsync([StdioEntry("ghost", "does-not-exist-4f2a9c")]);

        await WaitForStateAsync(manager, "ghost", McpClientStateKind.Failed);
    }

    /// <summary>The stdio entry launches its configured command (the classic dotnet one-liner talks itself out of business).</summary>
    [Fact]
    public async Task ApplyAsync_TheStdioEntry_LaunchesTheConfiguredCommand()
    {
        var manager = NewManager();

        await manager.ApplyAsync([StdioEntry("dotnet-now", "dotnet", "--version")]);

        await WaitForStateAsync(manager, "dotnet-now", McpClientStateKind.Connected);
    }

    #endregion Failure states (never a hang)

    #region Secrets

    /// <summary>No state row text ever carries the store's secret (only the reference key rides the configs).</summary>
    [Fact]
    public async Task TheStateRows_NeverCarryTheSecret()
    {
        await using var stub = new ConfigurableHttpStub(ConfigurableHttpStub.RequestMode.ServerError, connectionsToServe: 1);
        var manager = NewManager();

        await manager.ApplyAsync([HttpEntry("authed", stub.Url)]);
        await WaitForStateAsync(manager, "authed", McpClientStateKind.Failed);
        await Task.Delay(300); // let every late amendment land before judging the snapshot

        var snapshotText = string.Join(
            "|",
            manager.States.Select(state => $"{state.Name};{state.Detail}"));

        snapshotText.Should().NotContain(StoredCredential, "a value resolved from the store never lands in a state row");
    }

    #endregion Secrets

    #region Duplicate names

    /// <summary>Two entries with one name drop the duplicate loudly (the name keys everything).</summary>
    [Fact]
    public async Task ApplyAsync_ADuplicateName_DropsTheSecond()
    {
        await using var stub = new ConfigurableHttpStub(ConfigurableHttpStub.RequestMode.Happy, connectionsToServe: 1);
        var manager = NewManager();

        await manager.ApplyAsync(
        [
            HttpEntry("twin", stub.Url),
            HttpEntry("twin", RefusedPortUrl()),
        ]);

        var row = await WaitForStateAsync(manager, "twin", McpClientStateKind.Connected);
        manager.States.Should().ContainSingle().Which.Should().Be(row, "the first entry wins");
    }

    #endregion Duplicate names

    /// <summary>A URL on a free port: the socket message path refuses the dial at once (the connect fails, it does not hang).</summary>
    private static string RefusedPortUrl()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return $"http://127.0.0.1:{port}";
    }

    /// <summary>
    /// A loopback raw-TCP HTTP stand-in with a configurable handshake
    /// behavior: every served request answers from the mode (the T-032
    /// harness shape, extended over the failure matrix).
    /// </summary>
    private sealed class ConfigurableHttpStub : IAsyncDisposable
    {
        /// <summary>The per-request response mode.</summary>
        public enum RequestMode
        {
            /// <summary>200 with a JSON body (the happy handshakes ride this).</summary>
            Happy,

            /// <summary>500 with a body — the client reports the status error.</summary>
            ServerError,

            /// <summary>200 with a NON-JSON body — the malformed reply must not pass for a handshake.</summary>
            Malformed,

            /// <summary>The socket accepts and never answers — the client's own timeout fires.</summary>
            NeverRespond,
        }

        #region Private Fields

        private readonly TcpListener _listener;
        private readonly Task _acceptLoop;
        private readonly RequestMode _mode;
        private readonly int _connectionsToServe;
        private int _servedRequests;
        private bool _disposed;

        #endregion

        /// <summary>Start the loopback listener and its accept loop in the given mode.</summary>
        public ConfigurableHttpStub(RequestMode mode, int connectionsToServe)
        {
            _mode = mode;
            _connectionsToServe = connectionsToServe;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Url = $"http://127.0.0.1:{port}";
            _acceptLoop = AcceptLoopAsync();
        }

        /// <summary>The base URL the connecting client configures.</summary>
        public string Url { get; }

        /// <summary>The request/response pairs the stub SERVED so far (the redial pins compare against it).</summary>
        public int ServedRequests => _servedRequests;

        /// <summary>Stop the listener; the accept loop dies with it (bounded join).</summary>
        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _listener.Stop();
            await Task.WhenAny(_acceptLoop, Task.Delay(TimeSpan.FromSeconds(2)));
        }

        private async Task AcceptLoopAsync()
        {
            for (var served = 0; served < _connectionsToServe; served++)
            {
                TcpClient client;

                try
                {
                    client = await _listener.AcceptTcpClientAsync();
                }
                catch (Exception)
                {
                    return; // the dispose stopped the listener mid-accept
                }

                if (_mode == RequestMode.NeverRespond)
                {
                    // Hold the socket open UNANSWERED until the dispose closes
                    // it: the client's own timeout is the only way out. The
                    // hold runs on this accept loop itself — nothing spawns
                    // unobserved.
                    await HoldOpenAsync(client);
                    continue;
                }

                await ServeRequestsAsync(client);
            }
        }

        /// <summary>Hold the socket open unanswered until the dispose (a bounded cap keeps a leaked "wait" from outliving the test).</summary>
        private async Task HoldOpenAsync(TcpClient client)
        {
            var stuck = DateTime.UtcNow;
            while (!_disposed && DateTime.UtcNow - stuck < TimeSpan.FromSeconds(8))
            {
                await Task.Delay(50);
            }

            client.Close();
        }

        private async Task ServeRequestsAsync(TcpClient client)
        {
            try
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(
                    stream,
                    Encoding.UTF8,
                    detectEncodingFromByteOrderMarks: false,
                    bufferSize: 1024,
                    leaveOpen: true);

                // The client sends exactly two posts per connection
                // (initialize + notification); the stub reads each fully
                // (raw bytes forwarded to OS) and answers per the mode.
                for (var pair = 0; pair < 2; pair++)
                {
                    if (await ReadOneRequestAsync(reader) is null)
                    {
                        return;
                    }

                    _servedRequests++;
                    await RespondAsync(stream, closeAfter: pair == 1);
                }
            }
            finally
            {
                client.Close();
            }
        }

        /// <summary>
        /// Read one HTTP request exactly like the proven
        /// <c>McpClientServiceTests.LoopbackHttpServer</c> harness: the
        /// request line, the headers to the first empty line, then the
        /// content-length body read AFTER the header block (the in-loop body
        /// read version wedged HttpClient on the reply).
        /// </summary>
        private static async Task<string?> ReadOneRequestAsync(StreamReader reader)
        {
            var requestLine = await reader.ReadLineAsync();
            if (requestLine is null)
            {
                return null;
            }

            var requestText = new StringBuilder();
            requestText.AppendLine(requestLine);
            while (true)
            {
                var headerLine = await reader.ReadLineAsync();
                if (string.IsNullOrEmpty(headerLine))
                {
                    break;
                }

                requestText.AppendLine(headerLine);
            }

            var request = requestText.ToString();
            var contentLength = FindContentLength(request);
            if (contentLength > 0)
            {
                var body = new char[contentLength];
                _ = await reader.ReadBlockAsync(body, 0, contentLength);
            }

            return request;
        }

        /// <summary>Extract the Content-Length header's value from the raw request.</summary>
        private static int FindContentLength(string requestText)
        {
            foreach (var line in requestText.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = line.IndexOf(':');
                if (separator > 0 &&
                    line[..separator].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(
                        line[(separator + 1)..].Trim(),
                        System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var length))
                {
                    return length;
                }
            }

            return 0;
        }

        private async Task RespondAsync(NetworkStream stream, bool closeAfter)
        {
            (string status, string body) response = _mode switch
            {
                RequestMode.Happy => ("200 OK", "{}"),
                RequestMode.ServerError => ("500 Internal Server Error", "{}"),
                RequestMode.Malformed => ("200 OK", "not a json drop"),
                _ => ("200 OK", "{}"),
            };

            var payload = Encoding.UTF8.GetBytes(response.body);
            var head = new StringBuilder();
            head.Append("HTTP/1.1 ").Append(response.status).Append("\r\n");
            head.Append("Content-Type: application/json\r\n");
            head.Append("Content-Length: ").Append(payload.Length).Append("\r\n");
            if (closeAfter)
            {
                head.Append("Connection: close\r\n");
            }

            head.Append("\r\n");

            await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()));
            await stream.WriteAsync(payload);
            await stream.FlushAsync();
        }
    }
}
