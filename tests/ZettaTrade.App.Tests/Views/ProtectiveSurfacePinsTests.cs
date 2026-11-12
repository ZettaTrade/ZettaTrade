namespace ZettaTrade.App.Tests.Views;

using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

/// <summary>
/// The protective-levels surface pins (T-187): the positions panel carries
/// the stop-loss/take-profit columns and the secondary protect action on
/// every row, the dialog binds the parsed fields behind the named
/// AutomationIds, the code-behind answers the protect interaction with the
/// dialog handoff, and the whole feature's localization vocabulary exists
/// in BOTH cultures (en + ru). The repo has no headless render harness yet
/// (the T-144 lane is queued), so the facts walk the sources of truth —
/// the tokens/chrome/table-grammar pins' precedent; the render-level
/// acceptance rides that queued lane honestly.
/// </summary>
public sealed class ProtectiveSurfacePinsTests
{
    private static readonly string RepoRoot = FindRepositoryRoot();

    private static string PositionsXaml =>
        File.ReadAllText(Path.Combine(RepoRoot, "src", "ZettaTrade.App", "Views", "PositionsPanelWindow.axaml"))
            .Replace("\r\n", "\n");

    private static string PositionsCodeBehind =>
        File.ReadAllText(Path.Combine(RepoRoot, "src", "ZettaTrade.App", "Views", "PositionsPanelWindow.axaml.cs"))
            .Replace("\r\n", "\n");

    private static string DialogXaml =>
        File.ReadAllText(Path.Combine(RepoRoot, "src", "ZettaTrade.App", "Views", "ProtectiveLevelsDialogWindow.axaml"))
            .Replace("\r\n", "\n");

    private static string EnglishResx =>
        File.ReadAllText(Path.Combine(RepoRoot, "src", "ZettaTrade.UI", "Resources", "Strings.resx"));

    private static string RussianResx =>
        File.ReadAllText(Path.Combine(RepoRoot, "src", "ZettaTrade.UI", "Resources", "Strings.ru.resx"));

    /// <summary>The localization vocabulary the T-187 surfaces speak, in both cultures.</summary>
    private static readonly string[] ProtectResxKeys =
    [
        "Positions.ColStopLoss",
        "Positions.ColTakeProfit",
        "Positions.ActionProtect",
        "Positions.ProtectTitle",
        "Positions.ProtectSummary",
        "Positions.ProtectStopLoss",
        "Positions.ProtectTakeProfit",
        "Positions.ProtectHint",
        "Positions.ProtectApply",
        "Positions.ProtectStopNotANumber",
        "Positions.ProtectTakeProfitNotANumber",
        "Positions.ProtectLevelNone",
        "Positions.ProtectNothingToClear",
        "Positions.ProtectLevelCleared",
        "Positions.ProtectStopMustBeBelowMarket",
        "Positions.ProtectStopMustBeAboveMarket",
        "Positions.ProtectTakeProfitMustBeAboveMarket",
        "Positions.ProtectTakeProfitMustBeBelowMarket",
        "Positions.ProtectSetFormat",
        "Positions.ProtectAbortedFormat",
        "Trading.ConfirmProtectFormat",
    ];

    [Fact]
    public void ThePositionsPanel_CarriesTheLevelColumnsAndTheProtectAction()
    {
        var source = PositionsXaml;

        // The positions section's table widened by two numeric columns: the
        // header and the row ride the SAME column plan, so the cells sit
        // under their headers on every language.
        Regex.Matches(source, "ColumnDefinitions=\"\\*,80,90,110,95,95,140,380\"")
            .Should().HaveCount(2, "the positions section's header row and data row share the widened column plan");

        // The headers speak the resx vocabulary.
        source.Should().Contain("{loc:Localized Positions.ColStopLoss}")
            .And.Contain("{loc:Localized Positions.ColTakeProfit}");

        // The row cells bind the F8 display strings on the numeric stack,
        // right-aligned beside their header cells.
        source.Should().Contain("Text=\"{Binding StopLoss}\"")
            .And.Contain("Text=\"{Binding TakeProfit}\"");
        Regex.Matches(source, "<TextBlock Grid\\.Column=\"[45]\" Text=\"\\{Binding (StopLoss|TakeProfit)\\}\" FontFamily=\"\\{StaticResource NumericFont\\}\" TextAlignment=\"Right\" />")
            .Should().HaveCount(2, "the SL/TP cells are the same numeric stack + right alignment the table grammar owns");

        // The protect action: a secondary row action bound to the row VM's
        // command, captioned from the resx — the reverse keeps the single
        // destructive weight (the table-grammar pins own that count).
        source.Should().Contain("Content=\"{loc:Localized Positions.ActionProtect}\"");
        Regex.Matches(source, "<Button Classes=\"row-action secondary\"\r?\n?\\s*Content=\"\\{loc:Localized Positions\\.ActionProtect\\}\"\r?\n?\\s*Command=\"\\{Binding EditProtectCommand\\}\" />")
            .Should().HaveCount(1, "one protect action per row, the quiet outlined sibling");
    }

