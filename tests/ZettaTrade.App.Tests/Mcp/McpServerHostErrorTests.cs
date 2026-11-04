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
/// The MCP host's failure surface, scripted through the same stdio pipes the
/// real protocol uses: malformed and unfamiliar requests no longer vanish —
/// each one earns the JSON-RPC error reply its shape demands (parse → -32700,
/// missing method → -32600, unknown method → -32601, missing params → -32602;
/// BUG-056), notifications stay silent, and a registration while a tool call
/// sits parked in its handler completes immediately (the dispatch lock covers
/// only the table read; BUG-020). Shares a collection with
/// <see cref="McpServerHostStdioTests"/>: both suites own the process-wide
/// Console primitives and must never overlap.
/// </summary>
[Collection("McpStdioConsoleSerial")]
public sealed class McpServerHostErrorTests
{
    #region Constants

    /// <summary>The whole scripted harness must finish long before this.</summary>
    private static readonly TimeSpan HarnessTimeout = TimeSpan.FromSeconds(15);

    /// <summary>The scripted replies must appear within this window.</summary>
    private static readonly TimeSpan ResponseWait = TimeSpan.FromSeconds(10);

    /// <summary>The poll step while the replies are in flight.</summary>
    private static readonly TimeSpan PollStep = TimeSpan.FromMilliseconds(25);

    /// <summary>The pause before reading out a multi-request transcript.</summary>
    private static readonly TimeSpan DrainPause = TimeSpan.FromMilliseconds(150);

    /// <summary>The bounded wait for the parked tool call to announce itself.</summary>
    private static readonly TimeSpan InFlightWait = TimeSpan.FromSeconds(5);

    /// <summary>The bounded wait proving a registration never waits on a parked call.</summary>
    private static readonly TimeSpan RegisterWait = TimeSpan.FromSeconds(5);

    /// <summary>A recognizable request id the scripted error replies must echo.</summary>
    private const string ParseRequestId = "z1";

    /// <summary>The tools/list request closing the parse-error script (the loop survives bad JSON).</summary>
    private const string ToolsListRequest =
        "{\"jsonrpc\":\"2.0\",\"id\":\"" + ParseRequestId + "\",\"method\":\"tools/list\"}";

    /// <summary>The scripted tools/call the registration fact parks in its handler.</summary>
    private const string BlockerCallRequest =
        "{\"jsonrpc\":\"2.0\",\"id\":\"b1\",\"method\":\"tools/call\",\"params\":{\"name\":\"tests.blocker\"}}";

    /// <summary>The parked handler's tool name.</summary>
    private const string BlockerToolName = "tests.blocker";

    /// <summary>The parked handler's eventual answer.</summary>
    private const string BlockerToolText = "released";

    /// <summary>The tool the in-flight registration adds while the call is parked.</summary>
    private const string LateToolName = "tests.late";

    /// <summary>The pinned pass-through reply id the parked call ends with.</summary>
    private const string BlockerCallRequestId = "b1";

    #endregion

    #region Error matrix

    /// <summary>
    /// Bad JSON earns the -32700 parse-error reply (id null, per the JSON-RPC
    /// spec) and the loop keeps serving — the trailing tools/list request
    /// proves the very next line still gets its reply.
    /// </summary>
    [Fact]
    public Task ABadJsonLine_AnswersTheParseErrorAndTheLoopSurvives() =>
        ExchangeAsync(
            "not-json\n" + ToolsListRequest + "\n",
            setup: null,
            lines => lines.Length >= 2,
            (_, lines) =>
            {
                lines.Should().HaveCount(2, "the parse error and the tools/list reply both land");

                using var parseError = JsonDocument.Parse(lines[0]);
                parseError.RootElement.GetProperty("jsonrpc").GetString().Should().Be("2.0");
                parseError.RootElement.GetProperty("id").ValueKind.Should().Be(JsonValueKind.Null);
                var parseBody = parseError.RootElement.GetProperty("error");
                parseBody.GetProperty("code").GetInt32().Should().Be(-32700);
                parseBody.GetProperty("message").GetString().Should().NotBeNullOrEmpty();

                using var list = JsonDocument.Parse(lines[1]);
                list.RootElement.GetProperty("id").GetString().Should().Be(ParseRequestId);
                list.RootElement.GetProperty("result").GetProperty("tools").ValueKind
                    .Should().Be(JsonValueKind.Array);
            });

