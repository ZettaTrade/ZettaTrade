namespace ZettaTrade.App.Tests.Ai;

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using ZettaTrade.AI.Chat;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// The streaming half of the Ollama chat provider (T-205) against a loopback
/// stand-in: the request posts the same conversation with
/// <c>stream: true</c>, the NDJSON reply's deltas yield the moment their
/// lines arrive (the first delta lands while the writer still parks), the
/// full answer is the deltas' concatenation, and the failure families match
/// the one-shot contract (an error status is a HttpRequestException, an
/// unreadable line or a stream closed without content is an
/// InvalidOperationException, a cancellation is an
/// OperationCanceledException, a stream that ends without the terminal line
/// names the truncation).
/// </summary>
public sealed class OllamaChatCompletionStreamingTests
{
    #region Constants

    /// <summary>The model tag the tests' settings serve.</summary>
    private const string TestModel = "test-model";

    /// <summary>The turn every test sends (one user turn).</summary>
    private static readonly ChatTurn[] TestTurns = [new ChatTurn(ChatTurn.UserRole, "how is the market?")];

    #endregion

    #region Fixtures

    /// <summary>The scripted streamed reply: two delta lines, then the empty terminal one.</summary>
    private static string NdjsonStream(string model) =>
        $"{{\"model\":\"{model}\",\"message\":{{\"role\":\"assistant\",\"content\":\"Hel\"}},\"done\":false}}\r\n" +
        $"{{\"model\":\"{model}\",\"message\":{{\"role\":\"assistant\",\"content\":\"lo wor\"}},\"done\":false}}\r\n" +
        $"{{\"model\":\"{model}\",\"message\":{{\"role\":\"assistant\",\"content\":\"\"}},\"done\":true}}";

    /// <summary>The delta lines of a reply that never carries the terminal line.</summary>
    private static string NdjsonWithoutTerminal(string model) =>
        $"{{\"model\":\"{model}\",\"message\":{{\"role\":\"assistant\",\"content\":\"a partial\"}},\"done\":false}}\r\n" +
        $"{{\"model\":\"{model}\",\"message\":{{\"role\":\"assistant\",\"content\":\" answer\"}},\"done\":false}}";

    /// <summary>A single terminal line whose content is empty: no content at all.</summary>
    private static string NdjsonEmptyStream(string model) =>
        $"{{\"model\":\"{model}\",\"message\":{{\"role\":\"assistant\",\"content\":\"\"}},\"done\":true}}";

    /// <summary>Settings stub that answers the AI section's keys with whatever it holds.</summary>
    private static ISettingsService StubSettings(string url, string model)
    {
        var settings = Substitute.For<ISettingsService>();
        settings
            .GetAsync<string>(AiSettingsKeys.Section, AiSettingsKeys.OllamaUrl, Arg.Any<string>())
            .Returns(url);
        settings
            .GetAsync<string>(AiSettingsKeys.Section, AiSettingsKeys.Model, Arg.Any<string>())
            .Returns(model);

        return settings;
    }

    /// <summary>
    /// The provider wired to its substitutes: the named-client factory hands
    /// one raw HttpClient (no timeouts of its own — the streamed read carries
    /// the tokens).
    /// </summary>
    private static OllamaChatCompletion CreateProvider(string url, string model)
    {
        var settings = StubSettings(url, model);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(new HttpClient());

        return new OllamaChatCompletion(settings, factory, NullLogger<OllamaChatCompletion>.Instance);
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

    #endregion

    #region Wire Shape

    /// <summary>
    /// An answered stream: the request posts to Ollama's chat path with
    /// <c>stream: true</c> (the streamed dialect of the same conversation),
    /// the deltas arrive in their line order, and their concatenation is the
    /// full answer.
    /// </summary>
    [Fact]
    public async Task StreamAsync_AnAnsweredStream_PostsTheStreamingRequestAndYieldsTheDeltasInOrder()
    {
        using var server = LoopbackChatServer.Start();
        var provider = CreateProvider(server.BaseUrl.OriginalString, TestModel);
        var serving = server.ServeAsync(200, NdjsonStream(TestModel));

        var deltas = await CollectAsync(provider.StreamAsync("the system prompt", TestTurns));

        var request = await serving;
        request.Path.Should().Be(LoopbackChatServer.ChatPath);
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
            $"{{\"model\":\"{TestModel}\",\"message\":{{\"role\":\"assistant\",\"content\":\"Hel\"}},\"done\":false}}\r\n",
            gate.Task,
            $"{{\"model\":\"{TestModel}\",\"message\":{{\"role\":\"assistant\",\"content\":\"lo wor\"}},\"done\":false}}\r\n" +
            $"{{\"model\":\"{TestModel}\",\"message\":{{\"role\":\"assistant\",\"content\":\"\"}},\"done\":true}}");

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
        var serving = server.ServeAsync(200, NdjsonEmptyStream(TestModel));

        var act = async () => await CollectAsync(provider.StreamAsync("the system prompt", TestTurns));

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("without message content");
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
        var body =
            $"{{\"model\":\"{TestModel}\",\"message\":{{\"role\":\"assistant\",\"content\":\"Hel\"}},\"done\":false}}\r\n" +
            "{not json at all";
        var serving = server.ServeAsync(200, body);

        var act = async () => await CollectAsync(provider.StreamAsync("the system prompt", TestTurns));

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("unreadable line").And.Contain(TestModel);
        await serving;
    }

    /// <summary>
    /// A stream that ends without the terminal line is a truncation: the
    /// never-closed family (the deltas arrived — the answer is unusable, the
    /// caller decides what the partial prefix is worth).
    /// </summary>
    [Fact]
    public async Task StreamAsync_AStreamThatClosesWithoutTheTerminalLine_ThrowsTheNeverClosedFamily()
    {
        using var server = LoopbackChatServer.Start();
        var provider = CreateProvider(server.BaseUrl.OriginalString, TestModel);
        var serving = server.ServeAsync(200, NdjsonWithoutTerminal(TestModel));

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
            $"{{\"model\":\"{TestModel}\",\"message\":{{\"role\":\"assistant\",\"content\":\"Hel\"}},\"done\":false}}\r\n",
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

    #endregion
}
