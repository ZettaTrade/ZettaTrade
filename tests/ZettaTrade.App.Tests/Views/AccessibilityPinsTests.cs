namespace ZettaTrade.App.Tests.Views;

using System.Globalization;
using System.IO;
using FluentAssertions;
using Xunit;
using ZettaTrade.UI.Resources;

/// <summary>
/// The accessibility source pins (T-110): the views have no headless render
/// harness, so the wiring rides the source-of-truth files exactly like the
/// chrome pins. Together with the live peer facts (the widget peers) this
/// certifies the acceptance — every advertised control is reachable and
/// named without a mouse: the palette's picker and the chat's input are
/// named, the equity curves are named per window, and every accessibility
/// string resolves in BOTH cultures. The shared-themes facts (the title-bar
/// strip buttons' automation names, the focus indicators) moved home to the
/// Livery repo's own suite with the split (T-130).
/// </summary>
public sealed class AccessibilityPinsTests
{
    private static readonly string RepoRoot = FindRepositoryRoot();

    private static string MainWindowXaml =>
        File.ReadAllText(Path.Combine(RepoRoot, "src", "ZettaTrade.App", "Views", "MainWindow.axaml"));

    private static string AssistantPanelXaml =>
        File.ReadAllText(Path.Combine(RepoRoot, "src", "ZettaTrade.App", "Views", "Panels", "AssistantPanelView.axaml"));

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
