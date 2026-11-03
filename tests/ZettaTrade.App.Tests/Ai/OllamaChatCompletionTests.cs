namespace ZettaTrade.App.Tests.Ai;

using System;
using System.Linq;
using System.Net;
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
/// The Ollama chat completion provider against a loopback stand-in: the
/// request posts model + messages ([system rider] + the turns) to
/// <c>/api/chat</c> in camelCase, the answer is the final <c>done</c> line's
/// message content, failures surface in their exact families (error status →
/// HttpRequestException, an unusable body → InvalidOperationException, a
/// cancellation → OperationCanceledException), the endpoint and the model
/// come from the settings on every request, and blank stored values fall
/// back to the module's defaults.
/// </summary>
public sealed class OllamaChatCompletionTests
{
    #region Constants

    /// <summary>How long the cancellation test's budget lasts before the
    /// request must have given up (the loopback answers far faster than this
    /// — the budget fires only through the token).</summary>
    private static readonly TimeSpan RequestBudget = TimeSpan.FromSeconds(1);

    /// <summary>The model tag the tests' settings serve.</summary>
    private const string TestModel = "test-model";

    /// <summary>The answer carried by the scripted NDJSON reply.</summary>
    private const string ScriptedReply = "the full answer";

    /// <summary>The turn every test sends (one user turn).</summary>
    private static readonly ChatTurn[] TestTurns = [new ChatTurn(ChatTurn.UserRole, "how is the market?")];

    #endregion

    #region Fixtures

    /// <summary>The scripted NDJSON reply: a partial turn, then the final done line.</summary>
    private static string NdjsonReply(string model) =>
        $"{{\"model\":\"{model}\",\"message\":{{\"content\":\"a partial turn\"}},\"done\":false}}\r\n" +
        $"{{\"model\":\"{model}\",\"message\":{{\"content\":\"{ScriptedReply}\"}},\"done\":true}}";

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
    /// one raw HttpClient (no timeouts of its own — the request carries the tokens).
    /// </summary>
    private static (OllamaChatCompletion Provider, ISettingsService Settings) CreateProvider(
        string url,
        string model)
    {
        var settings = StubSettings(url, model);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(new HttpClient());

        return (new OllamaChatCompletion(settings, factory, NullLogger<OllamaChatCompletion>.Instance), settings);
    }

    /// <summary>The messages wire of a captured request: (role, content) pairs.</summary>
    private static List<(string? Role, string? Content)> MessagesOf(string body) =>
        JsonDocument.Parse(body)
            .RootElement.GetProperty("messages")
            .EnumerateArray()
            .Select(message => (
                message.GetProperty("role").GetString(),
                message.GetProperty("content").GetString()))
            .ToList();

    /// <summary>The wire model tag of a captured request body.</summary>
    private static string? ModelOf(string body) =>
        JsonDocument.Parse(body).RootElement.GetProperty("model").GetString();

    #endregion

    #region Wire Shape

    /// <summary>
    /// An answered chat: the request posts to Ollama's chat path with the
    /// model and the [system rider] + turns in camelCase, and the answer is
    /// the final done line's content (the partial line stays a partial).
    /// </summary>
    [Fact]
    public async Task CompleteAsync_AnAnsweredChat_PostsTheModelAndTheConversationToTheChatPath()
    {
        // Arrange
        using var server = LoopbackChatServer.Start();
        var (provider, _) = CreateProvider(server.BaseUrl.OriginalString, TestModel);
        var serving = server.ServeAsync(200, NdjsonReply(TestModel));

        // Act
        var reply = await provider.CompleteAsync("the system prompt", TestTurns);

        // Assert
        reply.Should().Be(ScriptedReply, "the final done line carries the full answer");
        var request = await serving;
        request.Path.Should().Be(LoopbackChatServer.ChatPath);
        ModelOf(request.Body).Should().Be(TestModel);
        JsonDocument.Parse(request.Body).RootElement.GetProperty("stream").GetBoolean()
            .Should().BeFalse("the provider chats one-shot, not streamed");
        MessagesOf(request.Body).Should().Equal(
            ("system", "the system prompt"),
            (ChatTurn.UserRole, "how is the market?"));
    }

