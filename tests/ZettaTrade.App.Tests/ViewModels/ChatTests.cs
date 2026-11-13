namespace ZettaTrade.App.Tests.ViewModels;

using System.Reactive.Linq;
using System.Reactive.Subjects;
using Livery.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ZettaTrade.AI.Chat;
using ZettaTrade.App;
using ZettaTrade.App.Services;
using ZettaTrade.App.ViewModels;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// The shared chat test fixture: the collaborators a ChatViewModel and its
/// workspace services are built from, with an idle context bound to a test
/// exchange — the symbol stream never fires and the ticker set up here never
/// ticks, so the snapshot stays "test exchange, no symbol" until a test
/// drives parts itself. Tests fetch substitutes once here instead of wiring
/// their own copies.
/// </summary>
internal static class ChatTests
{
    #region Constants

    /// <summary>The idle context's exchange display name.</summary>
    internal const string IdleExchangeName = "Test Exchange";

    /// <summary>The standing time frame the idle context parks on.</summary>
    internal const TimeFrame IdleTimeFrame = TimeFrame.H1;

    #endregion

    #region Fixtures

    /// <summary>A completion substitute the test programs per case.</summary>
    internal static IChatCompletion Completion() =>
        Substitute.For<IChatCompletion>();

    /// <summary>
    /// A localizer that echoes single-argument keys back, so role labels and
    /// format keys are visible assertions instead of hidden plumbing. Format
    /// calls keep the fallback (unconfigured) behavior unless the stub sets
    /// its own exact answer.
    /// </summary>
    internal static ILocalizationManager Localization()
    {
        var localization = Substitute.For<ILocalizationManager>();
        localization
            .GetString(Arg.Any<string>())
            .Returns(call => call.ArgAt<string>(0));

        return localization;
    }

    /// <summary>
    /// A settings substitute for the visibility conversation: the persisted
    /// read answers <paramref name="persistedVisibility"/> and the changes
    /// stream is an open subject the test may push into.
    /// </summary>
    internal static (ISettingsService Settings, Subject<SettingsChange> Changes) VisibilitySettings(
        bool persistedVisibility)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetAsync<bool>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns(persistedVisibility);
        var changes = new Subject<SettingsChange>();
        settings.Changes.Returns(changes);

        return (settings, changes);
    }

    /// <summary>
    /// An idle chat context: subscribed to dead streams, so its snapshot
    /// always reads "<see cref="IdleExchangeName"/>, no symbol" — every
    /// workspace part is absent until a test drives the streams.
    /// </summary>
    internal static ChatContextService IdleContext()
    {
        var marketData = Substitute.For<IMarketDataAdapter>();
        marketData.Name.Returns(IdleExchangeName);
        marketData.SubscribeTicker(Arg.Any<Symbol>())
            .Returns(Observable.Empty<Ticker>());

        return new ChatContextService(
            marketData,
            Observable.Empty<Symbol>(),
            Observable.Return(IdleTimeFrame),
            NullLogger<ChatContextService>.Instance);
    }

    /// <summary>
    /// A fully wired chat: anonymous collaborators, an idle workspace, and
    /// every substitute exposed for programming by the test. The streaming
    /// provider rides in only when a test asks for it — the default kit is
    /// the pre-T-205 one-shot shape.
    /// </summary>
    internal static ChatKit CreateKit(
        bool persistedVisibility = UiSettingsKeys.DefaultShowAiPanel,
        IStreamingChatCompletion? streaming = null)
    {
        var (settings, changes) = VisibilitySettings(persistedVisibility);
        var completion = Completion();
        var localization = Localization();

        var chat = new ChatViewModel(
            settings,
            completion,
            IdleContext(),
            new WorkspacePromptComposer(),
            localization,
            NullLogger<ChatViewModel>.Instance,
            streaming: streaming);

        return new ChatKit(
            chat,
            settings,
            changes,
            completion,
            localization,
            streaming);
    }

    /// <summary>
    /// An anonymous chat ViewModel on the persisted visibility — the shape
    /// the menu tests (the mirror and the restore) need.
    /// </summary>
    internal static ChatViewModel CreateAnonymousChat(
        bool persistedVisibility = UiSettingsKeys.DefaultShowAiPanel) =>
        CreateKit(persistedVisibility).Chat;

    #endregion
}

/// <summary>
/// One wired chat with every substitute that drives it, so a test can assert
/// against the pieces without wiring them again. The streaming provider is
/// null unless the test passed one.
/// </summary>
internal sealed record ChatKit(
    ChatViewModel Chat,
    ISettingsService Settings,
    Subject<SettingsChange> Changes,
    IChatCompletion Completion,
    ILocalizationManager Localization,
    IStreamingChatCompletion? Streaming = null)
{
    /// <summary>
    /// The kit rides the streamed path only when the settings say so: this
    /// stub pins the AI section's streaming key independent of the visibility
    /// stub's any-key answer (the later setup wins for the matching call).
    /// </summary>
    internal void StreamPerSettings(bool enabled)
    {
        Settings
            .GetAsync<bool>(AiSettingsKeys.Section, AiSettingsKeys.Streaming, Arg.Any<bool>())
            .Returns(enabled);
    }
}

