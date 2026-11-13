namespace ZettaTrade.App.Tests.Ai;

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using ZettaTrade.AI.Chat;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// The chat provider router (T-207) against two loopback stand-ins: the
/// stored pick routes both chat surfaces per request — blank falls back to
/// the shipped Ollama default, the two known spellings (case-insensitive)
/// pick their providers, an unknown pick is the honest error naming the
/// stored value (no default substitution), and a settings flip switches the
/// routing live. Each provider answers over its own server, so a request
/// landing on the wrong provider fails the fact.
/// </summary>
public sealed class ChatProviderRouterTests
{
    #region Constants

    /// <summary>The turn every test sends (one user turn).</summary>
    private static readonly ChatTurn[] TestTurns = [new ChatTurn(ChatTurn.UserRole, "how is the market?")];

    #endregion

    #region Fixtures

    /// <summary>The Ollama NDJSON reply the Ollama stand-in answers with.</summary>
    private static string OllamaReply() =>
        "{\"model\":\"ollama-model\",\"message\":{\"content\":\"from ollama\"},\"done\":true}";

    /// <summary>The OpenAI-compatible SSE reply the OpenAI stand-in answers with.</summary>
    private static string OpenAiReply() =>
        "data: {\"choices\":[{\"delta\":{\"content\":\"from openai\"}}]}\r\n" +
        "data: [DONE]";

    /// <summary>The OpenAI-compatible one-shot reply the OpenAI stand-in answers with.</summary>
    private static string OpenAiOneShotReply() =>
        "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"from openai\"}}]}";

    /// <summary>A settings stub that answers the provider pick (and nothing else).</summary>
    private static ISettingsService StubRouterSettings(string pick)
    {
        var settings = Substitute.For<ISettingsService>();
        settings
            .GetAsync<string>(AiSettingsKeys.Section, AiSettingsKeys.Provider, Arg.Any<string>())
            .Returns(pick);
        return settings;
    }

