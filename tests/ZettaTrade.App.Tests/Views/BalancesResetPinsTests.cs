namespace ZettaTrade.App.Tests.Views;

using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;
using ZettaTrade.App.Services;

/// <summary>
/// The paper reset's surface pins (T-192): the balances panel's footer
/// carries the reset route (the command bound, the AutomationId reachable
/// exactly once), the code-behind answers the dialog handoff through the
/// transient dialog view model, the dialog window's every interactive
/// control answers by AutomationId, and the whole feature's localization
/// vocabulary exists in BOTH cultures (en + ru). The repo has no headless
/// render harness yet (the T-144 lane is queued), so the facts walk the
/// sources of truth — the kill switch's surface-pins precedent; the
/// render-level acceptance rides that queued lane honestly.
/// </summary>
public sealed class BalancesResetPinsTests
{
    private static readonly string RepoRoot = FindRepositoryRoot();

    private static string PanelWindowXaml =>
        File.ReadAllText(Path.Combine(RepoRoot, "src", "ZettaTrade.App", "Views", "BalancesPanelWindow.axaml"))
            .Replace("\r\n", "\n");

    private static string PanelWindowCodeBehind =>
        File.ReadAllText(Path.Combine(RepoRoot, "src", "ZettaTrade.App", "Views", "BalancesPanelWindow.axaml.cs"))
            .Replace("\r\n", "\n");

    private static string DialogWindowXaml =>
        File.ReadAllText(Path.Combine(RepoRoot, "src", "ZettaTrade.App", "Views", "PaperResetDialogWindow.axaml"))
            .Replace("\r\n", "\n");

    private static string DialogViewModel =>
        File.ReadAllText(Path.Combine(RepoRoot, "src", "ZettaTrade.App", "ViewModels", "PaperResetDialogViewModel.cs"))
            .Replace("\r\n", "\n");

    private static string PanelViewModel =>
        File.ReadAllText(Path.Combine(RepoRoot, "src", "ZettaTrade.App", "ViewModels", "BalancesPanelViewModel.cs"))
            .Replace("\r\n", "\n");

    private static string EnglishResx =>
        File.ReadAllText(Path.Combine(RepoRoot, "src", "ZettaTrade.UI", "Resources", "Strings.resx"));

    private static string RussianResx =>
        File.ReadAllText(Path.Combine(RepoRoot, "src", "ZettaTrade.UI", "Resources", "Strings.ru.resx"));

    /// <summary>The localization vocabulary the T-192 surfaces speak, in both cultures.</summary>
    private static readonly string[] PaperResetResxKeys =
    [
        "Trading.PaperResetButton",
        "Trading.PaperResetTitle",
        "Trading.PaperResetSummary",
        "Trading.PaperResetBalanceLabel",
        "Trading.PaperResetNotANumber",
        "Trading.PaperResetHint",
        "Trading.PaperResetApply",
        "Trading.ConfirmPaperResetFormat",
        "Trading.PaperResetDoneFormat",
        "Trading.PaperResetFailedFormat",
    ];

    [Fact]
    public void ThePanelFooter_CarriesTheResetRoute()
    {
        var source = PanelWindowXaml;

        // The footer button rides the command binding and the localized
        // caption, reachable by its AutomationId exactly once.
        source.Should().Contain("Command=\"{Binding ResetPaperCommand}\"")
            .And.Contain("{loc:Localized Trading.PaperResetButton}");
        Regex.Matches(source, "AutomationProperties\\.AutomationId=\"ResetPaperButton\"")
            .Should().HaveCount(1, "the footer's reset button is reachable exactly once");

        // The finished reset's summary line rides the outcome binding beside
        // the error lane.
        source.Should().Contain("Text=\"{Binding OutcomeMessage}\"")
            .And.Contain("IsVisible=\"{Binding HasOutcomeMessage}\"");
    }

