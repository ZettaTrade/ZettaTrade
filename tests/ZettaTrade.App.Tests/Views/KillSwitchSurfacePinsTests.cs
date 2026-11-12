namespace ZettaTrade.App.Tests.Views;

using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia.Input;
using FluentAssertions;
using Xunit;
using ZettaTrade.App.Services;

/// <summary>
/// The kill switch's surface pins (T-188): the panic action rides the
/// hotkey manager (the registry carries the action, the menu prints its
/// advertised gesture from the same live map), it leads the trading menu
/// behind one confirmation, the window's code-behind runs the sweep and
/// renders the honest report, and the whole feature's localization
/// vocabulary exists in BOTH cultures (en + ru). The repo has no headless
/// render harness yet (the T-144 lane is queued), so the facts walk the
/// sources of truth — the tokens/chrome/table-grammar pins' precedent; the
/// render-level acceptance rides that queued lane honestly.
/// </summary>
public sealed class KillSwitchSurfacePinsTests
{
    private static readonly string RepoRoot = FindRepositoryRoot();

    private static string MainWindowXaml =>
        File.ReadAllText(Path.Combine(RepoRoot, "src", "ZettaTrade.App", "Views", "MainWindow.axaml"))
            .Replace("\r\n", "\n");

    private static string MainWindowCodeBehind =>
        File.ReadAllText(Path.Combine(RepoRoot, "src", "ZettaTrade.App", "Views", "MainWindow.axaml.cs"))
            .Replace("\r\n", "\n");

    private static string ReportWindowXaml =>
        File.ReadAllText(Path.Combine(RepoRoot, "src", "ZettaTrade.App", "Views", "KillSwitchReportWindow.axaml"))
            .Replace("\r\n", "\n");

    private static string EnglishResx =>
        File.ReadAllText(Path.Combine(RepoRoot, "src", "ZettaTrade.UI", "Resources", "Strings.resx"));

    private static string RussianResx =>
        File.ReadAllText(Path.Combine(RepoRoot, "src", "ZettaTrade.UI", "Resources", "Strings.ru.resx"));

    /// <summary>The localization vocabulary the T-188 surfaces speak, in both cultures.</summary>
    private static readonly string[] KillSwitchResxKeys =
    [
        "Menu.Trading.KillSwitch",
        "Hotkeys.Action.KillSwitch",
        "Trading.ConfirmKillSwitchFormat",
        "Trading.KillSwitchPlanVenueFormat",
        "Trading.KillSwitchPlanPositionFormat",
        "Trading.KillSwitchPlanNone",
        "Trading.KillSwitchReportTitle",
        "Trading.KillSwitchNothing",
        "Trading.KillSwitchVenueUnreachableFormat",
        "Trading.KillSwitchVenueDoneFormat",
        "Trading.KillSwitchClosedTailFormat",
        "Trading.KillSwitchClosedItemFormat",
        "Trading.KillSwitchFailedItemsFormat",
        "Trading.KillSwitchAbortedFormat",
    ];

    [Fact]
    public void TheRegistry_CarriesTheKillSwitchActionBehindTheDefaultChord()
    {
        // The panic action rides the hotkey manager: the registry is the one
        // seam the menu's advertised gesture, the cheat-sheet overlay and
        // the settings tab all read, so advertised and real never diverge.
        var action = HotkeyRegistry.Actions.SingleOrDefault(item => item.Id == HotkeyRegistry.KillSwitch);
        action.Should().NotBeNull("the kill switch is a registrable action, not a hardwired key route");
        action!.NameKey.Should().Be("Hotkeys.Action.KillSwitch");
        action.DefaultGesture.Should().Be(
            new KeyGesture(Key.X, KeyModifiers.Control | KeyModifiers.Shift),
            "the shipped chord is a deliberate two-modifier combo — a panic action never fires from a stray stroke");
    }

