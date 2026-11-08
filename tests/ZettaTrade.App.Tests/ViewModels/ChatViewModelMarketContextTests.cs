namespace ZettaTrade.App.Tests.ViewModels;

using System.Collections.Generic;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Livery.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using ZettaTrade.AI.Chat;
using ZettaTrade.App;
using ZettaTrade.App.Services;
using ZettaTrade.App.ViewModels;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;
using RxVoid = ReactiveUI.Primitives.RxVoid;

/// <summary>
/// The market-context consent: off until restored or toggled (the silent
/// crowd's chat keeps the exact pre-consent wording), informed sends carry
/// the token-bounded snapshot per message, the flip persists exactly once
/// per real difference, and external writers re-apply live. The composer
/// absence degrades the consent path to no preamble instead of crashing a
/// send.
/// </summary>
public sealed class ChatViewModelMarketContextTests
{
    #region Constants

    /// <summary>The input line every staged send rides.</summary>
    private const string StagedQuestion = "What is the market doing?";

    /// <summary>The staged answer the completion returns.</summary>
    private const string StagedAnswer = "answering";

    #endregion

    #region Fixtures

    /// <summary>The market-context rig: the substitutes a consent test
    /// programs, held beside the chat they drive.</summary>
    private sealed record ContextKit(
        ChatViewModel Chat,
        ISettingsService Settings,
        Subject<SettingsChange> Changes,
        IChatCompletion Completion,
        ChatContextService Context,
        WorkspacePromptComposer Composer,
        List<string> Prompts);

    /// <summary>
    /// A chat on a settings substitute (GetAsync answers false unless the
    /// test stages a persisted consent), with the completion recording every
    /// system prompt it receives.
    /// </summary>
    private static ContextKit CreateKit(
        ChatContextService context,
        bool withComposer = true,
        bool persistedConsent = false)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetAsync<bool>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns(persistedConsent);
        var changes = new Subject<SettingsChange>();
        settings.Changes.Returns(changes);

        var completion = Substitute.For<IChatCompletion>();
        var prompts = new List<string>();
        completion
            .CompleteAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<ChatTurn>>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                prompts.Add(call.ArgAt<string>(0));

