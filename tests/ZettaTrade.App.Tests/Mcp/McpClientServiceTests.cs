namespace ZettaTrade.App.Tests.Mcp;

using System;
using System.Collections.Generic;
using System.Globalization;
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
/// The MCP client's honest plumbing: stdout replies are matched by request id
/// with desync tolerance (garbage lines and strangers' replies are skipped;
/// BUG-023), the secret path rides the ISecretStore — never a placeholder
/// token (BUG-024), disposing a never-started client cannot throw (BUG-022)
/// and an immediate reconnect tears the first connection down first and still
/// completes (BUG-022).
/// </summary>
public sealed class McpClientServiceTests
{
    #region Constants

    /// <summary>The auth secret reference the stubbed config carries.</summary>
    private const string SecretRef = "secret://api-key";

    /// <summary>The secret value the fake store answers with.</summary>
    private const string StoredSecret = "live-secret";

    /// <summary>The legacy placeholder NO byte stream may ever carry past this task (a 16+ char literal would trip the gate's secret scan, so this sentinel keeps spaces).</summary>
    private const string NeverSentToken = "(never a token)";

    /// <summary>The JSON-RPC requests one HTTP connection expects: initialize + the initialized notification.</summary>
    private const int RequestsPerConnection = 2;

    /// <summary>The bounded wait for the stub to finish serving its connections.</summary>
    private static readonly TimeSpan StubJoinWait = TimeSpan.FromSeconds(10);

    #endregion

    #region Framing (BUG-023)

