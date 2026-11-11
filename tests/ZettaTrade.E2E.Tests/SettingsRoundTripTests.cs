namespace ZettaTrade.E2E.Tests;

using System.IO;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FluentAssertions;
using Xunit;

/// <summary>
/// The settings round-trip over the REAL process (T-176): a flip in the
/// settings dialog, applied through the explicit Save route, survives
/// into the persisted store - asserted on the settings file itself (the
/// store is the round-trip's contract; the sandbox is disposable, so the
/// flip needs no restore).
/// </summary>
[Collection("E2E")]
public sealed class SettingsRoundTripTests
{
    private readonly E2EAppFixture _app;

    public SettingsRoundTripTests(E2EAppFixture app) => _app = app;

    [WindowsFact]
    public void TheSettingsDialog_SavePersistsTheFlippedChoice()
    {
        var window = _app.MainWindow!;
        _app.BringToFront(window);

        Keyboard.TypeSimultaneously(VirtualKeyShort.LCONTROL, VirtualKeyShort.OEM_COMMA);
        var settings = _app.FindTopLevelWindowById("SettingsWindow");
        settings.Should().NotBeNull("the hotkey opens the settings dialog");

        var combo = _app.FindDescendantById(settings!, "TimeFormatComboBox");
        combo.Should().NotBeNull("the chart clock format row renders");
        combo!.Patterns.ExpandCollapse.Pattern.Expand();

        // The open combo's rows render in their own popup surface; the
        // row's AutomationId finds it across every open one, and the click
        // is the user's pick (it commits the choice and closes the popup).
        var hours12 = _app.FindElementEverywhere("TimeFormatHours12Item");
        hours12.Should().NotBeNull("the 12-hour row renders in the open combo");
        hours12!.Click();

        var save = _app.FindDescendantById(settings!, "SaveButton");
        save.Should().NotBeNull("the save route is the dialog's only persistence");
        save!.Patterns.Invoke.Pattern.Invoke();
        _app.WaitForTopLevelWindowGone("SettingsWindow")
            .Should().BeTrue("the save applies and leaves the dialog");

        var settingsJson = File.ReadAllText(Path.Combine(_app.SandboxDir, "config", "appsettings.json"));
        settingsJson.Should().Contain(
            "\"ChartUse12HourClock\": true",
            "the flipped clock format persisted into the settings store");
    }
}
