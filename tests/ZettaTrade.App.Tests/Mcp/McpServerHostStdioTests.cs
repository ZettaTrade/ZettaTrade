namespace ZettaTrade.App.Tests.Mcp;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;
using ZettaTrade.AI.Mcp;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// The MCP stdio host exercised end to end inside the test process: the host
/// answers the initialize handshake, carries the client's requests through
/// and echoes every id shape back byte-exact (string, numeric, explicit
/// null), and the ONLY bytes that ever reach stdout are the JSON-RPC
/// envelopes — a malformed request line is reported through <c>ILogger</c>
/// and earns exactly the spec's -32700 parse-error envelope (which is itself
/// protocol JSON; the composition rule: nothing may write to stdout except
/// MCP protocol messages; Serilog logs to files only). Shares a collection
/// with <see cref="McpServerHostErrorTests"/>: both suites own the
/// process-wide Console primitives and must never overlap.
/// </summary>
[Collection("McpStdioConsoleSerial")]
public sealed class McpServerHostStdioTests
{
    #region Constants

    /// <summary>The initialize request of the MCP handshake (revision-pinned envelope).</summary>
    private const string InitializeRequest =
        "{\"jsonrpc\":\"2.0\",\"id\":\"1\",\"method\":\"initialize\",\"params\":{}}";

    /// <summary>A non-protocol byte sequence the client never sends — the leak probe.</summary>
    private const string GarbageLine = "not-json";

    /// <summary>The tools/list request the discovery handshake follows with.</summary>
    private const string ToolsListRequest = "{\"jsonrpc\":\"2.0\",\"id\":\"2\",\"method\":\"tools/list\"}";

    /// <summary>The initialize request with a NUMERIC id (every mainline client's shape).</summary>
    private const string NumericInitializeRequest =
        "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{}}";

    /// <summary>A tools/call with a numeric id against the registered echo tool (the numeric regression).</summary>
    private const string NumericToolsCallRequest =
        "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\""
        + EchoToolName + "\",\"arguments\":{\"msg\":\"hi\"}}}";

    /// <summary>The tools/list with an explicit NULL id (JSON-RPC still counts it a request).</summary>
    private const string NullIdToolsListRequest = "{\"jsonrpc\":\"2.0\",\"id\":null,\"method\":\"tools/list\"}";

    /// <summary>The tools/list WITHOUT an id — a JSON-RPC notification (no reply is allowed).</summary>
    private const string NotificationToolsListRequest = "{\"jsonrpc\":\"2.0\",\"method\":\"tools/list\"}";

    /// <summary>The echo-tool name the scripted client calls.</summary>
    private const string EchoToolName = "test.echo";

    /// <summary>The text the echo tool answers with.</summary>
    private const string EchoToolText = "echoed";

    /// <summary>The initialize response's pinned protocol revision (the 2024-11-05 MCP standard).</summary>
    private const string PinnedProtocolVersion = "2024-11-05";

    /// <summary>The whole stdio harness must finish long before this.</summary>
    private static readonly TimeSpan HarnessTimeout = TimeSpan.FromSeconds(15);

    /// <summary>The completions must appear within this window.</summary>
    private static readonly TimeSpan ResponseWait = TimeSpan.FromSeconds(10);

    /// <summary>The poll step while the responses are in flight.</summary>
    private static readonly TimeSpan PollStep = TimeSpan.FromMilliseconds(25);

    /// <summary>The drain pause before reading out a multi-line transcript.</summary>
    private static readonly TimeSpan DrainPause = TimeSpan.FromMilliseconds(100);

    #endregion

    #region Facts