    /// <summary>
    /// An empty conversation sends the system rider alone (the conversation
    /// has no turns yet — the model still receives its instructions).
    /// </summary>
    [Fact]
    public async Task CompleteAsync_AnEmptyConversation_SendsTheSystemRiderAlone()
    {
        // Arrange
        using var server = LoopbackChatServer.Start();
        var (provider, _) = CreateProvider(server.BaseUrl.OriginalString, TestModel);
        var serving = server.ServeAsync(200, NdjsonReply(TestModel));

        // Act: the conversation begins without any turns (chat context only)
        await provider.CompleteAsync("the system prompt", Array.Empty<ChatTurn>());

        // Assert
        var request = await serving;
        MessagesOf(request.Body).Should().Equal(("system", "the system prompt"));
    }

    /// <summary>
    /// A blank system prompt is a caller bug: the provider refuses loudly
    /// instead of asking a model to answer nothing.
    /// </summary>
    [Fact]
    public async Task CompleteAsync_ABlankSystemPrompt_Throws()
    {
        // Arrange
        var (provider, _) = CreateProvider("http://localhost/stub", TestModel);

        // Act / Assert
        await Assert.ThrowsAsync<ArgumentException>(
            () => provider.CompleteAsync("   ", TestTurns));
    }

    #endregion

    #region Settings

    /// <summary>
    /// Endpoint and model are read from the settings on EVERY request: a
    /// settings change switches them live — the next call posts elsewhere
    /// with the new model, no restart needed.
    /// </summary>
    [Fact]
    public async Task CompleteAsync_ASettingsChange_SwitchesTheEndpointAndTheModelAtRuntime()
    {
        // Arrange
        using var first = LoopbackChatServer.Start();
        using var second = LoopbackChatServer.Start();
        var (provider, settings) = CreateProvider(first.BaseUrl.OriginalString, "first-model");
        var firstServing = first.ServeAsync(200, NdjsonReply("first-model"));
        await provider.CompleteAsync("the system prompt", TestTurns);
        (await firstServing).Path.Should().Be(LoopbackChatServer.ChatPath);

        // Act: the settings file changed between requests
        settings
            .GetAsync<string>(AiSettingsKeys.Section, AiSettingsKeys.OllamaUrl, Arg.Any<string>())
            .Returns(second.BaseUrl.OriginalString);
        settings
            .GetAsync<string>(AiSettingsKeys.Section, AiSettingsKeys.Model, Arg.Any<string>())
            .Returns("second-model");
        var secondServing = second.ServeAsync(200, NdjsonReply("second-model"));
        await provider.CompleteAsync("the system prompt", TestTurns);
        var secondRequest = await secondServing;

        // Assert
        ModelOf(secondRequest.Body).Should().Be("second-model");
        MessagesOf(secondRequest.Body).Should().Contain(("system", "the system prompt"));
    }

    /// <summary>
    /// A blank stored model (the key never written, or emptied in the file)
    /// falls back to the module's documented default instead of asking a
    /// model with no name.
    /// </summary>
    [Fact]
    public async Task CompleteAsync_ABlankStoredModel_FallsBackToTheModuleDefault()
    {
        // Arrange
        using var server = LoopbackChatServer.Start();
        var (provider, _) = CreateProvider(server.BaseUrl.OriginalString, "   ");
        var serving = server.ServeAsync(200, NdjsonReply(AiSettingsKeys.DefaultModel));

        // Act
        var reply = await provider.CompleteAsync("the system prompt", TestTurns);

        // Assert
        reply.Should().Be(ScriptedReply);
        var request = await serving;
        ModelOf(request.Body).Should().Be(AiSettingsKeys.DefaultModel);
    }