    /// <summary>A named-client factory handing one raw HttpClient.</summary>
    private static IHttpClientFactory StubFactory()
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(new HttpClient());
        return factory;
    }

    /// <summary>A secret store stub answering nothing (the OpenAI-compatible path runs keyless).</summary>
    private static ISecretStore StubSecrets(string? apiKey)
    {
        var secrets = Substitute.For<ISecretStore>();
        secrets
            .GetAsync(AiSettingsKeys.OpenAiCompatibleApiKeySecretKey, Arg.Any<CancellationToken>())
            .Returns(apiKey);
        return secrets;
    }

    /// <summary>
    /// One wired router kit: the router's own settings pick between two real
    /// providers, each wired to its own loopback server. A request landing on
    /// the wrong provider finds no parked serve and the fact fails on the
    /// missing answer.
    /// </summary>
    private static RouterKit CreateKit(string pick) =>
        new(pick, neverDialed: false);

    /// <summary>
    /// The unknown-pick kit: both providers stand against never-dialed
    /// addresses — the honest-refusal facts must never reach the wire at
    /// all, and a broken routing cannot hang the test on a parked serve.
    /// </summary>
    private static RouterKit CreateNeverDialedKit(string pick) =>
        new(pick, neverDialed: true);

    private sealed record RouterKit : IDisposable
    {
        public LoopbackChatServer OllamaServer { get; } = LoopbackChatServer.Start();

        public LoopbackChatServer OpenAiServer { get; } = LoopbackChatServer.Start();

        public ISettingsService Settings { get; }

        public ChatProviderRouter Router { get; }

        /// <summary>Build the full kit: the router's settings pick between the two providers.</summary>
        public RouterKit(string pick, bool neverDialed)
        {
            Settings = StubRouterSettings(pick);

            var ollamaSettings = Substitute.For<ISettingsService>();
            ollamaSettings
                .GetAsync<string>(AiSettingsKeys.Section, AiSettingsKeys.OllamaUrl, Arg.Any<string>())
                .Returns(neverDialed ? "http://127.0.0.1:9/" : OllamaServer.BaseUrl.OriginalString);
            ollamaSettings
                .GetAsync<string>(AiSettingsKeys.Section, AiSettingsKeys.Model, Arg.Any<string>())
                .Returns("ollama-model");
            var ollama = new OllamaChatCompletion(
                ollamaSettings,
                StubFactory(),
                NullLogger<OllamaChatCompletion>.Instance);

            var openAiSettings = Substitute.For<ISettingsService>();
            openAiSettings
                .GetAsync<string>(
                    AiSettingsKeys.OpenAiCompatibleSection,
                    AiSettingsKeys.OpenAiCompatibleBaseUrl,
                    Arg.Any<string>())
                .Returns(neverDialed ? "http://127.0.0.1:9/" : OpenAiServer.BaseUrl.OriginalString);
            openAiSettings
                .GetAsync<string>(
                    AiSettingsKeys.OpenAiCompatibleSection,
                    AiSettingsKeys.OpenAiCompatibleModel,
                    Arg.Any<string>())
                .Returns("openai-model");
            var openAiCompatible = new OpenAiCompatibleChatCompletion(
                openAiSettings,
                StubFactory(),
                StubSecrets(null),
                NullLogger<OpenAiCompatibleChatCompletion>.Instance);

            Router = new ChatProviderRouter(
                Settings,
                ollama,
                openAiCompatible,
                NullLogger<ChatProviderRouter>.Instance);
        }

        public void Dispose()
        {
            OllamaServer.Dispose();
            OpenAiServer.Dispose();
        }
    }

    /// <summary>Collect the whole stream.</summary>
    private static async Task<List<string>> CollectAsync(IAsyncEnumerable<string> stream)
    {
        var deltas = new List<string>();
        await foreach (var delta in stream)
        {
            deltas.Add(delta);
        }

        return deltas;
    }

    #endregion

    #region Routing

    /// <summary>
    /// A blank (unset) pick routes to the shipped default: the request lands
    /// on the Ollama server (local models first), the OpenAI stand-in hears
    /// nothing.
    /// </summary>
    [Fact]
    public async Task CompleteAsync_ABlankPick_RoutesToTheDefaultOllama()
    {
        using var kit = CreateKit(pick: string.Empty);
        var ollamaServing = kit.OllamaServer.ServeAsync(200, OllamaReply());

        var reply = await kit.Router.CompleteAsync("the system prompt", TestTurns);

        reply.Should().Be("from ollama");
        (await ollamaServing).Path.Should().Be(LoopbackChatServer.ChatPath);
    }

    /// <summary>
    /// The OpenAI-compatible pick routes to the OpenAI-compatible provider:
    /// the request lands on its server.
    /// </summary>
    [Fact]
    public async Task CompleteAsync_TheOpenAiCompatiblePick_RoutesToTheOpenAiCompatibleProvider()
    {
        using var kit = CreateKit(pick: "OpenAiCompatible");
        var openAiServing = kit.OpenAiServer.ServeAsync(200, OpenAiOneShotReply());

        var reply = await kit.Router.CompleteAsync("the system prompt", TestTurns);

        reply.Should().Be("from openai");
        (await openAiServing).Path.Should().Be("/chat/completions");
    }

    /// <summary>
    /// The Ollama pick routes to Ollama (the explicit choice of the default).
    /// </summary>
    [Fact]
    public async Task CompleteAsync_TheOllamaPick_RoutesToOllama()
    {
        using var kit = CreateKit(pick: "Ollama");
        var ollamaServing = kit.OllamaServer.ServeAsync(200, OllamaReply());

        var reply = await kit.Router.CompleteAsync("the system prompt", TestTurns);

        reply.Should().Be("from ollama");
        (await ollamaServing).Path.Should().Be(LoopbackChatServer.ChatPath);
    }

    /// <summary>
    /// The pick matches case-insensitively: a hand-edited lowercase
    /// spelling routes exactly like the canonical one.
    /// </summary>
    [Fact]
    public async Task CompleteAsync_ThePickIsCaseInsensitive()
    {
        using var kit = CreateKit(pick: "openaicompatible");
        var openAiServing = kit.OpenAiServer.ServeAsync(200, OpenAiOneShotReply());

        var reply = await kit.Router.CompleteAsync("the system prompt", TestTurns);

        reply.Should().Be("from openai");
        (await openAiServing).Path.Should().Be("/chat/completions");
    }

    /// <summary>
    /// The pick is read per request: a settings flip between calls switches
    /// the routing live — no restart needed.
    /// </summary>
    [Fact]
    public async Task CompleteAsync_ThePickIsReadPerRequest_TheFlipSwitchesLive()
    {
        using var kit = CreateKit(pick: string.Empty);
        var ollamaServing = kit.OllamaServer.ServeAsync(200, OllamaReply());
        await kit.Router.CompleteAsync("the system prompt", TestTurns);
        await ollamaServing;

        kit.Settings
            .GetAsync<string>(AiSettingsKeys.Section, AiSettingsKeys.Provider, Arg.Any<string>())
            .Returns("OpenAiCompatible");
        var openAiServing = kit.OpenAiServer.ServeAsync(200, OpenAiOneShotReply());
        var reply = await kit.Router.CompleteAsync("the system prompt", TestTurns);

        reply.Should().Be("from openai");
        (await openAiServing).Path.Should().Be("/chat/completions");
    }

    #endregion

    #region The Unknown Pick

    /// <summary>
    /// An unknown pick is the honest error naming the stored value: no
    /// default substitution, no silent reroute — and nothing ever reaches
    /// the wire (both providers stand against never-dialed addresses).
    /// </summary>
    [Fact]
    public async Task CompleteAsync_AnUnknownPick_ThrowsTheHonestErrorWithoutDialing()
    {
        using var kit = CreateNeverDialedKit(pick: "Mistral");

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => kit.Router.CompleteAsync("the system prompt", TestTurns));

        failure.Message.Should().Contain("Mistral");
        failure.Message.Should().Contain("OpenAiCompatible");
    }

    /// <summary>
    /// The streaming surface keeps the same honest refusal: the first
    /// enumeration surfaces the error (nothing renders, nothing dials).
    /// </summary>
    [Fact]
    public async Task StreamAsync_AnUnknownPick_ThrowsOnTheFirstEnumeration()
    {
        using var kit = CreateNeverDialedKit(pick: "Mistral");

        var act = async () => await CollectAsync(kit.Router.StreamAsync("the system prompt", TestTurns));

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("Mistral");
    }

    #endregion

    #region The Streamed Routing

    /// <summary>
    /// The streaming surface routes like the one-shot: the OpenAI-compatible
    /// pick streams its SSE deltas.
    /// </summary>
    [Fact]
    public async Task StreamAsync_ThePickRoutesTheStream()
    {
        using var kit = CreateKit(pick: "OpenAiCompatible");
        var openAiServing = kit.OpenAiServer.ServeAsync(200, OpenAiReply());

        var deltas = await CollectAsync(kit.Router.StreamAsync("the system prompt", TestTurns));

        deltas.Should().Equal("from openai");
        (await openAiServing).Path.Should().Be("/chat/completions");
    }

    #endregion
}