    [Fact]
    public void TheCodeBehind_AnswersTheProtectInteractionWithTheDialogHandoff()
    {
        var source = PositionsCodeBehind;

        // The interaction registration rides the activation scope beside the
        // amend handoff — it dies with the window, never a leak.
        source.Should().Contain("ViewModel?.ProtectRequested")
            .And.Contain(".RegisterHandler(HandleProtectRequestedAsync)");

        // The handoff builds the transient dialog view model over the row's
        // position and resolves the interaction with the dialog's parsed
        // edits (null when the dialog closed without one).
        source.Should().Contain("GetRequiredService<ProtectiveLevelsDialogViewModel>()")
            .And.Contain("viewModel.Initialize(position)")
            .And.Contain("new ProtectiveLevelsDialogWindow")
            .And.Contain("return dialog.ProtectEdits;");

        // A dialog fault resolves the interaction honestly (null output) —
        // the panel stays usable, the flow never hangs.
        source.Should().Contain("Failed to open the protective-levels dialog");
    }

    [Fact]
    public void TheDialog_BindsTheParsedFieldsBehindTheNamedAutomationIds()
    {
        var source = DialogXaml;

        // The window and every interactive control answer by AutomationId —
        // the accessibility and UI-test contract the dialogs hold.
        foreach (var id in new[]
                 {
                     "ProtectiveLevelsDialogWindow",
                     "ProtectStopLossInput",
                     "ProtectTakeProfitInput",
                     "ProtectErrorLine",
                     "ProtectApplyButton",
                     "ProtectCancelButton",
                 })
        {
            Regex.Matches(source, $"AutomationProperties\\.AutomationId=\"{id}\"")
                .Should().HaveCount(1, "the dialog's {0} is reachable by its AutomationId exactly once", id);
        }

        // Both level fields ride the numeric stack; the error line binds the
        // VM's message and its visibility flag.
        Regex.Matches(source, "FontFamily=\"\\{StaticResource NumericFont\\}\"")
            .Should().HaveCount(2, "both level inputs hold their digits in the shared stack");
        source.Should().Contain("Text=\"{Binding StopLossInput, Mode=TwoWay}\"")
            .And.Contain("Text=\"{Binding TakeProfitInput, Mode=TwoWay}\"")
            .And.Contain("Text=\"{Binding ErrorMessage}\"")
            .And.Contain("IsVisible=\"{Binding HasErrorMessage}\"")
            .And.Contain("Text=\"{Binding Summary}\"");

        // The captions ride the resx: the title, both level captions, the
        // clear-semantics hint, the apply verb and the standard cancel.
        source.Should().Contain("{loc:Localized Positions.ProtectTitle}")
            .And.Contain("{loc:Localized Positions.ProtectStopLoss}")
            .And.Contain("{loc:Localized Positions.ProtectTakeProfit}")
            .And.Contain("{loc:Localized Positions.ProtectHint}")
            .And.Contain("{loc:Localized Positions.ProtectApply}")
            .And.Contain("{loc:Localized Trading.Cancel}");
    }

    [Fact]
    public void TheProtectVocabulary_ExistsInBothCultures()
    {
        foreach (var (culture, resx) in new[] { ("en", EnglishResx), ("ru", RussianResx) })
        {
            foreach (var key in ProtectResxKeys)
            {
                resx.Should().Contain(
                    $"<data name=\"{key}\"",
                    "the {0} resx carries the T-187 key {1} — the feature speaks both cultures", culture, key);
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