    #endregion

    #region Failures

    /// <summary>
    /// An error status is the "assistant off" family: it surfaces as
    /// HttpRequestException with its status code intact (the chat reports
    /// the honest reason).
    /// </summary>
    [Fact]
    public async Task CompleteAsync_AnErrorStatus_SurfacesAsAnUnreachableAssistant()
    {
        // Arrange
        using var server = LoopbackChatServer.Start();
        var (provider, _) = CreateProvider(server.BaseUrl.OriginalString, TestModel);
        var serving = server.ServeAsync(500, "the model is not loaded");

        // Act / Assert
        var failure = await Assert.ThrowsAsync<HttpRequestException>(
            () => provider.CompleteAsync("the system prompt", TestTurns));
        failure.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        await serving;
    }

    /// <summary>
    /// An unreadable reply line (the server answered something that is not
    /// the chat NDJSON) is an unusable answer, not a transport failure: the
    /// provider surfaces it as InvalidOperationException, naming the model.
    /// </summary>
    [Fact]
    public async Task CompleteAsync_AnUnreadableReply_SurfacesAsAnUnusableAnswer()
    {
        // Arrange
        using var server = LoopbackChatServer.Start();
        var (provider, _) = CreateProvider(server.BaseUrl.OriginalString, TestModel);
        var serving = server.ServeAsync(200, "definitely not json");

        // Act / Assert
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.CompleteAsync("the system prompt", TestTurns));
        failure.Message.Should().Contain("unreadable");
        failure.Message.Should().Contain(TestModel, "the honest message names the model");
        await serving;
    }

    /// <summary>
    /// A done line whose message content is blank is an unusable answer: the
    /// provider does not hand a whitespace answer to the chat.
    /// </summary>
    [Fact]
    public async Task CompleteAsync_ABlankReplyContent_SurfacesAsAnUnusableAnswer()
    {
        // Arrange
        using var server = LoopbackChatServer.Start();
        var (provider, _) = CreateProvider(server.BaseUrl.OriginalString, TestModel);
        var blankReply = "{\"model\":\"test-model\",\"message\":{\"content\":\"   \"},\"done\":true}";
        var serving = server.ServeAsync(200, blankReply);

        // Act / Assert
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.CompleteAsync("the system prompt", TestTurns));
        failure.Message.Should().Contain("message content", "the honest no-content failure");
        await serving;
    }

    /// <summary>
    /// A stream that never reports done (the connection closed mid-answer)
    /// is an unusable answer: the provider surfaces what happened honestly.
    /// </summary>
    [Fact]
    public async Task CompleteAsync_AStreamThatNeverCloses_SurfacesAsAnUnusableAnswer()
    {
        // Arrange
        using var server = LoopbackChatServer.Start();
        var (provider, _) = CreateProvider(server.BaseUrl.OriginalString, TestModel);
        var unfinishedReply = "{\"model\":\"test-model\",\"message\":{\"content\":\"a partial turn\"},\"done\":false}";
        var serving = server.ServeAsync(200, unfinishedReply);

        // Act / Assert
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.CompleteAsync("the system prompt", TestTurns));
        failure.Message.Should().Contain("never closed", "the honest unfinished-stream failure");
        await serving;
    }

    /// <summary>
    /// A canceled request stops waiting for its answer and surfaces
    /// OperationCanceledException (the caller owns the budget).
    /// </summary>
    [Fact]
    public async Task CompleteAsync_ACanceledRequest_StopsWaitingForTheAnswer()
    {
        // Arrange: a listening server that never answers its queue (no
        // ServeAsync parked) — the budget fires through the token alone
        using var server = LoopbackChatServer.Start();
        var (provider, _) = CreateProvider(server.BaseUrl.OriginalString, TestModel);
        using var budget = new CancellationTokenSource(RequestBudget);

        // Act / Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.CompleteAsync("the system prompt", TestTurns, budget.Token));
    }

    #endregion
}