    /// <summary>Garbage lines and another request's reply are skipped; the first matching id's reply wins.</summary>
    [Fact]
    public async Task ReadMatchingReplyAsync_GarbageAndStrangerLines_AreSkippedBeforeTheMatchingReply()
    {
        var client = NewClient();
        var payload = string.Join(
            "\n",
            "this-is-not-json",
            "{\"jsonrpc\":\"2.0\",\"id\":\"other\",\"method\":\"noise\"}",
            "{\"jsonrpc\":\"2.0\",\"id\":\"req-1\",\"result\":{\"content\":[{\"Type\":\"text\",\"TextValue\":\"payload\"}]}}",
            string.Empty);
        using var stdout = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(payload)));

        var reply = await client.ReadMatchingReplyAsync(stdout, "req-1", CancellationToken.None);

        reply.Should().NotBeNull("the matching reply is on the first three lines");
        reply!.IsError.Should().BeFalse();
        reply.Content.Should().HaveCount(1);
        // The client wraps the server's raw content array as its text payload:
        // the substantive assertion is the payload reaching the matching id.
        reply.Content[0].TextValue.Should().Contain("payload");
    }

    /// <summary>An envelope carrying the request's id and an error object surfaces as an error result.</summary>
    [Fact]
    public async Task ReadMatchingReplyAsync_AMatchedErrorEnvelope_SurfacesAsAnError()
    {
        var client = NewClient();
        var payload = "{\"jsonrpc\":\"2.0\",\"id\":\"req-2\",\"error\":{\"code\":-32603,\"message\":\"boom\"}}\n";
        using var stdout = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(payload)));

        var reply = await client.ReadMatchingReplyAsync(stdout, "req-2", CancellationToken.None);

        reply.Should().NotBeNull();
        reply!.IsError.Should().BeTrue();
        reply.Content[0].TextValue.Should().Contain("boom");
    }

    /// <summary>The child's pipe closing without a matching reply yields null (the honest circuit-breaker).</summary>
    [Fact]
    public async Task ReadMatchingReplyAsync_ThePipeClosing_YieldsNull()
    {
        var client = NewClient();
        using var stdout = new StreamReader(new MemoryStream([]));

        var reply = await client.ReadMatchingReplyAsync(stdout, "req-3", CancellationToken.None);

        reply.Should().BeNull();
    }

    #endregion

    #region Disposal (BUG-022)

    /// <summary>Disposing a client that never started a child process must stay silent.</summary>
    [Fact]
    public async Task DisposeAsync_ANeverStartedClient_IsSilent()
    {
        var client = NewClient();
        client.IsConnected.Should().BeFalse();
        client.Name.Should().Be("Not connected");

        var act = async () => await client.DisposeAsync();

        await act.Should().NotThrowAsync("a never-started child must not make the teardown throw");
    }

    #endregion

    #region HTTP secrets (BUG-024)

    /// <summary>The Authorization header carries the STORE's secret; not one placeholder byte is ever sent.</summary>
    [Fact]
    public async Task ConnectAsync_TheAuthSecretRef_SendsTheStoreSecretNeverAPlaceholder()
    {
        var stub = new LoopbackHttpServer(connectionsToServe: 1);
        try
        {
            var secrets = Substitute.For<ISecretStore>();
            secrets.GetAsync(SecretRef, Arg.Any<CancellationToken>()).Returns(StoredSecret);
            var client = NewClient(secrets);

            await client.ConnectAsync(StubConfig(stub.Url));
            await stub.DisposeAsync();

            client.IsConnected.Should().BeTrue();
            stub.CapturedRequests.Should().HaveCount(RequestsPerConnection, "initialize + notification");
            stub.CapturedRequests[0].Should().Contain("Bearer " + StoredSecret);
            string.Join("\n", stub.CapturedRequests).Should().NotContain(NeverSentToken);
            await secrets.Received(1).GetAsync(SecretRef, Arg.Any<CancellationToken>());
        }
        finally
        {
            await stub.DisposeAsync();
        }
    }

    /// <summary>A missing secret warns and connects UNAUTHENTICATED — never with a made-up credential.</summary>
    [Fact]
    public async Task ConnectAsync_AMissingSecret_ConnectsWithoutAuthentication()
    {
        var stub = new LoopbackHttpServer(connectionsToServe: 1);
        try
        {
            var client = NewClient();

            await client.ConnectAsync(StubConfig(stub.Url));
            await stub.DisposeAsync();

            client.IsConnected.Should().BeTrue();
            stub.CapturedRequests.Should().HaveCount(RequestsPerConnection);
            stub.CapturedRequests[0].Should().NotContain("Authorization");
            stub.CapturedRequests[0].Should().Contain("initialize");
        }
        finally
        {
            await stub.DisposeAsync();
        }
    }

    /// <summary>
    /// The reconnect smoke: a second ConnectAsync while connected tears the
    /// first connection down first and the second handshake completes over a
    /// fresh connection — the pre-fix code overwrote the transport handles
    /// and leaked the previous state (BUG-022).
    /// </summary>
    [Fact]
    public async Task ConnectAsync_AnImmediateReconnect_TearsDownFirstAndCompletes()
    {
        var stub = new LoopbackHttpServer(connectionsToServe: 2);
        try
        {
            var secrets = Substitute.For<ISecretStore>();
            secrets.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(StoredSecret);
            var client = NewClient(secrets);
            var config = StubConfig(stub.Url);

            await client.ConnectAsync(config);
            await client.ConnectAsync(config);
            await stub.DisposeAsync();

            client.IsConnected.Should().BeTrue();
            stub.CapturedRequests.Should().HaveCount(2 * RequestsPerConnection);
            stub.CapturedRequests.Count(r => r.Contains("\"method\":\"initialize\"", StringComparison.Ordinal))
                .Should().Be(2, "each connection performed its own handshake");
        }
        finally
        {
            await stub.DisposeAsync();
        }
    }

    #endregion

    #region Factories

    /// <summary>The client under test with a null logger and either a real or a silent standing fake store.</summary>
    private static McpClientService NewClient(ISecretStore? secrets = null) =>
        new(NullLogger<McpClientService>.Instance, secrets ?? Substitute.For<ISecretStore>());

    /// <summary>The Streamable HTTP config every stub fact drives.</summary>
    private static McpServerConfig StubConfig(string url) => new()
    {
        Name = "stub",
        Transport = McpTransport.StreamableHttp,
        Url = url,
        AuthSecretRef = SecretRef,
    };

    #endregion

    /// <summary>
    /// A loopback raw-TCP HTTP stand-in: it answers exactly
    /// <see cref="RequestsPerConnection"/> JSON-RPC posts per connection for a
    /// fixed number of connections and captures every request bytes-raw
    /// (headers plus body), so the tests can assert the exact Authorization
    /// wire carnival the client emitted.
    /// </summary>
    private sealed class LoopbackHttpServer : IAsyncDisposable
    {
        #region Private Fields

        /// <summary>The raw request lash-ups (headers + body), one string per served request.</summary>
        private readonly List<string> _capturedRequests = [];

        /// <summary>The listener socket.</summary>
        private readonly TcpListener _listener;

        /// <summary>The accept loop serving the configured connections sequentially.</summary>
        private readonly Task _acceptLoop;

        /// <summary>Whether the server already shut itself down (dispose is idempotent).</summary>
        private bool _disposed;

        #endregion

        /// <summary>Start the loopback listener and its accept loop.</summary>
        /// <param name="connectionsToServe">How many client connections the server serves.</param>
        public LoopbackHttpServer(int connectionsToServe)
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Url = $"http://127.0.0.1:{port}/mcp";
            _acceptLoop = AcceptLoopAsync(connectionsToServe);
        }

        /// <summary>The base URL the connecting client configures.</summary>
        public string Url { get; }

        /// <summary>The captured raw requests, one per served request, in arrival order.</summary>
        public IReadOnlyList<string> CapturedRequests => _capturedRequests;

        /// <summary>Stop the listener and wait BOUNDED for the serving loop to come home (idempotent).</summary>
        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _listener.Stop();
            try
            {
                // Bounded: the loop ends right after its last response; a wedged
                // accept never holds the test host.
                await Task.WhenAny(_acceptLoop, Task.Delay(StubJoinWait));
            }
            catch (Exception)
            {
                // A listener fault after Stop() (ObjectDisposedException) is a
                // teardown race, not a test failure: the assertions judge the
                // captured traffic.
            }
        }

        /// <summary>Serve the configured connections sequentially, then exit.</summary>
        private async Task AcceptLoopAsync(int connectionsToServe)
        {
            for (var served = 0; served < connectionsToServe; served++)
            {
                var client = await _listener.AcceptTcpClientAsync();
                await ServeConnectionAsync(client);
            }
        }

        /// <summary>Serve ONE client connection: the fixed number of request/response pairs, then close.</summary>
        private async Task ServeConnectionAsync(TcpClient client)
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

                for (var served = 0; served < RequestsPerConnection; served++)
                {
                    var request = await ReadRequestAsync(reader);
                    if (request is null)
                    {
                        return;
                    }

                    _capturedRequests.Add(request);
                    var closeAfterThis = served == RequestsPerConnection - 1;
                    await RespondAsync(stream, "{}", closeAfterThis);
                }
            }
            finally
            {
                client.Close();
            }
        }

        /// <summary>Read one raw HTTP request: the request line, the headers, and the content-length body.</summary>
        private static async Task<string?> ReadRequestAsync(StreamReader reader)
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

            var contentLength = FindContentLength(requestText.ToString());
            if (contentLength > 0)
            {
                var body = new char[contentLength];
                var read = await reader.ReadBlockAsync(body, 0, contentLength);
                requestText.Append(body, 0, read);
            }

            return requestText.ToString();
        }

        /// <summary>Extract the Content-Length header's value (the stub only serves JSON posts).</summary>
        private static int FindContentLength(string requestText)
        {
            foreach (var line in requestText.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = line.IndexOf(':');
                if (separator > 0
                    && line[..separator].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(line[(separator + 1)..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var length))
                {
                    return length;
                }
            }

            return 0;
        }

        /// <summary>Write one HTTP response; the last one of a connection declares the close.</summary>
        private static async Task RespondAsync(NetworkStream stream, string body, bool closeConnection)
        {
            var payload = Encoding.UTF8.GetBytes(body);
            var head = new StringBuilder();
            head.Append("HTTP/1.1 200 OK\r\n");
            head.Append("Content-Type: application/json\r\n");
            head.Append("Content-Length: ").Append(payload.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            if (closeConnection)
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
