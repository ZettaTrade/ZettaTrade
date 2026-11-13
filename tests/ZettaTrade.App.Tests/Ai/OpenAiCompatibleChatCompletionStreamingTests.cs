namespace ZettaTrade.App.Tests.Ai;

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using ZettaTrade.AI.Chat;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// The streaming half of the OpenAI-compatible chat provider (T-207) against
/// a loopback stand-in: the request posts the same conversation with
/// <c>stream: true</c>, the SSE reply's <c>data:</c> events yield each
/// <c>choices[0].delta.content</c> the moment its line arrives (the first
/// delta lands while the writer still parks), the <c>data: [DONE]</c>
/// sentinel closes the stream, and the failure families match the one-shot
/// contract (an error status is a HttpRequestException, an unreadable line
/// or a stream closed without content is an InvalidOperationException, a
/// cancellation is an OperationCanceledException, a stream that ends without
/// the sentinel names the truncation). The stored key rides the streamed
/// request too and never reaches the log.
/// </summary>
public sealed class OpenAiCompatibleChatCompletionStreamingTests
{
    #region Constants

    /// <summary>The model name the tests' settings serve.</summary>
    private const string TestModel = "test-model";

    /// <summary>The API key the keyed tests store.</summary>
    private const string TestApiKey = "test-key-9f81";

    /// <summary>The turn every test sends (one user turn).</summary>
    private static readonly ChatTurn[] TestTurns = [new ChatTurn(ChatTurn.UserRole, "how is the market?")];

    #endregion

    #region Fixtures

    /// <summary>The scripted streamed reply: two delta events (blank lines
    /// between), then the [DONE] sentinel.</summary>
    private static string SseStream() =>
        "data: {\"choices\":[{\"delta\":{\"content\":\"Hel\"}}]}\r\n" +
        "\r\n" +
        "data: {\"choices\":[{\"delta\":{\"content\":\"lo wor\"}}]}\r\n" +
        "\r\n" +
        "data: [DONE]";

    /// <summary>The delta events of a reply that never carries the sentinel.</summary>
    private static string SseWithoutDone() =>
        "data: {\"choices\":[{\"delta\":{\"content\":\"a partial\"}}]}\r\n" +
        "data: {\"choices\":[{\"delta\":{\"content\":\" answer\"}}]}\r\n";

    /// <summary>The sentinel alone: no content at all.</summary>
    private static string SseEmptyStream() => "data: [DONE]";

    /// <summary>Settings stub that answers the OpenAI-compatible section's keys with whatever it holds.</summary>
    private static ISettingsService StubSettings(string url, string model)
    {
        var settings = Substitute.For<ISettingsService>();
        settings
            .GetAsync<string>(
                AiSettingsKeys.OpenAiCompatibleSection,
                AiSettingsKeys.OpenAiCompatibleBaseUrl,
                Arg.Any<string>())
            .Returns(url);
        settings
            .GetAsync<string>(
                AiSettingsKeys.OpenAiCompatibleSection,
                AiSettingsKeys.OpenAiCompatibleModel,
                Arg.Any<string>())
            .Returns(model);

        return settings;
    }

    /// <summary>Secret store stub that answers the API key slot with whatever it holds.</summary>
    private static ISecretStore StubSecrets(string? apiKey)
    {
        var secrets = Substitute.For<ISecretStore>();
        secrets
            .GetAsync(AiSettingsKeys.OpenAiCompatibleApiKeySecretKey, Arg.Any<CancellationToken>())
            .Returns(apiKey);
        return secrets;
    }

    /// <summary>
    /// The provider wired to its substitutes: the named-client factory hands
    /// one raw HttpClient (no timeouts of its own — the streamed read carries
    /// the tokens).
    /// </summary>
    private static OpenAiCompatibleChatCompletion CreateProvider(
        string url,
        string model,
        string? apiKey = null,
        ILogger<OpenAiCompatibleChatCompletion>? logger = null) =>
        new(
            StubSettings(url, model),
            StubFactory(),
            StubSecrets(apiKey),
            logger ?? NullLogger<OpenAiCompatibleChatCompletion>.Instance);

