namespace ZettaTrade.App.Tests.ViewModels;

using System.Reactive.Linq;
using FluentAssertions;
using ReactiveUI.Primitives;
using Xunit;
using ZettaTrade.App.ViewModels;

/// <summary>
/// Tests for the menu command routing: the dialog commands raise their
/// <see cref="MenuViewModel"/> interactions for the View to host the windows,
/// and the AI chat command TOGGLES the persisted panel visibility — the
/// focus interaction fires only when the toggle shows the panel, and the
/// checkable item mirrors the applied state live.
/// </summary>
public sealed class MenuViewModelTests
{
    [Fact]
    public async Task ShowPositionsCommand_Executed_RoutesThroughShowPositionsInteraction()
    {
        // Arrange
        var menu = new MenuViewModel(CreateChat());
        var handled = 0;
        menu.ShowPositionsRequested.RegisterHandler(context =>
        {
            Interlocked.Increment(ref handled);
            context.SetOutput(RxVoid.Default);
            return Task.CompletedTask;
        });

        // Act
        await menu.ShowPositionsCommand.Execute(RxVoid.Default);

        // Assert
        handled.Should().Be(1);
    }

    [Fact]
    public async Task OpenSettingsCommand_Executed_RoutesThroughOpenSettingsInteraction()
    {
        // Arrange
        var menu = new MenuViewModel(CreateChat());
        var handled = 0;
        menu.OpenSettingsRequested.RegisterHandler(context =>
        {
            Interlocked.Increment(ref handled);
            context.SetOutput(RxVoid.Default);
            return Task.CompletedTask;
        });

        // Act
        await menu.OpenSettingsCommand.Execute(RxVoid.Default);

        // Assert
        handled.Should().Be(1);
    }

    /// <summary>
    /// The Help menu's disclaimer item routes through its interaction the
    /// same way every dialog command does — the View hosts the window.
    /// </summary>
    [Fact]
    public async Task ShowDisclaimerCommand_Executed_RoutesThroughShowDisclaimerInteraction()
    {
        // Arrange
        var menu = new MenuViewModel(CreateChat());
        var handled = 0;
        menu.ShowDisclaimerRequested.RegisterHandler(context =>
        {
            Interlocked.Increment(ref handled);
            context.SetOutput(RxVoid.Default);
            return Task.CompletedTask;
        });

        // Act
        await menu.ShowDisclaimerCommand.Execute(RxVoid.Default);

        // Assert
        handled.Should().Be(1);
    }

    [Fact]
    public async Task ShowOrderHistoryCommand_Executed_RoutesThroughShowOrderHistoryInteraction()
    {
        // Arrange
        var menu = new MenuViewModel(CreateChat());
        var handled = 0;
        menu.ShowOrderHistoryRequested.RegisterHandler(context =>
        {
            Interlocked.Increment(ref handled);
            context.SetOutput(RxVoid.Default);
            return Task.CompletedTask;
        });

        // Act
        await menu.ShowOrderHistoryCommand.Execute(RxVoid.Default);

        // Assert
        handled.Should().Be(1);
    }

    /// <summary>
    /// The chat toggle hides a visible panel without any focus request and
    /// shows a hidden one WITH one (the input is reachable only now).
    /// </summary>
    [Fact]
    public async Task ToggleAiChatCommand_Executed_AsksFocusOnlyWhenThePanelShows()
    {
        // Arrange
        var menu = new MenuViewModel(CreateChat());
        var handled = 0;
        menu.FocusAiChatRequested.RegisterHandler(context =>
        {
            Interlocked.Increment(ref handled);
            context.SetOutput(RxVoid.Default);
            return Task.CompletedTask;
        });

        // Act: the default state is visible — the first toggle hides
        await menu.ToggleAiChatCommand.Execute(RxVoid.Default);

        // Assert
        handled.Should().Be(0);
        menu.IsAiChatVisible.Should().BeFalse("the first toggle hides the default-visible panel");

        // Act: the second toggle shows it again
        await menu.ToggleAiChatCommand.Execute(RxVoid.Default);

        // Assert
        handled.Should().Be(1);
        menu.IsAiChatVisible.Should().BeTrue();
    }

    /// <summary>
    /// The checkable item is a live mirror: the subscription emits the
    /// current state as it lands and follows every flip of the chat ViewModel.
    /// </summary>
    [Fact]
    public async Task TheChatPanelMirror_FollowsEveryFlip()
    {
        var (menu, chat) = CreateMenuWithChat();

        menu.IsAiChatVisible.Should().BeTrue("the subscription emits the current state when it lands");

        await chat.ToggleAsync();

        menu.IsAiChatVisible.Should().BeFalse("the mirror follows the chat ViewModel, never writes back");
    }

    /// <summary>
    /// The menu may already be constructed when the startup walk restores the
    /// persisted state — the mirror lands on the restored visibility, not on
    /// the constructor moment.
    /// </summary>
    [Fact]
    public async Task TheStartupRestoreArrivesAfterTheMenuLanded_MirrorsTheRestoredState()
    {
        var chat = ChatTests.CreateAnonymousChat(false);
        var menu = new MenuViewModel(chat);

        menu.IsAiChatVisible.Should().BeTrue("the restore has not applied yet");

        await chat.InitializeAsync();

        menu.IsAiChatVisible.Should().BeFalse("the mirror follows the restored persisted state");
    }

    private static ChatViewModel CreateChat() =>
        ChatTests.CreateAnonymousChat();

    private static (MenuViewModel Menu, ChatViewModel Chat) CreateMenuWithChat()
    {
        var chat = CreateChat();

        return (new MenuViewModel(chat), chat);
    }
}
