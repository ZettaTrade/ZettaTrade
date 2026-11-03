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

/// <summary>
/// The MCP stdio host's stdout purity, exercised end to end inside the test
/// process: the host answers the initialize handshake and lists its tools,
/// and the ONLY bytes that ever reach stdout are the JSON-RPC envelopes —
/// a malformed request line is reported through <c>ILogger</c> and writes
/// nothing to the protocol stream (the composition rule: nothing may write
/// to stdout except MCP protocol messages; Serilog logs to files only).
/// </summary>
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

    /// <summary>The initialize response's pinned protocol revision (the 2024-11-05 MCP standard).</summary>
    private const string PinnedProtocolVersion = "2024-11-05";

    /// <summary>The whole stdio harness must finish long before this.</summary>
    private static readonly TimeSpan HarnessTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Two protocol responses must appear within this window.</summary>
    private static readonly TimeSpan ResponseWait = TimeSpan.FromSeconds(10);

    /// <summary>The poll step while the responses are in flight.</summary>
    private static readonly TimeSpan PollStep = TimeSpan.FromMilliseconds(25);

    /// <summary>The drain pause before the final stdout readout.</summary>
    private static readonly TimeSpan DrainPause = TimeSpan.FromMilliseconds(100);

    #endregion

    /// <summary>
    /// The full harness: redirect console in/out, start the host, exchange
    /// initialize + a garbage line + tools/list, stop it, and restore the
    /// console primitives even on failure.
    /// </summary>
    [Fact]
    public async Task Handshake_StdoutCarriesProtocolJsonOnly()
    {
        var captured = new StringWriter();
        var originalOut = Console.Out;
        var originalIn = Console.In;
        var input = new StringReader(
            InitializeRequest + "\n" +
            GarbageLine + "\n" +
            ToolsListRequest + "\n");
        var logger = new RecordingLogger();
        var lifetime = new CancellationTokenSource(HarnessTimeout);
        var host = new McpServerHost(logger);

        Console.SetOut(captured);
        Console.SetIn(input);
        try
        {
            await host.StartAsync(lifetime.Token);

            // The run loop processes the lines sequentially, so BOTH protocol
            // responses being written proves the garbage line was already
            // handled (and found nothing to say on stdout).
            var deadline = DateTime.UtcNow + ResponseWait;
            while (ProtocolLineCount(captured.ToString()) < 2)
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException(
                        "the MCP host never answered both protocol lines; stdout: " +
                        (captured.ToString().Length == 0 ? "<empty>" : captured.ToString()));
                }

                await Task.Delay(PollStep, lifetime.Token);
            }

            // Let the run loop reach the end of its input before reading out.
            await Task.Delay(DrainPause, lifetime.Token);

            await host.StopAsync(lifetime.Token);

            var lines = captured.ToString()
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            lines.Should().HaveCount(2, "only the two protocol envelopes reach stdout");

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

            using var tools = JsonDocument.Parse(lines[1]);
            tools.RootElement.GetProperty("jsonrpc").GetString().Should().Be("2.0");
            tools.RootElement.GetProperty("id").GetString().Should().Be("2");
            tools.RootElement.GetProperty("result").GetProperty("tools").ValueKind
                .Should().Be(JsonValueKind.Array);

            // The garbage line went to the HONEST channel: the logger. Stdout
            // saw none of it (counted above) — the leak pin lives here.
            logger.Levels.Should().Contain(LogLevel.Error);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetIn(originalIn);
            await host.DisposeAsync();
            lifetime.Dispose();
        }
    }

    /// <summary>The newline-separated protocol envelopes currently on stdout.</summary>
    private static int ProtocolLineCount(string stdout)
    {
        var lines = stdout.Split(
            new[] { "\r\n", "\n" },
            StringSplitOptions.RemoveEmptyEntries);
        return lines.Length;
    }

    /// <summary>
    /// A recording test logger: it only collects the levels it was given, so
    /// the test can assert the run loop reports its failures to the logging
    /// pipeline (never to the console).
    /// </summary>
    private sealed class RecordingLogger : ILogger<McpServerHost>
    {
        /// <summary>The levels logged so far, in arrival order.</summary>
        public List<LogLevel> Levels { get; } = new();

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
            Func<TState, Exception?, string> formatter) => Levels.Add(logLevel);

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