    /// <summary>A structurally valid JSON without a method is an invalid REQUEST (-32600), answered with the id echoed.</summary>
    [Fact]
    public Task ARequestWithoutAMethod_AnswersInvalidRequest() =>
        ExchangeAsync(
            "{\"jsonrpc\":\"2.0\",\"id\":\"no-method\"}\n",
            setup: null,
            lines => lines.Length >= 1,
            (_, lines) =>
            {
                lines.Should().HaveCount(1, "the malformed request carries an id, so it earns a reply");
                using var envelope = JsonDocument.Parse(lines[0]);
                envelope.RootElement.GetProperty("id").GetString().Should().Be("no-method");
                envelope.RootElement.GetProperty("error").GetProperty("code").GetInt32().Should().Be(-32600);
            });

    /// <summary>
    /// A method-less NOTIFICATION is (correctly) not answered: the silent
    /// notification is followed by a request whose reply proves the loop
    /// processed both lines in order.
    /// </summary>
    [Fact]
    public Task ANamelessNotification_StaysSilentWhileTheLoopSurvives() =>
        ExchangeAsync(
            "{\"jsonrpc\":\"2.0\"}\n" + ToolsListRequest + "\n",
            setup: null,
            lines => lines.Length >= 1,
            (_, lines) =>
            {
                lines.Should().HaveCount(1, "the notification is processed silently, only the request is answered");
                using var envelope = JsonDocument.Parse(lines[0]);
                envelope.RootElement.GetProperty("id").GetString().Should().Be(ParseRequestId);
                envelope.RootElement.GetProperty("result").GetProperty("tools").ValueKind
                    .Should().Be(JsonValueKind.Array);
            });

    /// <summary>An unknown method with an id earns -32601 with that id echoed.</summary>
    [Fact]
    public Task AnUnknownMethod_AnswersMethodNotFound() =>
        ExchangeAsync(
            "{\"jsonrpc\":\"2.0\",\"id\":\"un-1\",\"method\":\"wallet/report\"}\n",
            setup: null,
            lines => lines.Length >= 1,
            (_, lines) =>
            {
                lines.Should().HaveCount(1);
                using var envelope = JsonDocument.Parse(lines[0]);
                envelope.RootElement.GetProperty("id").GetString().Should().Be("un-1");
                var body = envelope.RootElement.GetProperty("error");
                body.GetProperty("code").GetInt32().Should().Be(-32601);
                body.GetProperty("message").GetString().Should().Contain("wallet/report");
            });

    /// <summary>A tools/call without params.name and a resources/read without uri both earn -32602.</summary>
    [Fact]
    public Task ACallMissingItsParams_AnswersInvalidParams() =>
        ExchangeAsync(
            "{\"jsonrpc\":\"2.0\",\"id\":\"tc\",\"method\":\"tools/call\"}\n"
            + "{\"jsonrpc\":\"2.0\",\"id\":\"rr\",\"method\":\"resources/read\"}\n",
            setup: null,
            lines => lines.Length >= 2,
            (_, lines) =>
            {
                lines.Should().HaveCount(2);

                using var toolCall = JsonDocument.Parse(lines[0]);
                toolCall.RootElement.GetProperty("id").GetString().Should().Be("tc");
                toolCall.RootElement.GetProperty("error").GetProperty("code").GetInt32().Should().Be(-32602);

                using var resourceRead = JsonDocument.Parse(lines[1]);
                resourceRead.RootElement.GetProperty("id").GetString().Should().Be("rr");
                resourceRead.RootElement.GetProperty("error").GetProperty("code").GetInt32().Should().Be(-32602);
            });

    /// <summary>A tools/call naming an unregistered tool keeps its -32601 error (pinned pre-existing behavior).</summary>
    [Fact]
    public Task ACallForAnUnknownTool_AnswersMethodNotFound() =>
        ExchangeAsync(
            "{\"jsonrpc\":\"2.0\",\"id\":\"tc-ghost\",\"method\":\"tools/call\",\"params\":{\"name\":\"ghost.tool\"}}\n",
            setup: null,
            lines => lines.Length >= 1,
            (_, lines) =>
            {
                lines.Should().HaveCount(1);
                using var envelope = JsonDocument.Parse(lines[0]);
                envelope.RootElement.GetProperty("id").GetString().Should().Be("tc-ghost");
                envelope.RootElement.GetProperty("error").GetProperty("code").GetInt32().Should().Be(-32601);
            });

    #endregion

    #region Dispatch lock

