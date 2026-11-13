namespace ZettaTrade.App.Tests.Mcp;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ZettaTrade.AI.Mcp;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// The prompts/get surface exercised end to end inside the test process
/// (T-208): a registered prompt renders into ONE user message (role user,
/// content type text), an unregistered prompt earns the -32601 envelope, a
/// missing name, a non-object arguments member, a non-string argument value
/// and a missing required argument all earn the -32602 envelope with the
/// honest reason, and a faulted prompt handler earns the -32603 envelope
/// instead of hanging the client. Shares a collection with the other stdio
/// suites: both own the process-wide Console primitives and must never
/// overlap.
/// </summary>
[Collection("McpStdioConsoleSerial")]
public sealed class McpServerHostPromptTests
{
    #region Constants

    /// <summary>The initialize request of the MCP handshake.</summary>
    private const string InitializeRequest =
        "{\"jsonrpc\":\"2.0\",\"id\":\"1\",\"method\":\"initialize\",\"params\":{}}";

    /// <summary>The prompts/list request the handshake follows with.</summary>
    private const string PromptsListRequest = "{\"jsonrpc\":\"2.0\",\"id\":\"2\",\"method\":\"prompts/list\"}";

    /// <summary>The prompts/get for the registered prompt (both arguments supplied).</summary>
    private const string PromptsGetRequest =
        "{\"jsonrpc\":\"2.0\",\"id\":\"3\",\"method\":\"prompts/get\",\"params\":{\"name\":\"test.brief\",\"arguments\":{\"asset\":\"BTC\",\"depth\":\"deep\"}}}";

    /// <summary>The prompts/get omitting the OPTIONAL argument: its absence is legal.</summary>
    private const string PromptsGetOptionalOnlyRequest =
        "{\"jsonrpc\":\"2.0\",\"id\":\"4\",\"method\":\"prompts/get\",\"params\":{\"name\":\"test.brief\",\"arguments\":{\"asset\":\"BTC\"}}}";

    /// <summary>The prompts/get omitting a REQUIRED argument.</summary>
    private const string PromptsGetMissingRequiredRequest =
        "{\"jsonrpc\":\"2.0\",\"id\":\"9\",\"method\":\"prompts/get\",\"params\":{\"name\":\"test.brief\",\"arguments\":{\"depth\":\"deep\"}}}";

    /// <summary>The prompts/get whose argument value is a number, never a string.</summary>
    private const string PromptsGetNumericArgumentRequest =
        "{\"jsonrpc\":\"2.0\",\"id\":\"5\",\"method\":\"prompts/get\",\"params\":{\"name\":\"test.brief\",\"arguments\":{\"asset\":42}}}";

    /// <summary>The prompts/get whose arguments member is an array, never an object.</summary>
    private const string PromptsGetArrayArgumentsRequest =
        "{\"jsonrpc\":\"2.0\",\"id\":\"6\",\"method\":\"prompts/get\",\"params\":{\"name\":\"test.brief\",\"arguments\":[1,2]}}";

    /// <summary>The prompts/get with no name at all.</summary>
    private const string PromptsGetMissingNameRequest =
        "{\"jsonrpc\":\"2.0\",\"id\":\"7\",\"method\":\"prompts/get\",\"params\":{}}";

    /// <summary>The prompts/get naming a prompt no registrar registered.</summary>
    private const string PromptsGetUnknownPromptRequest =
        "{\"jsonrpc\":\"2.0\",\"id\":\"8\",\"method\":\"prompts/get\",\"params\":{\"name\":\"test.missing\"}}";

    /// <summary>The registered prompt's rendered text (the handler's output).</summary>
    private const string RenderedPromptText = "brief: BTC deep";

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
    /// The full prompts walk: prompts/list carries the registered template
    /// with its camelCase argument facts, prompts/get renders it into one
    /// user message, the optional argument's absence renders legally, and
    /// every malformed get earns its honest envelope.
    /// </summary>
    [Fact]
    public Task PromptsWalk_TheGetRendersOneUserMessage_AndTheMalformedOnesRefuseHonestly() =>
        ExchangeScriptedAsync(
            InitializeRequest + "\n" +
            PromptsListRequest + "\n" +
            PromptsGetRequest + "\n" +
            PromptsGetOptionalOnlyRequest + "\n" +
            PromptsGetNumericArgumentRequest + "\n" +
            PromptsGetArrayArgumentsRequest + "\n" +
            PromptsGetMissingNameRequest + "\n" +
            PromptsGetUnknownPromptRequest + "\n" +
            PromptsGetMissingRequiredRequest + "\n",
            setup: RegisterTestBriefPrompt,
            complete: captured => StdoutLines(captured).Length >= 9,
            drainBeforeAsserts: true,
            (_, captured) =>
            {
                var lines = StdoutLines(captured);
                lines.Should().HaveCount(9);

                using var list = JsonDocument.Parse(lines[1]);
                var prompts = list.RootElement.GetProperty("result").GetProperty("prompts");
                prompts.GetArrayLength().Should().Be(1);
                var listed = prompts[0];
                listed.GetProperty("name").GetString().Should().Be("test.brief");
                var listedArguments = listed.GetProperty("arguments");
                listedArguments.GetArrayLength().Should().Be(2);
                listedArguments[0].GetProperty("name").GetString().Should().Be("asset");
                listedArguments[0].GetProperty("required").GetBoolean().Should().BeTrue();
                listedArguments[1].GetProperty("required").GetBoolean().Should().BeFalse();

                using var get = JsonDocument.Parse(lines[2]);
                var full = get.RootElement.GetProperty("result");
                full.GetProperty("messages").GetArrayLength().Should().Be(1, "the rendered prompt rides ONE user message");
                var fullMessage = full.GetProperty("messages")[0];
                fullMessage.GetProperty("role").GetString().Should().Be("user");
                fullMessage.GetProperty("content").GetProperty("type").GetString().Should().Be("text");
                fullMessage.GetProperty("content").GetProperty("text").GetString().Should().Be(RenderedPromptText);

                // The OPTIONAL argument's absence is legal: the prompt renders
                // from the supplied asset alone.
                using var optionalOnly = JsonDocument.Parse(lines[3]);
                optionalOnly.RootElement.GetProperty("result").GetProperty("messages")[0]
                    .GetProperty("content").GetProperty("text").GetString().Should().Be("brief: BTC");

                AssertErrorCode(lines[4], -32602, "string");
                AssertErrorCode(lines[5], -32602, "object");
                AssertErrorCode(lines[6], -32602, "name");
                AssertErrorCode(lines[7], -32601, "Unknown prompt");
                AssertErrorCode(lines[8], -32602, "required");
            });

