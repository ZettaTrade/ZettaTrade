namespace ZettaTrade.App.Tests.Views;

using System.Globalization;
using System.IO;
using FluentAssertions;
using Xunit;
using ZettaTrade.UI.Resources;

/// <summary>
/// The accessibility source pins (T-110): the views and the shared control
/// themes have no headless render harness, so the wiring rides the
/// source-of-truth files exactly like the chrome pins. Together with the
/// live peer facts (the widget peers) this certifies the acceptance —
/// every advertised control is reachable and named without a mouse: the
/// icon-only title-bar buttons carry real automation names, the palette's
/// picker and the chat's input are named, the equity curves are named per
/// window, the focus indicators exist for the button/tab/menu families, and
/// every accessibility string resolves in BOTH cultures.
/// </summary>
public sealed class AccessibilityPinsTests
{
    private static readonly string RepoRoot = FindRepositoryRoot();

    private static string MainWindowXaml =>
        File.ReadAllText(Path.Combine(RepoRoot, "src", "ZettaTrade.App", "Views", "MainWindow.axaml"));

    private static string AssistantPanelXaml =>
        File.ReadAllText(Path.Combine(RepoRoot, "src", "ZettaTrade.App", "Views", "Panels", "AssistantPanelView.axaml"));

    private static string ControlsThemes =>
        File.ReadAllText(Path.Combine(RepoRoot, "src", "Livery", "Themes", "Controls.axaml"));

    [Fact]
    public void EveryTitleBarStripButton_CarriesItsAutomationName()
    {
        var source = ControlsThemes;

        source.Should().Contain("AutomationProperties.Name=\"{loc:Localized Chrome.Minimize}\"",
            "the minimize button is icon-only; assistive tech needs the name");
        source.Should().Contain("AutomationProperties.Name=\"{loc:Localized Chrome.Maximize}\"",
            "the maximize button is icon-only");
        source.Should().Contain("AutomationProperties.Name=\"{loc:Localized Chrome.Restore}\"",
            "the restore button is icon-only");
        source.Should().Contain("AutomationProperties.Name=\"{loc:Localized Chrome.Close}\"",
            "the close button is icon-only — its name must never rely on the tooltip");
    }

    [Fact]
    public void TheSharedThemes_CarryTheKeyboardFocusIndicators()
    {
        var source = ControlsThemes;

        foreach (var selector in new[]
                 {
                     "<Style Selector=\"Button:focus\">",
                     "<Style Selector=\"ToggleButton:focus\">",
                     "<Style Selector=\"TabItem:focus\">",
                     "<Style Selector=\"MenuItem:focus\">",
                     "<Style Selector=\"livery|TitleBar Button.strip-icon:focus\">",
                 })
        {
            source.Should().Contain(
                selector,
                "keyboard focus needs a visible marker on every focusable family");
        }

        source.Should().Contain(
            "Value=\"{DynamicResource Highlight}\"",
            "the focus markers borrow the palette's on-state accent");
    }

    [Fact]
    public void ThePalettePicker_AndTheChatInput_CarryTheirNames()
    {
        var source = MainWindowXaml;

        source.Should().Contain(
            "AutomationProperties.Name=\"{loc:Localized Chart.A11ySymbolPicker}\"",
            "the palette's query box is the picker's advertised control");

        // The chat input moved into the detachable assistant panel (T-103);
        // the name rides the panel's own view — the hotkey focus route
        // lands there whatever window hosts the panel.
        AssistantPanelXaml.Should().Contain(
            "AutomationProperties.Name=\"{loc:Localized AI.Chat.InputName}\"",
            "the chat input is named — the hotkey focus route lands there");
    }

    [Fact]
    public void BothEquityWindows_NameTheirCurve()
    {
        File.ReadAllText(Path.Combine(RepoRoot, "src", "ZettaTrade.App", "Views", "BacktestWindow.axaml"))
            .Should().Contain(
                "AutomationProperties.Name=\"{loc:Localized Backtest.A11yEquity}\"",
                "the backtest equity curve is canvas-drawn and window-named");

        File.ReadAllText(Path.Combine(RepoRoot, "src", "ZettaTrade.App", "Views", "TradeStatsWindow.axaml"))
            .Should().Contain(
                "AutomationProperties.Name=\"{loc:Localized Stats.A11yEquity}\"",
                "the trade-stats equity curve is canvas-drawn and window-named");
    }

    [Fact]
    public void TheCatalog_CarriesEveryA11yKeyInBothCultures()
    {
        foreach (var key in new[]
                 {
                     "Chart.A11ySymbolPicker", "Chart.A11yNoSeries", "Chart.A11ySeries",
                     "Chart.A11yWithCompare", "OrderBook.A11yWithSymbol", "OrderBook.A11yDepthWithSymbol",
                     "Tape.A11yWithSymbol", "Backtest.A11yEquity", "Stats.A11yEquity", "AI.Chat.InputName",
                 })
        {
            Strings.ResourceManager.GetString(key, CultureInfo.InvariantCulture)
                .Should().NotBeNullOrEmpty("'{0}' is a user-facing accessibility string", key);
            Strings.ResourceManager.GetString(key, CultureInfo.GetCultureInfo("ru-RU"))
                .Should().NotBeNullOrEmpty("'{0}' must localize into the ru catalog", key);
        }
    }

    /// <summary>
    /// Walk up from the test's base directory to the folder carrying the
    /// solution marker (the chrome pins' walk: tests, gate runners and IDE
    /// runs all execute inside the repository).
    /// </summary>
    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ZettaTrade.slnx")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("the test must run inside a ZettaTrade repository checkout");
        return directory!.FullName;
    }
}