                return Task.FromResult(StagedAnswer);
            });

        var composer = new WorkspacePromptComposer();
        var chat = new ChatViewModel(
            settings,
            completion,
            context,
            composer,
            Substitute.For<ILocalizationManager>(),
            NullLogger<ChatViewModel>.Instance,
            withComposer ? new MarketContextPreambleComposer() : null);

        return new ContextKit(chat, settings, changes, completion, context, composer, prompts);
    }

    /// <summary>A context bound to a paper venue; the market-data face is a
    /// stub, only the venue tag is read through it.</summary>
    private static ChatContextService ContextWithPaperVenue()
    {
        var marketData = Substitute.For<IMarketDataAdapter>();
        marketData.Name.Returns("Test Exchange");
        marketData.SubscribeTicker(Arg.Any<Symbol>())
            .Returns(Observable.Empty<Ticker>());

        var venue = Substitute.For<ITradingAdapter>();
        venue.Id.Returns("paper");

        return new ChatContextService(
            marketData,
            Observable.Empty<Symbol>(),
            Observable.Return(TimeFrame.H1),
            NullLogger<ChatContextService>.Instance,
            venue);
    }

    /// <summary>A context with no optional sources (the degraded rig).</summary>
    private static ChatContextService ContextWithoutFacts()
    {
        var marketData = Substitute.For<IMarketDataAdapter>();
        marketData.Name.Returns("Test Exchange");
        marketData.SubscribeTicker(Arg.Any<Symbol>())
            .Returns(Observable.Empty<Ticker>());

        return new ChatContextService(
            marketData,
            Observable.Empty<Symbol>(),
            Observable.Return(TimeFrame.H1),
            NullLogger<ChatContextService>.Instance);
    }

    #endregion

    #region Consent Matrix

    /// <summary>The OFF row: an un-consented send carries the exact
    /// pre-consent wording — the composer never rides it.</summary>
    [Fact]
    public async Task SendTextCommand_UnConsented_CarriesThePreConsentWording()
    {
        var kit = CreateKit(ContextWithoutFacts());
        await kit.Chat.InitializeAsync();

        kit.Chat.IsMarketContextEnabled.Should().BeFalse("the consent is off until stored or toggled");
        kit.Chat.AiChatInput = StagedQuestion;
        await kit.Chat.SendTextCommand.Execute(RxVoid.Default);

        kit.Prompts.Should().ContainSingle()
            .Which.Should().Be(kit.Composer.Compose(kit.Context.Current));
    }

    /// <summary>The ON row: an informed send carries the snapshot as the very
    /// head of the prompt — the venue fact rides before the identity, so
    /// everything the model reads stands against the right venue.</summary>
    [Fact]
    public async Task SendTextCommand_Consented_CarriesTheSnapshotFirst()
    {
        var kit = CreateKit(ContextWithPaperVenue());
        await kit.Chat.InitializeAsync();

        await kit.Chat.ToggleContextCommand.Execute(RxVoid.Default);
        kit.Chat.AiChatInput = StagedQuestion;
        await kit.Chat.SendTextCommand.Execute(RxVoid.Default);

        kit.Prompts.Should().ContainSingle()
            .Which.Should().StartWith("Market snapshot: trading on paper");
    }

    /// <summary>Injected per message: the flip between two sends switches the
    /// next send exactly and only — informed rides the snapshot, un-consented
    /// returns to the silent wording.</summary>
    [Fact]
    public async Task SendTextCommand_TheFlipBetweenSends_SwitchesTheWordingForThatSendOnly()
    {
        var kit = CreateKit(ContextWithPaperVenue());
        await kit.Chat.InitializeAsync();

        await kit.Chat.ToggleContextCommand.Execute(RxVoid.Default);
        kit.Chat.AiChatInput = StagedQuestion;
        await kit.Chat.SendTextCommand.Execute(RxVoid.Default);

        await kit.Chat.ToggleContextCommand.Execute(RxVoid.Default);
        kit.Chat.AiChatInput = StagedQuestion;
        await kit.Chat.SendTextCommand.Execute(RxVoid.Default);

        kit.Prompts.Should().HaveCount(2);
        kit.Prompts[0].Should().StartWith("Market snapshot:", "the informed send rides the snapshot");
        kit.Prompts[1].Should().NotStartWith("Market snapshot:", "the next un-consented send returns to the silent wording");
    }

    /// <summary>The degraded consent path: a chat without the composer (the
    /// null DI seam) never crashes a send — the wording stays silent.</summary>
    [Fact]
    public async Task SendTextCommand_WithoutTheComposer_TheConsentDegradesToSilence()
    {
        var kit = CreateKit(ContextWithPaperVenue(), withComposer: false);
        await kit.Chat.InitializeAsync();

        await kit.Chat.ToggleContextCommand.Execute(RxVoid.Default);
        kit.Chat.AiChatInput = StagedQuestion;
        await kit.Chat.SendTextCommand.Execute(RxVoid.Default);

        kit.Prompts.Should().ContainSingle()
            .Which.Should().NotStartWith("Market snapshot:");
    }

    #endregion

    #region Persistence

    /// <summary>The flip persists once: the real-difference discipline on
    /// its own settings key.</summary>
    [Fact]
    public async Task ToggleContextCommand_TheRealDifference_PersistsOnce()
    {
        var kit = CreateKit(ContextWithoutFacts());
        await kit.Chat.InitializeAsync();

        await kit.Chat.ToggleContextCommand.Execute(RxVoid.Default);
        await kit.Settings.Received(1).SetAsync(
            UiSettingsKeys.Section,
            UiSettingsKeys.ChatMarketContext,
            true);
    }

    /// <summary>The startup walk restores the stored consent (not a default)
    /// and never rewrites the value it just read.</summary>
    [Fact]
    public async Task InitializeAsync_TheStoredConsent_IsRestored()
    {
        var kit = CreateKit(ContextWithoutFacts(), persistedConsent: true);
        await kit.Chat.InitializeAsync();

        kit.Chat.IsMarketContextEnabled.Should().BeTrue("the stored consent wins over the default");
        await kit.Settings.DidNotReceive().SetAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<bool>());
    }

    /// <summary>External writers re-apply live and stay silent in the write
    /// face (the applied value is no real difference).</summary>
    [Fact]
    public async Task Changes_TheExternalConsentWriter_AppliesLiveAndStaysSilent()
    {
        var kit = CreateKit(ContextWithoutFacts());
        await kit.Chat.InitializeAsync();

        kit.Changes.OnNext(new SettingsChange(
            UiSettingsKeys.Section,
            UiSettingsKeys.ChatMarketContext,
            false,
            true));

        kit.Chat.IsMarketContextEnabled.Should().BeTrue("the external change applies without a restart");
        await kit.Settings.DidNotReceive().SetAsync(
            UiSettingsKeys.Section,
            UiSettingsKeys.ChatMarketContext,
            true);
    }

    #endregion
}
