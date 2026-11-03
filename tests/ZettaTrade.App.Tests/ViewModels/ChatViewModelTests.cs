namespace ZettaTrade.App.Tests.ViewModels;

using System.Reactive.Subjects;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using ZettaTrade.App;
using ZettaTrade.App.ViewModels;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// Chat panel visibility as persisted state: the startup walk restores
/// <c>UI.ShowAiPanel</c> (default visible) before any window opens, every
/// user toggle persists immediately, the restore and the changes-stream echo
/// never rewrite the value they just applied (the real-difference cache), and
/// external writers apply live.
/// </summary>
public sealed class ChatViewModelTests
{
    /// <summary>
    /// No persisted choice (the third-party default read) keeps the panel the
    /// layout every session had before the toggle existed.
    /// </summary>
    [Fact]
    public async Task InitializeAsync_NoPersistedChoice_KeepsThePanelVisible()
    {
        var chat = CreateChat(out _, UiSettingsKeys.DefaultShowAiPanel);

        await chat.InitializeAsync();

        chat.IsAiChatVisible.Should().BeTrue();
    }

    /// <summary>
    /// The persisted hidden panel applies at the walk, and the walk never
    /// writes the value it just read back.
    /// </summary>
    [Fact]
    public async Task InitializeAsync_RestoresThePersistedHiddenPanel_WithoutRewritingIt()
    {
        var chat = CreateChat(out var settings, false);

        await chat.InitializeAsync();

        chat.IsAiChatVisible.Should().BeFalse();
        await settings.DidNotReceiveWithAnyArgs().SetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>());
    }

    /// <summary>
    /// A user toggle on the default layout hides the panel and persists the
    /// flip; the toggle reports the visibility AFTER the flip so the menu
    /// asks the View to focus the input exactly when the panel shows.
    /// </summary>
    [Fact]
    public async Task Toggle_HidesTheVisiblePanel_PersistsAndReportsTheFlip()
    {
        var chat = CreateChat(out var settings, UiSettingsKeys.DefaultShowAiPanel);
        await chat.InitializeAsync();

        var visible = await chat.ToggleAsync();

        visible.Should().BeFalse("the default layout is visible, so the first toggle hides");
        chat.IsAiChatVisible.Should().BeFalse();
        await settings.Received().SetAsync(
            UiSettingsKeys.Section,
            UiSettingsKeys.ShowAiPanel,
            false);
    }

    /// <summary>
    /// A toggle on the persisted-hidden panel shows it again and persists the
    /// flip the same way; the second toggle back is a new difference and is
    /// persisted too (both states round-trip).
    /// </summary>
    [Fact]
    public async Task Toggle_AHiddenPanel_ShowsItAgainAndPersists()
    {
        var chat = CreateChat(out var settings, false);
        await chat.InitializeAsync();

        var visible = await chat.ToggleAsync();

        visible.Should().BeTrue();
        chat.IsAiChatVisible.Should().BeTrue();
        await settings.Received().SetAsync(
            UiSettingsKeys.Section,
            UiSettingsKeys.ShowAiPanel,
            true);

        await chat.ToggleAsync();

        await settings.Received().SetAsync(
            UiSettingsKeys.Section,
            UiSettingsKeys.ShowAiPanel,
            false);
    }

    /// <summary>
    /// An external writer re-applies live without a restart: matching keys
    /// apply, unrelated keys and non-bool values are dropped. The applied
    /// value never echoes back through the persistence pipeline — the cache
    /// led the property change, so the applied change saves nothing.
    /// </summary>
    [Fact]
    public async Task TheSettingsServiceChange_AppliesVisibilityLive_WithoutEchoingIt()
    {
        var (chat, changes, _) = CreateChatWithChanges(UiSettingsKeys.DefaultShowAiPanel);

        await chat.InitializeAsync();

        changes.OnNext(new SettingsChange(
            UiSettingsKeys.Section,
            UiSettingsKeys.ChartTimeFrame,
            "H1",
            "H4"));
        changes.OnNext(new SettingsChange(
            UiSettingsKeys.Section,
            UiSettingsKeys.ShowAiPanel,
            true,
            "true"));

        chat.IsAiChatVisible.Should().BeTrue("an unrelated key and a non-bool value both leave the panel alone");
    }

    /// <summary>
    /// The applied external change is silent on the settings service (no
    /// echo rewrite) and lands on the property; a user toggle AFTER the
    /// external application still persists its (now different) state.
    /// </summary>
    [Fact]
    public async Task TheSettingsServiceChange_EchoIsSilent_TogglesStillPersist()
    {
        var (chat, changes, settings) = CreateChatWithChanges(UiSettingsKeys.DefaultShowAiPanel);

        // Arrange: the default layout is visible, the walk restored it
        await chat.InitializeAsync();

        // Act: an external writer (hot-reload, another session) hides the panel
        changes.OnNext(new SettingsChange(
            UiSettingsKeys.Section,
            UiSettingsKeys.ShowAiPanel,
            true,
            false));

        // Assert: the flip applied live and the applied value stayed silent
        chat.IsAiChatVisible.Should().BeFalse("the external change applies without a restart");
        await settings.DidNotReceive().SetAsync(
            UiSettingsKeys.Section,
            UiSettingsKeys.ShowAiPanel,
            false);

        // Act: the user shows the panel again — a real difference against the
        // externally applied state, so it persists this time
        await chat.ToggleAsync();

        chat.IsAiChatVisible.Should().BeTrue();
        await settings.Received().SetAsync(
            UiSettingsKeys.Section,
            UiSettingsKeys.ShowAiPanel,
            true);
    }

    /// <summary>Create a chat whose persisted read returns <paramref name="persistedValue"/>.</summary>
    private static ChatViewModel CreateChat(
        out ISettingsService settings,
        bool persistedValue)
    {
        var (chat, _, substitute) = CreateChatWithChanges(persistedValue);

        settings = substitute;

        return chat;
    }

    private static (ChatViewModel Chat, Subject<SettingsChange> Changes, ISettingsService Settings)
        CreateChatWithChanges(bool persistedValue)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetAsync<bool>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns(persistedValue);
        var changes = new Subject<SettingsChange>();
        settings.Changes.Returns(changes);

        return (new ChatViewModel(settings, NullLogger<ChatViewModel>.Instance), changes, settings);
    }
}