    /// <summary>
    /// The full handshake: the host answers initialize and lists its tools,
    /// the garbage line earns exactly the spec's -32700 parse-error envelope
    /// and is reported to the logger (never stdout) — every request-shaped
    /// line the client sends gets an answer (the pre-fix loop swallowed the
    /// malformed line silently) — and both string ids echo back verbatim.
    /// </summary>
    [Fact]
    public Task Handshake_StdoutCarriesProtocolJsonOnly() =>
        ExchangeScriptedAsync(
            InitializeRequest + "\n" + GarbageLine + "\n" + ToolsListRequest + "\n",
            setup: null,
            complete: captured => StdoutLines(captured).Length >= 3,
            drainBeforeAsserts: true,
            (logger, captured) =>
            {
                var lines = StdoutLines(captured);
                lines.Should().HaveCount(3, "the initialize, the parse error and the tools/list envelopes reach stdout");

                using var initialize = JsonDocument.Parse(lines[0]);
                initialize.RootElement.GetProperty("jsonrpc").GetString().Should().Be("2.0");
                initialize.RootElement.GetProperty("id").GetString().Should().Be("1");
                var result = initialize.RootElement.GetProperty("result");
                result.GetProperty("protocolVersion").GetString().Should().Be(PinnedProtocolVersion);
                result.GetProperty("serverInfo").GetProperty("name").GetString()
                    .Should().Be(McpServerHost.ServerName);
                result.GetProperty("serverInfo").GetProperty("version").GetString()
                    .Should().Be(McpServerHost.ServerVersion);
                result.GetProperty("capabilities").TryGetProperty("tools", out _)
                    .Should().BeTrue();
                result.GetProperty("capabilities").TryGetProperty("resources", out _)
                    .Should().BeTrue("the host implements the resources surface (T-208) and advertises it honestly");
                result.GetProperty("capabilities").TryGetProperty("prompts", out _)
                    .Should().BeTrue("the host implements the prompts surface (T-208) and advertises it honestly");

                // The garbage line earns exactly the spec's -32700 parse-error
                // envelope (itself protocol JSON: the id is the JSON null — a
                // parse failure cannot know any request id).
                using var parseError = JsonDocument.Parse(lines[1]);
                parseError.RootElement.GetProperty("jsonrpc").GetString().Should().Be("2.0");
                parseError.RootElement.GetProperty("id").ValueKind.Should().Be(JsonValueKind.Null);
                var parseBody = parseError.RootElement.GetProperty("error");
                parseBody.GetProperty("code").GetInt32().Should().Be(-32700);
                parseBody.GetProperty("message").GetString().Should().NotBeNullOrEmpty();

                using var tools = JsonDocument.Parse(lines[2]);
                tools.RootElement.GetProperty("jsonrpc").GetString().Should().Be("2.0");
                tools.RootElement.GetProperty("id").GetString().Should().Be("2");
                tools.RootElement.GetProperty("result").GetProperty("tools").ValueKind
                    .Should().Be(JsonValueKind.Array);

                // The garbage line's failure went to the HONEST channel: the
                // logger (the -32700 reply rides stdout as protocol JSON). The
                // leak pin lives in the count above.
                logger.Levels.Should().Contain(LogLevel.Error);
            });

    /// <summary>
    /// The exact capability a real client hangs on: NUMERIC request ids must
    /// travel through and echo back — the scripted client drives initialize
    /// and a tools/call with integer ids and receives both replies, each
    /// echoing its own id (the pre-fix host dropped both — GetString()
    /// turned a numeric id into null — and the client hung).
    /// </summary>
    [Fact]
    public Task NumericIdRoundTrip_AnswersAndEchoesEveryId() =>
        ExchangeScriptedAsync(
            NumericInitializeRequest + "\n" + NumericToolsCallRequest + "\n",
            RegisterEchoTool,
            complete: captured => StdoutLines(captured).Length >= 2,
            drainBeforeAsserts: true,
            (_, captured) =>
            {
                var lines = StdoutLines(captured);
                lines.Should().HaveCount(2, "both numeric-id requests are answered");

                using var initialize = JsonDocument.Parse(lines[0]);
                initialize.RootElement.GetProperty("jsonrpc").GetString().Should().Be("2.0");
                initialize.RootElement.GetProperty("id").ValueKind.Should().Be(JsonValueKind.Number);
                initialize.RootElement.GetProperty("id").GetInt32().Should().Be(1);
                var result = initialize.RootElement.GetProperty("result");
                result.GetProperty("protocolVersion").GetString().Should().Be(PinnedProtocolVersion);
                result.GetProperty("serverInfo").GetProperty("name").GetString()
                    .Should().Be(McpServerHost.ServerName);
                result.GetProperty("serverInfo").GetProperty("version").GetString()
                    .Should().Be(McpServerHost.ServerVersion);

                using var call = JsonDocument.Parse(lines[1]);
                call.RootElement.GetProperty("jsonrpc").GetString().Should().Be("2.0");
                call.RootElement.GetProperty("id").ValueKind.Should().Be(JsonValueKind.Number);
                call.RootElement.GetProperty("id").GetInt32().Should().Be(2);
                var content = call.RootElement.GetProperty("result").GetProperty("content");
                content.ValueKind.Should().Be(JsonValueKind.Array);
                content.GetArrayLength().Should().Be(1);
                var entry = content[0];
                entry.GetProperty("Type").GetString().Should().Be("text");
                entry.GetProperty("TextValue").GetString().Should().Be(EchoToolText);
            });

    /// <summary>
    /// An explicit NULL id still counts as a request: the reply must echo
    /// that null (the pre-fix host turned the whole id into null and DROPPED
    /// the response as if it were a notification).
    /// </summary>
    [Fact]
    public Task TheNullId_AnswersWithTheEchoedNull() =>
        ExchangeScriptedAsync(
            NullIdToolsListRequest + "\n",
            setup: null,
            complete: captured => StdoutLines(captured).Length >= 1,
            drainBeforeAsserts: false,
            (_, captured) =>
            {
                var lines = StdoutLines(captured);
                lines.Should().HaveCount(1, "the null-id request still needs exactly one reply");
                using var reply = JsonDocument.Parse(lines[0]);
                reply.RootElement.GetProperty("jsonrpc").GetString().Should().Be("2.0");
                reply.RootElement.GetProperty("id").ValueKind.Should().Be(JsonValueKind.Null);
                reply.RootElement.GetProperty("result").GetProperty("tools").ValueKind
                    .Should().Be(JsonValueKind.Array);
            });

