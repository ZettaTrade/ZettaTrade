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
        var menu = new MenuViewModel(CreateChat(), HudTests.CreateAnonymousHud(), HotkeyKits.CreateAnonymousHotkeys());
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
        var menu = new MenuViewModel(CreateChat(), HudTests.CreateAnonymousHud(), HotkeyKits.CreateAnonymousHotkeys());
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
        var menu = new MenuViewModel(CreateChat(), HudTests.CreateAnonymousHud(), HotkeyKits.CreateAnonymousHotkeys());
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

    /// <summary>
    /// The Help menu's log-viewer item routes through its interaction the
    /// same way every dialog command does — the View hosts the window.
    /// </summary>
    [Fact]
    public async Task ShowLogViewerCommand_Executed_RoutesThroughShowLogViewerInteraction()
    {
        // Arrange
        var menu = new MenuViewModel(CreateChat(), HudTests.CreateAnonymousHud(), HotkeyKits.CreateAnonymousHotkeys());
        var handled = 0;
        menu.ShowLogViewerRequested.RegisterHandler(context =>
        {
            Interlocked.Increment(ref handled);
            context.SetOutput(RxVoid.Default);
            return Task.CompletedTask;
        });

        // Act
        await menu.ShowLogViewerCommand.Execute(RxVoid.Default);

        // Assert
        handled.Should().Be(1);
    }

    /// <summary>
    /// The Help menu's license-activation item (T-132) routes through its
    /// interaction the same way every dialog command does — the View hosts
    /// the dialog window.
    /// </summary>
    [Fact]
    public async Task ShowLicenseActivationCommand_Executed_RoutesThroughShowLicenseActivationInteraction()
    {
        // Arrange
        var menu = new MenuViewModel(CreateChat(), HudTests.CreateAnonymousHud(), HotkeyKits.CreateAnonymousHotkeys());
        var handled = 0;
        menu.ShowLicenseActivationRequested.RegisterHandler(context =>
        {
            Interlocked.Increment(ref handled);
            context.SetOutput(RxVoid.Default);
            return Task.CompletedTask;
        });

        // Act
        await menu.ShowLicenseActivationCommand.Execute(RxVoid.Default);

        // Assert
        handled.Should().Be(1);
    }

    [Fact]
    public async Task ShowOrderHistoryCommand_Executed_RoutesThroughShowOrderHistoryInteraction()
    {
        // Arrange
        var menu = new MenuViewModel(CreateChat(), HudTests.CreateAnonymousHud(), HotkeyKits.CreateAnonymousHotkeys());
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

    [Fact]
    public async Task ShowBalancesCommand_Executed_RoutesThroughShowBalancesInteraction()
    {
        // Arrange
        var menu = new MenuViewModel(CreateChat(), HudTests.CreateAnonymousHud(), HotkeyKits.CreateAnonymousHotkeys());
        var handled = 0;
        menu.ShowBalancesRequested.RegisterHandler(context =>
        {
            Interlocked.Increment(ref handled);
            context.SetOutput(RxVoid.Default);
            return Task.CompletedTask;
        });

        // Act
        await menu.ShowBalancesCommand.Execute(RxVoid.Default);

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
        var menu = new MenuViewModel(CreateChat(), HudTests.CreateAnonymousHud(), HotkeyKits.CreateAnonymousHotkeys());
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
        var menu = new MenuViewModel(chat, HudTests.CreateAnonymousHud(), HotkeyKits.CreateAnonymousHotkeys());

        menu.IsAiChatVisible.Should().BeTrue("the restore has not applied yet");

        await chat.InitializeAsync();

        menu.IsAiChatVisible.Should().BeFalse("the mirror follows the restored persisted state");
    }

    /// <summary>
    /// The performance HUD's menu item (T-108): the toggle command is the
    /// HUD ViewModel's own — a press flips the persisted overlay visibility
    /// and the checkable mirror follows.
    /// </summary>
    [Fact]
    public async Task ToggleHudCommand_Executed_FlipsTheOverlayVisibility_AndTheMirrorFollows()
    {
        var hud = HudTests.CreateAnonymousHud();
        var menu = new MenuViewModel(CreateChat(), hud, HotkeyKits.CreateAnonymousHotkeys());

        menu.IsHudVisible.Should().BeFalse("the HUD ships off");

        await menu.ToggleHudCommand.Execute(RxVoid.Default);

        menu.IsHudVisible.Should().BeTrue("the mirror follows the flipped state");
        hud.IsVisible.Should().BeTrue("the command toggles the HUD ViewModel's own visibility");
    }

    private static ChatViewModel CreateChat() =>
        ChatTests.CreateAnonymousChat();

    private static (MenuViewModel Menu, ChatViewModel Chat) CreateMenuWithChat()
    {
        var chat = CreateChat();

        return (new MenuViewModel(chat, HudTests.CreateAnonymousHud(), HotkeyKits.CreateAnonymousHotkeys()), chat);
    }

    /// <summary>
    /// The hotkey cheat-sheet menu item (T-102): the toggle command is the
    /// shared hotkey manager's own — a press flips the sheet's visibility,
    /// and the menu hands the injected manager through, so the advertised
    /// gestures and the overlay ride one seam.
    /// </summary>
    [Fact]
    public async Task ToggleCheatSheetCommand_Executed_FlipsTheSheetVisibility()
    {
        var hotkeys = HotkeyKits.CreateAnonymousHotkeys();
        var menu = new MenuViewModel(CreateChat(), HudTests.CreateAnonymousHud(), hotkeys);

        menu.Hotkeys.Should().BeSameAs(hotkeys, "the menu routes the manager the composition injected");

        await menu.Hotkeys.ToggleSheetCommand.Execute(RxVoid.Default);

        menu.Hotkeys.IsSheetVisible.Should().BeTrue("the Help menu's '?' opened the cheat-sheet overlay");
    }
}