    /// <summary>
    /// The BUG-020 differential: a tool call parked inside its handler (a
    /// human-confirmation await) must NOT block a concurrent
    /// <see cref="IMcpServerHost.RegisterTool"/> — the dispatch lock covers
    /// only the synchronous table read. The pre-fix host held the lock across
    /// the whole handler await, so the registration would stall until the
    /// parked call was released.
    /// </summary>
    [Fact]
    public async Task ARegistrationWhileAToolCallIsInFlight_CompletesImmediately()
    {
        var originalOut = Console.Out;
        var originalIn = Console.In;
        var captured = new StringWriter();
        var logger = new RecordingLogger();
        var host = new McpServerHost(logger);
        var lifetime = new CancellationTokenSource(HarnessTimeout);
        var callInFlight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCall = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        host.RegisterTool(new McpToolDefinition
        {
            Name = BlockerToolName,
            Description = "A tool whose handler parks on a gate (the stand-in for a confirmation dialog)",
            InputSchema = JsonSerializer.SerializeToElement(new { type = "object" }),
            Handler = async (_, _) =>
            {
                callInFlight.TrySetResult();
                await releaseCall.Task;
                return new McpToolResult(IsError: false, Content: [McpContent.CreateText(BlockerToolText)]);
            },
        });

        Console.SetOut(captured);
        Console.SetIn(new StringReader(BlockerCallRequest + "\n"));
        try
        {
            await host.StartAsync(lifetime.Token);

            // The handler is parked: it announced itself and now sits on the
            // gate — the parked call is "in flight" exactly as the finding
            // describes. WaitAsync throws on the bounded timeout, so the
            // arrival alone is the assertion.
            await callInFlight.Task.WaitAsync(InFlightWait, lifetime.Token);

            // The registration runs on a worker thread and must complete on
            // its own, well before the parked call ends (the lock is already
            // released while the handler is parked).
            var registerTask = Task.Run(() => host.RegisterTool(new McpToolDefinition
            {
                Name = LateToolName,
                Description = "Registered while another tool call is parked",
                InputSchema = JsonSerializer.SerializeToElement(new { type = "object" }),
                Handler = async (_, _) => new McpToolResult(
                    IsError: false,
                    Content: [McpContent.CreateText("late")]),
            }));

            (await Task.WhenAny(registerTask, Task.Delay(RegisterWait, lifetime.Token)))
                .Should().Be(registerTask, "the registration must never wait on the parked tool call");
            registerTask.IsCompletedSuccessfully.Should().BeTrue();

            // Release the parked call: its reply lands with no error and the
            // pass-through content.
            releaseCall.TrySetResult();
            await WaitUntilAsync(() => StdoutLines(captured).Length >= 1, lifetime.Token);

            var lines = StdoutLines(captured);
            lines.Should().HaveCount(1, "only the parked call's own reply reaches stdout");
            using var envelope = JsonDocument.Parse(lines[0]);
            envelope.RootElement.GetProperty("id").GetString().Should().Be(BlockerCallRequestId);
            envelope.RootElement.TryGetProperty("error", out _).Should().BeFalse("the released call succeeds");
            var content = envelope.RootElement.GetProperty("result").GetProperty("content");
            content.GetArrayLength().Should().Be(1);
            content[0].GetProperty("TextValue").GetString().Should().Be(BlockerToolText);

            await host.StopAsync(lifetime.Token);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetIn(originalIn);
            releaseCall.TrySetResult();
            await host.DisposeAsync();
            lifetime.Dispose();
        }
    }

    #endregion

    #region Factories

    /// <summary>
    /// The scripted exchange: redirect the console primitives, run the setup,
    /// start the host, wait until the completion predicate holds (a stalled
    /// host fails with its stdout and logs attached), stop the host, run the
    /// asserts, and restore the console primitives even on failure. Draining
    /// before the asserts lets multi-request scripts finish both their
    /// replies.
    /// </summary>
    private static async Task ExchangeAsync(
        string scriptInput,
        Action<McpServerHost>? setup,
        Func<string[], bool> complete,
        Action<RecordingLogger, string[]> asserts)
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
            await WaitUntilAsync(() => complete(StdoutLines(captured)), lifetime.Token);

            // Let the run loop reach the end of its input before asserting.
            await Task.Delay(DrainPause, lifetime.Token);

            await host.StopAsync(lifetime.Token);
            asserts(logger, StdoutLines(captured));
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetIn(originalIn);
            await host.DisposeAsync();
            lifetime.Dispose();
        }
    }

    /// <summary>Poll the condition until it holds or the deadline bursts (with the transcript attached).</summary>
    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken lifetime)
    {
        var deadline = DateTime.UtcNow + ResponseWait;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("the scripted exchanges never settled; taking too long");
            }

            await Task.Delay(PollStep, lifetime);
        }
    }

    /// <summary>Splits the captured stdout into its complete written lines.</summary>
    private static string[] StdoutLines(StringWriter captured) =>
        captured.ToString().Split([Environment.NewLine, "\n"], StringSplitOptions.RemoveEmptyEntries);

    #endregion

    /// <summary>
    /// A recording test logger collecting levels AND messages, so a failed
    /// fact can attach exactly what the host reported (the failing paths go
    /// to the pipeline, never to stdout).
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