    [Fact]
    public void ThePanelViewModel_RunsTheFlowBehindTheDialogInteraction()
    {
        var source = PanelViewModel;

        // The flow: the interaction handoff (decimal in, decimal? out), the
        // command, the outcome lane, and the localized keys.
        source.Should().Contain("Interaction<decimal, decimal?> ResetRequested")
            .And.Contain("ResetPaperCommand")
            .And.Contain("ResetRequested.Handle(prefill)")
            .And.Contain("OutcomeMessage")
            .And.Contain("ResetFailedKey = \"Trading.PaperResetFailedFormat\"")
            .And.Contain("IPaperResetService");

        // A closed dialog and a refused confirmation leave nothing on the
        // surface (the declined reset is silent).
        source.Should().Contain("chosen is null")
            .And.Contain("!outcome.Done");
    }

    [Fact]
    public void TheCodeBehind_AnswersTheDialogHandoff()
    {
        var source = PanelWindowCodeBehind;

        // The handoff registration rides the activation scope and dies with
        // the window; the dialog opens over the panel and its parsed choice
        // resolves the interaction.
        source.Should().Contain("ViewModel?.ResetRequested")
            .And.Contain(".RegisterHandler(HandleResetRequestedAsync)")
            .And.Contain("GetRequiredService<PaperResetDialogViewModel>()")
            .And.Contain("viewModel.Initialize(prefillBalance)")
            .And.Contain("new PaperResetDialogWindow")
            .And.Contain("dialog.ChosenBalance");

        // A dialog fault resolves the interaction honestly — the panel stays
        // usable, the flow never hangs.
        source.Should().Contain("Failed to open the paper reset dialog");
    }

    [Fact]
    public void TheDialogWindow_BindsTheInputsBehindTheNamedAutomationIds()
    {
        var source = DialogWindowXaml;

        // The window and every interactive control answer by AutomationId —
        // the accessibility and UI-test contract the dialogs hold.
        foreach (var id in new[]
                 {
                     "PaperResetDialogWindow",
                     "PaperResetBalanceInput",
                     "PaperResetErrorLine",
                     "PaperResetApplyButton",
                     "PaperResetCancelButton",
                 })
        {
            Regex.Matches(source, $"AutomationProperties\\.AutomationId=\"{id}\"")
                .Should().HaveCount(1, "the reset dialog's {0} is reachable by its AutomationId exactly once", id);
        }

        // The input rides the numeric font, the captions ride the resx, the
        // primary button is the reset itself, the cancel rides the standard
        // cancel key.
        source.Should().Contain("Text=\"{Binding BalanceInput, Mode=TwoWay}\"")
            .And.Contain("FontFamily=\"{StaticResource NumericFont}\"")
            .And.Contain("Text=\"{Binding Summary}\"")
            .And.Contain("{loc:Localized Trading.PaperResetTitle}")
            .And.Contain("{loc:Localized Trading.PaperResetBalanceLabel}")
            .And.Contain("{loc:Localized Trading.PaperResetHint}")
            .And.Contain("{loc:Localized Trading.PaperResetApply}")
            .And.Contain("{loc:Localized Trading.Cancel}")
            .And.Contain("Classes=\"primary\"");
    }

    [Fact]
    public void TheDialogViewModel_ParsesAndRefusesNeverDefaults()
    {
        var source = DialogViewModel;

        // The prefill seeds from the persisted choice; the parse refuses an
        // unparsable or non-positive entry inline (the localized not-a-number
        // line); a parsed choice closes the dialog through ChosenBalance.
        source.Should().Contain("public void Initialize(decimal currentBalance)")
            .And.Contain("public bool TryBuildChoice()")
            .And.Contain("public decimal? ChosenBalance { get; private set; }")
            .And.Contain("NotANumberKey = \"Trading.PaperResetNotANumber\"")
            .And.Contain("balance <= decimal.Zero");
    }

    [Fact]
    public void ThePaperResetVocabulary_ExistsInBothCultures()
    {
        foreach (var (culture, resx) in new[] { ("en", EnglishResx), ("ru", RussianResx) })
        {
            foreach (var key in PaperResetResxKeys)
            {
                resx.Should().Contain(
                    $"<data name=\"{key}\"",
                    "the {0} resx carries the T-192 key {1} — the feature speaks both cultures", culture, key);
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