    [Fact]
    public void TheMenu_LeadsTheTradingMenuWithTheKillSwitchItem()
    {
        var source = MainWindowXaml;

        // The item speaks the registry vocabulary: the localized caption,
        // the menu route and the live map's advertised gesture.
        source.Should().Contain("{loc:Localized Menu.Trading.KillSwitch}")
            .And.Contain("Command=\"{Binding KillSwitchCommand}\"")
            .And.Contain("InputGesture=\"{Binding Hotkeys.KillSwitchGesture}\"")
            .And.Contain("AutomationProperties.AutomationId=\"TradingKillSwitchMenuItem\"");

        // Panic prominence: the kill switch is the trading menu's FIRST item,
        // followed by a separator before the everyday items.
        var tradingMenuIndex = source.IndexOf("AutomationProperties.AutomationId=\"TradingMenu\"", StringComparison.Ordinal);
        var killSwitchIndex = source.IndexOf("TradingKillSwitchMenuItem", StringComparison.Ordinal);
        var separatorIndex = source.IndexOf("<Separator />", tradingMenuIndex, StringComparison.Ordinal);
        var newOrderIndex = source.IndexOf("TradingNewOrderMenuItem", StringComparison.Ordinal);
        tradingMenuIndex.Should().BePositive("the trading menu anchor must exist");
        killSwitchIndex.Should().BeGreaterThan(tradingMenuIndex, "the kill switch lives inside the trading menu");
        separatorIndex.Should().BeGreaterThan(killSwitchIndex, "a separator follows the panic item");
        newOrderIndex.Should().BeGreaterThan(separatorIndex, "the everyday items start after the separator");
    }

    [Fact]
    public void TheCodeBehind_RunsTheSweepAndRendersTheReport()
    {
        var source = MainWindowCodeBehind;

        // The interaction registration rides the activation scope beside the
        // other menu handoffs — it dies with the window, never a leak.
        source.Should().Contain("ViewModel?.Menu.KillSwitchRequested.RegisterHandler(RunKillSwitchAsync)");

        // The handoff resolves the sweep service, runs it behind its ONE
        // confirmation, and renders the report window over the composed
        // summary; a refusal reports nothing.
        source.Should().Contain("GetRequiredService<KillSwitchService>()")
            .And.Contain("service.ExecuteAsync()")
            .And.Contain("result.IsDeclined")
            .And.Contain("new KillSwitchReportWindow")
            .And.Contain("new KillSwitchReportViewModel(result.Summary ?? string.Empty)");

        // A sweep fault resolves the interaction honestly — the window stays
        // usable, the flow never hangs.
        source.Should().Contain("Failed to run the kill switch");
    }

    [Fact]
    public void TheReportWindow_BindsTheSummaryBehindTheNamedAutomationIds()
    {
        var source = ReportWindowXaml;

        // The window and every interactive control answer by AutomationId —
        // the accessibility and UI-test contract the dialogs hold.
        foreach (var id in new[]
                 {
                     "KillSwitchReportWindow",
                     "KillSwitchReportSummary",
                     "KillSwitchReportCloseButton",
                 })
        {
            Regex.Matches(source, $"AutomationProperties\\.AutomationId=\"{id}\"")
                .Should().HaveCount(1, "the report window's {0} is reachable by its AutomationId exactly once", id);
        }

        // The summary binds the composed multi-line report; the captions
        // ride the resx: the title and the standard chrome close.
        source.Should().Contain("Text=\"{Binding Summary}\"")
            .And.Contain("{loc:Localized Trading.KillSwitchReportTitle}")
            .And.Contain("{loc:Localized Chrome.Close}");
    }

    [Fact]
    public void TheRouteAndTheAdvertisedGesture_StayWired()
    {
        var main = File.ReadAllText(Path.Combine(RepoRoot, "src", "ZettaTrade.App", "ViewModels", "MainViewModel.cs"))
            .Replace("\r\n", "\n");
        var hotkeys = File.ReadAllText(Path.Combine(RepoRoot, "src", "ZettaTrade.App", "ViewModels", "HotkeyViewModel.cs"))
            .Replace("\r\n", "\n");

        // The window's key route dispatches the registry id to the menu
        // command — an unknown id still faults, never a substitute action.
        main.Should().Contain("case HotkeyRegistry.KillSwitch:")
            .And.Contain("DispatchHotkey(Menu.KillSwitchCommand, actionId);");

        // The cheat-sheet mirror re-applies the kill switch's gesture on
        // every map change and language switch — the menu never shows a
        // stale binding.
        hotkeys.Should().Contain("map[HotkeyRegistry.KillSwitch]")
            .And.Contain("nameof(KillSwitchGesture)");
    }

    [Fact]
    public void TheKillSwitchVocabulary_ExistsInBothCultures()
    {
        foreach (var (culture, resx) in new[] { ("en", EnglishResx), ("ru", RussianResx) })
        {
            foreach (var key in KillSwitchResxKeys)
            {
                resx.Should().Contain(
                    $"<data name=\"{key}\"",
                    "the {0} resx carries the T-188 key {1} — the feature speaks both cultures", culture, key);
            }
        }
    }

    /// <summary>
    /// Walk up from the test's base directory to the folder carrying the
    /// solution marker: tests, gate runners and IDE runs all execute inside
    /// the repository, so this resolves identically for every consumer.
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