    /// <summary>
    /// A request line without an id is a JSON-RPC notification: the server
    /// processes it (the trailing string-id request proves the loop read both
    /// lines in order) but stdout must stay silent for it — no reply, no leak.
    /// </summary>
    [Fact]
    public Task ANotification_StaysSilentOnStdout() =>
        ExchangeScriptedAsync(
            NotificationToolsListRequest + "\n" + ToolsListRequest + "\n",
            setup: null,
            complete: captured => StdoutLines(captured).Length >= 1,
            drainBeforeAsserts: false,
            (_, captured) =>
            {
                var lines = StdoutLines(captured);
                lines.Should().HaveCount(1, "the notification is processed but must never be answered");
                using var reply = JsonDocument.Parse(lines[0]);
                reply.RootElement.GetProperty("id").GetString().Should().Be("2");
            });

    #endregion

    #region Factories

    /// <summary>
    /// The whole scripted exchange: redirect the console primitives, run the
    /// setup, start the host, drive the input script, wait until the
    /// completion predicate holds (a stalled host fails with its stdout
    /// attached), stop the host, run the asserts, and restore the console
    /// primitives even on failure.
    /// </summary>
    private static async Task ExchangeScriptedAsync(
        string scriptInput,
        Action<McpServerHost>? setup,
        Func<StringWriter, bool> complete,
        bool drainBeforeAsserts,
        Action<RecordingLogger, StringWriter> asserts)
    {
        var originalOut = Console.Out;
        var originalIn = Console.In;
        var captured = new StringWriter();
        var logger = new RecordingLogger();
        var lifetime = new CancellationTokenSource(HarnessTimeout);
        var host = new McpServerHost(logger);
        setup?.Invoke(host);

        Console.SetOut(captured);
        Console.SetIn(new StringReader(scriptInput));
        try
        {
            await host.StartAsync(lifetime.Token);

            // The run loop processes the lines sequentially, so the completion
            // predicate holding proves earlier script lines were already
            // handled too.
            var deadline = DateTime.UtcNow + ResponseWait;
            while (!complete(captured))
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException(
                        "the MCP host never reached the scripted completion; stdout: " +
                        string.Join(" | ", StdoutLines(captured)) +
                        "; logs: " + string.Join(" | ", logger.Messages));
                }

                await Task.Delay(PollStep, lifetime.Token);
            }

            // Let the run loop reach the end of its input before reading out.
            if (drainBeforeAsserts)
            {
                await Task.Delay(DrainPause, lifetime.Token);
            }

            await host.StopAsync(lifetime.Token);
            asserts(logger, captured);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetIn(originalIn);
            await host.DisposeAsync();
            lifetime.Dispose();
        }
    }

    /// <summary>Registers the echo tool the scripted tools/call drives.</summary>
    private static void RegisterEchoTool(McpServerHost host) =>
        host.RegisterTool(new McpToolDefinition
        {
            Name = EchoToolName,
            Description = "Scripted-client echo tool",
            InputSchema = JsonSerializer.SerializeToElement(new { type = "object" }),
            Handler = async (_, _) => new McpToolResult(
                IsError: false,
                Content: [McpContent.CreateText(EchoToolText)]),
        });

    /// <summary>Splits the captured stdout into its complete written lines.</summary>
    private static string[] StdoutLines(StringWriter captured) =>
        captured.ToString().Split([Environment.NewLine, "\n"], StringSplitOptions.RemoveEmptyEntries);

    #endregion

    /// <summary>
    /// A recording test logger: it collects both the levels and the rendered
    /// messages it was given, so the test can assert the run loop reports
    /// its failures to the logging pipeline (never to the console).
    /// </summary>
    private sealed class RecordingLogger : ILogger<McpServerHost>
    {
        /// <summary>The levels logged so far, in arrival order.</summary>
        public List<LogLevel> Levels { get; } = new();

        /// <summary>The rendered messages logged so far, in arrival order.</summary>
        public List<string> Messages { get; } = new();

        /// <inheritdoc/>
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
            NoopScope.Instance;

        /// <inheritdoc/>
        public bool IsEnabled(LogLevel logLevel) => true;

        /// <inheritdoc/>
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Levels.Add(logLevel);
            Messages.Add(formatter(state, exception));
        }

        /// <summary>The scope handed to callers who never dispose it twice.</summary>
        private sealed class NoopScope : IDisposable
        {
            /// <summary>The shared no-op instance.</summary>
            public static readonly NoopScope Instance = new();

            /// <inheritdoc/>
            public void Dispose()
            {
            }
        }
    }
}
