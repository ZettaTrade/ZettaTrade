namespace ZettaTrade.App.Tests.Ai;

using System;
using System.Linq;
using System.Net;
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
/// The OpenAI-compatible chat completion provider (T-207) against a loopback
/// stand-in: the request posts model + messages ([system rider] + the turns)
/// to <c>chat/completions</c> on the configured base URL in camelCase, the
/// answer is <c>choices[0].message.content</c>, the stored API key rides the
/// Authorization header (an absent one leaves the request unauthenticated —
/// local servers need no key) and the key never reaches the log. Failures
/// surface in their exact families (error status → HttpRequestException, an
/// unusable body → InvalidOperationException, a cancellation →
/// OperationCanceledException, a blank model → the honest no-model error),
/// and base URL and model come from the settings on every request.
/// </summary>
public sealed class OpenAiCompatibleChatCompletionTests
{
    #region Constants

    /// <summary>How long the cancellation test's budget lasts before the
    /// request must have given up (the loopback answers far faster than this
    /// — the budget fires only through the token).</summary>
    private static readonly TimeSpan RequestBudget = TimeSpan.FromSeconds(1);

    /// <summary>The model name the tests' settings serve.</summary>
    private const string TestModel = "test-model";

    /// <summary>The answer carried by the scripted reply.</summary>
    private const string ScriptedReply = "the full answer";

    /// <summary>The API key the keyed tests store (juicy enough that any
    /// leak is loud; short by design — the gate's secret scanner flags a
    /// 16+-char value beside an ApiKey-like name, and this is a fake).</summary>
    private const string TestApiKey = "test-key-9f81";

    /// <summary>The turn every test sends (one user turn).</summary>
    private static readonly ChatTurn[] TestTurns = [new ChatTurn(ChatTurn.UserRole, "how is the market?")];

    #endregion

    #region Fixtures

    /// <summary>The scripted chat-completions reply: one choice, full content.</summary>
    private static string Reply(string content) =>
        $"{{\"id\":\"chatcmpl-1\",\"choices\":[{{\"index\":0,\"message\":{{\"role\":\"assistant\",\"content\":\"{content}\"}}}}]}}";

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

