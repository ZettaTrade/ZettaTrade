namespace ZettaTrade.E2E.Tests;

using System;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FluentAssertions;
using Xunit;

/// <summary>
/// The hotkey round over the REAL process (T-176): the two typed menu
/// hotkeys the menu hints advertise open their dialogs, and the dialogs
/// leave by their own close routes. The dialog state asserts through the
/// window's AutomationIds, never through localized titles.
/// </summary>
[Collection("E2E")]
public sealed class HotkeyDialogFlowTests
{
    private readonly E2EAppFixture _app;

    public HotkeyDialogFlowTests(E2EAppFixture app) => _app = app;

    [WindowsFact]
    public void TheAdvertisedHotkeys_OpenAndLeaveTheirDialogs()
    {
        var window = _app.MainWindow!;
        _app.BringToFront(window);

        Keyboard.TypeSimultaneously(VirtualKeyShort.LCONTROL, VirtualKeyShort.KEY_N);
        var orderDialog = _app.FindTopLevelWindowById("NewOrderDialogWindow");
        orderDialog.Should().NotBeNull("Ctrl+N is the typed hotkey the New Order hint renders");

        _app.FindDescendantById(orderDialog!, "QuantityInput")
            .Should().NotBeNull("the ticket's quantity field renders");
        var cancel = _app.FindDescendantById(orderDialog!, "CancelButton");
        cancel.Should().NotBeNull("the cancel affordance renders");
        cancel!.Patterns.Invoke.Pattern.Invoke();
        _app.WaitForTopLevelWindowGone("NewOrderDialogWindow")
            .Should().BeTrue("the cancel route closes the ticket without placing anything");

        _app.BringToFront(window);
        Keyboard.TypeSimultaneously(VirtualKeyShort.LCONTROL, VirtualKeyShort.OEM_COMMA);
        var settings = _app.FindTopLevelWindowById("SettingsWindow");
        settings.Should().NotBeNull("Ctrl+OemComma is the typed hotkey the Settings hint renders");

        var closeButton = _app.FindDescendantById(settings!, "CloseButton");
        closeButton.Should().NotBeNull("the close affordance renders");
        closeButton!.Patterns.Invoke.Pattern.Invoke();
        _app.WaitForTopLevelWindowGone("SettingsWindow")
            .Should().BeTrue("a plain close leaves the dialog without saving");
    }
}