    /// <summary>A prompts/get for a prompt whose handler faulted earns the -32603 envelope, not a hang.</summary>
    [Fact]
    public Task PromptsGet_AFaultedHandler_EarnsTheInternalErrorEnvelope() =>
        ExchangeScriptedAsync(
            InitializeRequest + "\n" +
            "{\"jsonrpc\":\"2.0\",\"id\":\"2\",\"method\":\"prompts/get\",\"params\":{\"name\":\"test.boom\"}}\n",
            setup: host => host.RegisterPrompt(new McpPromptDefinition
            {
                Name = "test.boom",
                Description = "faults",
                Arguments = [],
                Handler = (_, _) => throw new InvalidOperationException("boom"),
            }),
            complete: captured => StdoutLines(captured).Length >= 2,
            drainBeforeAsserts: false,
            (logger, captured) =>
            {
                var lines = StdoutLines(captured);
                lines.Should().HaveCount(2);
                AssertErrorCode(lines[1], -32603, "Internal error");
                logger.Messages.Should().Contain(
                    message => message.Contains("faulted"),
                    "the fault goes to the honest logging channel");
            });

    #endregion

    #region Factories

    /// <summary>Registers the brief prompt the scripted prompts walk drives.</summary>
    private static void RegisterTestBriefPrompt(McpServerHost host) =>
        host.RegisterPrompt(new McpPromptDefinition
        {
            Name = "test.brief",
            Description = "the test brief",
            Arguments =
            [
                new McpPromptArgument("asset", "the asset to brief", Required: true),
                new McpPromptArgument("depth", "the brief's depth", Required: false),
            ],
            Handler = (arguments, _) =>
                Task.FromResult($"brief: {arguments.GetValueOrDefault("asset")} {arguments.GetValueOrDefault("depth")}".TrimEnd()),
        });

    /// <summary>
    /// The whole scripted exchange: redirect the console primitives, run the
    /// setup, start the host, wait until the completion predicate holds (a
    /// stalled host fails with its stdout attached), stop the host, run the
    /// asserts, and restore the console primitives even on failure.
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

            var deadline = DateTime.UtcNow + ResponseWait;
            while (!complete(captured))
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException(
                        "the MCP host never reached the scripted completion; stdout: " +
                        string.Join(" | ", StdoutLines(captured)));
                }

                await Task.Delay(PollStep, lifetime.Token);
            }

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

    /// <summary>Assert one envelope's error code and message fragment.</summary>
    private static void AssertErrorCode(string line, int expectedCode, string expectedFragment)
    {
        using var doc = JsonDocument.Parse(line);
        doc.RootElement.GetProperty("jsonrpc").GetString().Should().Be("2.0");
        var error = doc.RootElement.GetProperty("error");
        error.GetProperty("code").GetInt32().Should().Be(expectedCode);
        error.GetProperty("message").GetString().Should().Contain(expectedFragment);
    }

    /// <summary>Splits the captured stdout into its complete written lines.</summary>
    private static string[] StdoutLines(StringWriter captured) =>
        captured.ToString().Split([Environment.NewLine, "\n"], StringSplitOptions.RemoveEmptyEntries);

    #endregion

    /// <summary>
    /// A recording test logger: it collects both the levels and the rendered
    /// messages it was given, so the test can assert the host reports its
    /// failures to the logging pipeline (never to the console).
    /// </summary>
    private sealed class RecordingLogger : ILogger<McpServerHost>
    {
        /// <summary>The levels logged so far, in arrival order.</summary>
        public List<LogLevel> Levels { get; } = new();

        /// <summary>The rendered messages logged so far, in arrival order.</summary>
        public List<string> Messages { get; } = new();

        /// <inheritdoc/>
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

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
    }
}