    /// <summary>A named-client factory handing one raw HttpClient (no timeouts of its own — the request carries the tokens).</summary>
    private static IHttpClientFactory StubFactory()
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(new HttpClient());
        return factory;
    }

    /// <summary>The provider wired to its substitutes.</summary>
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

    /// <summary>The messages wire of a captured request: (role, content) pairs.</summary>
    private static List<(string? Role, string? Content)> MessagesOf(string body) =>
        JsonDocument.Parse(body)
            .RootElement.GetProperty("messages")
            .EnumerateArray()
            .Select(message => (
                message.GetProperty("role").GetString(),
                message.GetProperty("content").GetString()))
            .ToList();

    /// <summary>The wire model name of a captured request body.</summary>
    private static string? ModelOf(string body) =>
        JsonDocument.Parse(body).RootElement.GetProperty("model").GetString();

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
    /// An answered chat: the request posts to the chat-completions path with
    /// the model and the [system rider] + turns in camelCase, and the answer
    /// is the choice's content.
    /// </summary>
    [Fact]
    public async Task CompleteAsync_AnAnsweredChat_PostsTheModelAndTheConversationToTheChatPath()
    {
        // Arrange
        using var server = LoopbackChatServer.Start();
        var provider = CreateProvider(server.BaseUrl.OriginalString, TestModel);
        var serving = server.ServeAsync(200, Reply(ScriptedReply));

        // Act
        var reply = await provider.CompleteAsync("the system prompt", TestTurns);

        // Assert
        reply.Should().Be(ScriptedReply);
        var request = await serving;
        request.Path.Should().Be("/chat/completions");
        ModelOf(request.Body).Should().Be(TestModel);
        JsonDocument.Parse(request.Body).RootElement.GetProperty("stream").GetBoolean()
            .Should().BeFalse("the provider chats one-shot, not streamed");
        MessagesOf(request.Body).Should().Equal(
            ("system", "the system prompt"),
            (ChatTurn.UserRole, "how is the market?"));
    }

    /// <summary>
    /// A base URL with a version segment keeps it: the provider appends the
    /// resource path to the base verbatim (the local LM Studio shape carries
    /// <c>/v1</c>).
    /// </summary>
    [Fact]
    public async Task CompleteAsync_ABaseUrlWithAVersionSegment_AppendsTheResourcePath()
    {
        // Arrange
        using var server = LoopbackChatServer.Start();
        var provider = CreateProvider(server.BaseUrl.OriginalString + "/v1", TestModel);
        var serving = server.ServeAsync(200, Reply(ScriptedReply));

        // Act
        await provider.CompleteAsync("the system prompt", TestTurns);

        // Assert
        var request = await serving;
        request.Path.Should().Be("/v1/chat/completions");
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
        var provider = CreateProvider(server.BaseUrl.OriginalString, TestModel);
        var serving = server.ServeAsync(200, Reply(ScriptedReply));

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
        var provider = CreateProvider("http://localhost/stub", TestModel);

        // Act / Assert
        await Assert.ThrowsAsync<ArgumentException>(
            () => provider.CompleteAsync("   ", TestTurns));
    }

    #endregion

    #region Settings

    /// <summary>
    /// Base URL and model are read from the settings on EVERY request: a
    /// settings change switches them live — the next call posts elsewhere
    /// with the new model, no restart needed.
    /// </summary>
    [Fact]
    public async Task CompleteAsync_ASettingsChange_SwitchesTheEndpointAndTheModelAtRuntime()
    {
        // Arrange
        using var first = LoopbackChatServer.Start();
        using var second = LoopbackChatServer.Start();
        var settings = StubSettings(first.BaseUrl.OriginalString, "first-model");
        var provider = new OpenAiCompatibleChatCompletion(
            settings,
            StubFactory(),
            StubSecrets(null),
            NullLogger<OpenAiCompatibleChatCompletion>.Instance);
        var firstServing = first.ServeAsync(200, Reply(ScriptedReply));
        await provider.CompleteAsync("the system prompt", TestTurns);
        (await firstServing).Path.Should().Be("/chat/completions");

        // Act: the settings file changed between requests
        settings
            .GetAsync<string>(
                AiSettingsKeys.OpenAiCompatibleSection,
                AiSettingsKeys.OpenAiCompatibleBaseUrl,
                Arg.Any<string>())
            .Returns(second.BaseUrl.OriginalString);
        settings
            .GetAsync<string>(
                AiSettingsKeys.OpenAiCompatibleSection,
                AiSettingsKeys.OpenAiCompatibleModel,
                Arg.Any<string>())
            .Returns("second-model");
        var secondServing = second.ServeAsync(200, Reply(ScriptedReply));
        await provider.CompleteAsync("the system prompt", TestTurns);
        var secondRequest = await secondServing;

        // Assert
        ModelOf(secondRequest.Body).Should().Be("second-model");
        MessagesOf(secondRequest.Body).Should().Contain(("system", "the system prompt"));
    }

    /// <summary>
    /// A blank stored model has NO default: the send refuses honestly — a
    /// model name is the user's choice, never a guess.
    /// </summary>
    [Fact]
    public async Task CompleteAsync_ABlankStoredModel_ThrowsTheHonestError()
    {
        // Arrange
        var provider = CreateProvider("http://localhost/stub", "   ");

        // Act / Assert
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.CompleteAsync("the system prompt", TestTurns));
        failure.Message.Should().Contain("No OpenAI-compatible model is configured");
    }

    #endregion

    #region Secrets

    /// <summary>
    /// A stored API key rides the request as the Authorization Bearer value:
    /// the secret store is the key's only home, the wire is where it works.
    /// </summary>
    [Fact]
    public async Task CompleteAsync_AStoredKey_RidesTheAuthorizationHeader()
    {
        // Arrange
        using var server = LoopbackChatServer.Start();
        var provider = CreateProvider(server.BaseUrl.OriginalString, TestModel, TestApiKey);
        var serving = server.ServeAsync(200, Reply(ScriptedReply));

        // Act
        await provider.CompleteAsync("the system prompt", TestTurns);

        // Assert
        var request = await serving;
        request.Authorization.Should().Be($"Bearer {TestApiKey}");
    }

    /// <summary>
    /// An absent (or blank) key leaves the request unauthenticated: a local
    /// server needs no key, and the provider never fabricates one.
    /// </summary>
    [Fact]
    public async Task CompleteAsync_AnAbsentKey_TheRequestTravelsUnauthenticated()
    {
        // Arrange
        using var server = LoopbackChatServer.Start();
        var provider = CreateProvider(server.BaseUrl.OriginalString, TestModel, apiKey: null);
        var serving = server.ServeAsync(200, Reply(ScriptedReply));

        // Act
        await provider.CompleteAsync("the system prompt", TestTurns);

        // Assert
        var request = await serving;
        request.Authorization.Should().BeNull("no stored key means no Authorization header");
    }

    /// <summary>
    /// THE secret-handling pin: the key never reaches a log — no formatted
    /// message and no exception text carries it (a leaked key is the one
    /// failure this provider may never commit).
    /// </summary>
    [Fact]
    public async Task CompleteAsync_TheApiKey_NeverReachesTheLog()
    {
        // Arrange
        using var server = LoopbackChatServer.Start();
        var logger = new RecordingLogger();
        var provider = CreateProvider(server.BaseUrl.OriginalString, TestModel, TestApiKey, logger);
        var serving = server.ServeAsync(200, Reply(ScriptedReply));

        // Act
        await provider.CompleteAsync("the system prompt", TestTurns);
        await serving;

        // Assert
        logger.Entries.Should().NotBeEmpty("the pin is meaningful only over a talkative request");
        logger.AllText.Should().NotContain(TestApiKey, "the key never reaches a log");
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
        var provider = CreateProvider(server.BaseUrl.OriginalString, TestModel);
        var serving = server.ServeAsync(500, "the model is not loaded");

        // Act / Assert
        var failure = await Assert.ThrowsAsync<HttpRequestException>(
            () => provider.CompleteAsync("the system prompt", TestTurns));
        failure.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        await serving;
    }

    /// <summary>
    /// An unreadable reply (the server answered something that is not the
    /// chat JSON) is an unusable answer, not a transport failure: the
    /// provider surfaces it as InvalidOperationException, naming the model.
    /// </summary>
    [Fact]
    public async Task CompleteAsync_AnUnreadableReply_SurfacesAsAnUnusableAnswer()
    {
        // Arrange
        using var server = LoopbackChatServer.Start();
        var provider = CreateProvider(server.BaseUrl.OriginalString, TestModel);
        var serving = server.ServeAsync(200, "definitely not json");

        // Act / Assert
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.CompleteAsync("the system prompt", TestTurns));
        failure.Message.Should().Contain("unreadable");
        failure.Message.Should().Contain(TestModel, "the honest message names the model");
        await serving;
    }

    /// <summary>
    /// A reply whose choice content is blank is an unusable answer: the
    /// provider does not hand a whitespace answer to the chat.
    /// </summary>
    [Fact]
    public async Task CompleteAsync_ABlankReplyContent_SurfacesAsAnUnusableAnswer()
    {
        // Arrange
        using var server = LoopbackChatServer.Start();
        var provider = CreateProvider(server.BaseUrl.OriginalString, TestModel);
        var serving = server.ServeAsync(200, Reply("   "));

        // Act / Assert
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.CompleteAsync("the system prompt", TestTurns));
        failure.Message.Should().Contain("without message content", "the honest no-content failure");
        failure.Message.Should().Contain(TestModel);
        await serving;
    }

    /// <summary>
    /// A reply without choices at all is the same unusable family: no
    /// choices, no content, no answer.
    /// </summary>
    [Fact]
    public async Task CompleteAsync_AReplyWithoutChoices_SurfacesAsAnUnusableAnswer()
    {
        // Arrange
        using var server = LoopbackChatServer.Start();
        var provider = CreateProvider(server.BaseUrl.OriginalString, TestModel);
        var serving = server.ServeAsync(200, "{\"id\":\"chatcmpl-1\"}");

        // Act / Assert
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.CompleteAsync("the system prompt", TestTurns));
        failure.Message.Should().Contain("without message content");
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
        var provider = CreateProvider(server.BaseUrl.OriginalString, TestModel);
        using var budget = new CancellationTokenSource(RequestBudget);

        // Act / Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.CompleteAsync("the system prompt", TestTurns, budget.Token));
    }

    #endregion
}