    /// <summary>A named-client factory handing one raw HttpClient.</summary>
    private static IHttpClientFactory StubFactory()
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(new HttpClient());
        return factory;
    }

    /// <summary>
    /// Collect the whole stream: the deltas in arrival order, so the facts
    /// can assert the answer's assembly.
    /// </summary>
    private static async Task<List<string>> CollectAsync(
        IAsyncEnumerable<string> stream,
        CancellationToken ct = default)
    {
        var deltas = new List<string>();
        await foreach (var delta in stream.WithCancellation(ct))
        {
            deltas.Add(delta);
        }

        return deltas;
    }

    /// <summary>A capturing logger: every formatted entry and every exception text lands in one searchable pool.</summary>
    private sealed class RecordingLogger : ILogger<OpenAiCompatibleChatCompletion>
    {
        public List<string> Entries { get; } = [];

        public string AllText => string.Join(Environment.NewLine, Entries);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(formatter(state, exception));
            if (exception is not null)
            {
                Entries.Add(exception.ToString());
            }
        }
    }

    #endregion

    #region Wire Shape

    /// <summary>
    /// An answered stream: the request posts to the chat-completions path
    /// with <c>stream: true</c> (the streamed dialect of the same
    /// conversation), the deltas arrive in their event order, and their
    /// concatenation is the full answer.
    /// </summary>
    [Fact]
    public async Task StreamAsync_AnAnsweredStream_PostsTheStreamingRequestAndYieldsTheDeltasInOrder()
    {
        using var server = LoopbackChatServer.Start();
        var provider = CreateProvider(server.BaseUrl.OriginalString, TestModel);
        var serving = server.ServeAsync(200, SseStream());

        var deltas = await CollectAsync(provider.StreamAsync("the system prompt", TestTurns));

        var request = await serving;
        request.Path.Should().Be("/chat/completions");
        JsonDocument.Parse(request.Body).RootElement.GetProperty("stream").GetBoolean()
            .Should().BeTrue("the streamed reply asks the server to stream");
        deltas.Should().Equal("Hel", "lo wor");
        string.Concat(deltas).Should().Be("Hello wor");
    }

    /// <summary>
    /// The stream is not buffered: the first delta lands while the writer
    /// still parks on its gate — the consumer renders before the reply
    /// finishes.
    /// </summary>
    [Fact]
    public async Task StreamAsync_TheFirstDeltaArrivesWhileTheWriterStillParks()
    {
        using var server = LoopbackChatServer.Start();
        var provider = CreateProvider(server.BaseUrl.OriginalString, TestModel);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serving = server.ServePacedAsync(
            200,
            "data: {\"choices\":[{\"delta\":{\"content\":\"Hel\"}}]}\r\n",
            gate.Task,
            "data: {\"choices\":[{\"delta\":{\"content\":\"lo wor\"}}]}\r\n" +
            "data: [DONE]");

        var collected = new List<string>();
        await foreach (var delta in provider.StreamAsync("the system prompt", TestTurns))
        {
            collected.Add(delta);
            break;
        }

        collected.Should().ContainSingle("the first delta rendered while the writer is parked")
            .Which.Should().Be("Hel");

        gate.TrySetResult();
        await serving;
    }

    /// <summary>
    /// A data line without the space is still an event (some servers write
    /// the field bare) and a comment line is skipped: both shapes answer the
    /// same way.
    /// </summary>
    [Fact]
    public async Task StreamAsync_ABareDataLineAndAComment_AnswerTheSame()
    {
        using var server = LoopbackChatServer.Start();
        var provider = CreateProvider(server.BaseUrl.OriginalString, TestModel);
        var serving = server.ServeAsync(
            200,
            ": keep-alive\r\n" +
            "data:{\"choices\":[{\"delta\":{\"content\":\"one\"}}]}\r\n" +
            "\r\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"two\"}}]}\r\n" +
            "data: [DONE]");

        var deltas = await CollectAsync(provider.StreamAsync("the system prompt", TestTurns));

        deltas.Should().Equal("one", "two");
        await serving;
    }

    #endregion

    #region Secrets

    /// <summary>
    /// A stored key rides the STREAMED request too: the streaming half is the
    /// same endpoint with the same authorization shape.
    /// </summary>
    [Fact]
    public async Task StreamAsync_TheStoredKey_RidesTheStreamedRequestToo()
    {
        using var server = LoopbackChatServer.Start();
        var provider = CreateProvider(server.BaseUrl.OriginalString, TestModel, TestApiKey);
        var serving = server.ServeAsync(200, SseStream());

        await CollectAsync(provider.StreamAsync("the system prompt", TestTurns));

        var request = await serving;
        request.Authorization.Should().Be($"Bearer {TestApiKey}");
    }

    /// <summary>
    /// The streaming secret-handling pin: the key never reaches a log on the
    /// streamed path either.
    /// </summary>
    [Fact]
    public async Task StreamAsync_TheApiKey_NeverReachesTheLog()
    {
        using var server = LoopbackChatServer.Start();
        var logger = new RecordingLogger();
        var provider = CreateProvider(server.BaseUrl.OriginalString, TestModel, TestApiKey, logger);
        var serving = server.ServeAsync(200, SseStream());

        await CollectAsync(provider.StreamAsync("the system prompt", TestTurns));
        await serving;

        logger.Entries.Should().NotBeEmpty();
        logger.AllText.Should().NotContain(TestApiKey, "the key never reaches a log");
    }

    #endregion

    #region Failure Families

    /// <summary>
    /// A stream that closes without carrying any content is the one-shot's
    /// missing-content family: an unusable reply, never a silently empty
    /// answer.
    /// </summary>
    [Fact]
    public async Task StreamAsync_AStreamClosedWithoutContent_ThrowsTheUnusableReplyFamily()
    {
        using var server = LoopbackChatServer.Start();
        var provider = CreateProvider(server.BaseUrl.OriginalString, TestModel);
        var serving = server.ServeAsync(200, SseEmptyStream());

        var act = async () => await CollectAsync(provider.StreamAsync("the system prompt", TestTurns));

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("without message content").And.Contain(TestModel);
        await serving;
    }

    /// <summary>
    /// An unreadable line breaks the reply honestly: the model name rides
    /// the message, the family matches the one-shot's unusable-reply shape.
    /// </summary>
    [Fact]
    public async Task StreamAsync_AnUnreadableLine_ThrowsTheUnusableReplyFamily()
    {
        using var server = LoopbackChatServer.Start();
        var provider = CreateProvider(server.BaseUrl.OriginalString, TestModel);
        var serving = server.ServeAsync(
            200,
            "data: {\"choices\":[{\"delta\":{\"content\":\"Hel\"}}]}\r\n" +
            "data: {not json at all");

        var act = async () => await CollectAsync(provider.StreamAsync("the system prompt", TestTurns));

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("unreadable line").And.Contain(TestModel);
        await serving;
    }

    /// <summary>
    /// A line that is not a data event at all is the same unreadable family:
    /// the SSE framing is known, a foreign line is an unusable answer.
    /// </summary>
    [Fact]
    public async Task StreamAsync_AForeignNonDataLine_ThrowsTheUnusableReplyFamily()
    {
        using var server = LoopbackChatServer.Start();
        var provider = CreateProvider(server.BaseUrl.OriginalString, TestModel);
        var serving = server.ServeAsync(
            200,
            "data: {\"choices\":[{\"delta\":{\"content\":\"Hel\"}}]}\r\n" +
            "banana line\r\n");

        var act = async () => await CollectAsync(provider.StreamAsync("the system prompt", TestTurns));

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("not a data event");
        await serving;
    }

    /// <summary>
    /// A stream that ends without the sentinel is a truncation: the
    /// never-closed family (the deltas arrived — the answer is unusable, the
    /// caller decides what the partial prefix is worth).
    /// </summary>
    [Fact]
    public async Task StreamAsync_AStreamThatClosesWithoutTheSentinel_ThrowsTheNeverClosedFamily()
    {
        using var server = LoopbackChatServer.Start();
        var provider = CreateProvider(server.BaseUrl.OriginalString, TestModel);
        var serving = server.ServeAsync(200, SseWithoutDone());

        var act = async () => await CollectAsync(provider.StreamAsync("the system prompt", TestTurns));

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("never closed the chat stream");
        await serving;
    }

    /// <summary>
    /// The caller's token cuts a mid-flight stream: the first delta already
    /// rendered, the next read throws the cancellation family.
    /// </summary>
    [Fact]
    public async Task StreamAsync_TheCallerCancelsMidStream_ThrowsOperationCanceled()
    {
        using var server = LoopbackChatServer.Start();
        var provider = CreateProvider(server.BaseUrl.OriginalString, TestModel);
        using var cts = new CancellationTokenSource();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serving = server.ServePacedAsync(
            200,
            "data: {\"choices\":[{\"delta\":{\"content\":\"Hel\"}}]}\r\n",
            gate.Task);

        try
        {
            var enumerator = provider
                .StreamAsync("the system prompt", TestTurns, cts.Token)
                .GetAsyncEnumerator(cts.Token);
            (await enumerator.MoveNextAsync()).Should().BeTrue("the first delta renders before the cut");

            cts.Cancel();
            var act = async () =>
            {
                while (await enumerator.MoveNextAsync())
                {
                }
            };

            await act.Should().ThrowAsync<OperationCanceledException>();
        }
        finally
        {
            // The writer is parked while the consumer is gone: release it so
            // the serve task settles (the abort faults are swallowed server-
            // side).
            gate.TrySetResult();
        }

        await serving;
    }

    /// <summary>
    /// An error status is the transport family, exactly as the one-shot
    /// reports it.
    /// </summary>
    [Fact]
    public async Task StreamAsync_AnErrorStatus_ThrowsHttpRequestException()
    {
        using var server = LoopbackChatServer.Start();
        var provider = CreateProvider(server.BaseUrl.OriginalString, TestModel);
        var serving = server.ServeAsync(500, "the server broke");

        var act = async () => await CollectAsync(provider.StreamAsync("the system prompt", TestTurns));

        await act.Should().ThrowAsync<HttpRequestException>();
        await serving;
    }

    /// <summary>
    /// A blank stored model refuses before anything is sent: the same honest
    /// no-model error the one-shot keeps.
    /// </summary>
    [Fact]
    public async Task StreamAsync_ABlankStoredModel_ThrowsTheHonestError()
    {
        var provider = CreateProvider("http://localhost/stub", "   ");

        var act = async () => await CollectAsync(provider.StreamAsync("the system prompt", TestTurns));

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("No OpenAI-compatible model is configured");
    }

    #endregion
}
