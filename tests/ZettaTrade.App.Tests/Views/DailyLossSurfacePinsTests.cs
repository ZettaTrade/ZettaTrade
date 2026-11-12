namespace ZettaTrade.App.Tests.Views;

using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

/// <summary>
/// The daily loss governor's surface pins (T-189): the ticket reads the
/// blocked state honestly (a banner above the flow-failure line, the reset
/// beside it behind ONE confirmation), the order gate's ordering is the
/// design decision (the reduce-only exemption precedes any read, the loss
/// gate precedes the confirmation threshold), the composition wires the
/// service before the risk manager, and the feature's localization
/// vocabulary exists in BOTH cultures (en + ru). The repo has no headless
/// render harness yet (the T-144 lane is queued), so the facts walk the
/// sources of truth — the kill switch's surface pins' precedent.
/// </summary>
public sealed class DailyLossSurfacePinsTests
{
    private static readonly string RepoRoot = FindRepositoryRoot();

    private static string TicketXaml =>
        File.ReadAllText(Path.Combine(RepoRoot, "src", "ZettaTrade.App", "Views", "NewOrderDialogWindow.axaml"))
            .Replace("\r\n", "\n");

    private static string TicketViewModel =>
        File.ReadAllText(Path.Combine(RepoRoot, "src", "ZettaTrade.App", "ViewModels", "NewOrderDialogViewModel.cs"))
            .Replace("\r\n", "\n");

    private static string RiskManager =>
        File.ReadAllText(Path.Combine(RepoRoot, "src", "ZettaTrade.App", "Services", "DefaultRiskManager.cs"))
            .Replace("\r\n", "\n");

    private static string Composition =>
        File.ReadAllText(Path.Combine(RepoRoot, "src", "ZettaTrade.App", "CompositionRoot.cs"))
            .Replace("\r\n", "\n");

    private static string EnglishResx =>
        File.ReadAllText(Path.Combine(RepoRoot, "src", "ZettaTrade.UI", "Resources", "Strings.resx"));

    private static string RussianResx =>
        File.ReadAllText(Path.Combine(RepoRoot, "src", "ZettaTrade.UI", "Resources", "Strings.ru.resx"));

    /// <summary>The localization vocabulary the T-189 surfaces speak, in both cultures.</summary>
    private static readonly string[] DailyLossResxKeys =
    [
        "Trading.DailyLossBannerFormat",
        "Trading.DailyLossReset",
        "Trading.ConfirmDailyLossReset",
        "NewOrder.DailyLossResetFailed",
    ];

    [Fact]
    public void TheTicket_RendersTheBannerAboveTheFlowFailures()
    {
        var source = TicketXaml;

        // The banner speaks the view-model's honest state with its reset
        // beside it; the reset control answers by AutomationId exactly once.
        source.Should().Contain("IsVisible=\"{Binding IsDailyLossBlocked}\"")
            .And.Contain("Text=\"{Binding DailyLossBanner}\"")
            .And.Contain("Command=\"{Binding ResetDailyLossCommand}\"")
            .And.Contain("{loc:Localized Trading.DailyLossReset}");

        Regex.Matches(source, "AutomationProperties\\.AutomationId=\"DailyLossResetButton\"")
            .Should().HaveCount(1, "the reset control is reachable by its AutomationId exactly once");

        // The banner rides ABOVE the flow-failure line: the block's own
        // vocabulary never hides behind a later error.
        var bannerIndex = source.IndexOf("IsDailyLossBlocked", StringComparison.Ordinal);
        var errorIndex = source.IndexOf("Text=\"{Binding ErrorMessage}\"", StringComparison.Ordinal);
        bannerIndex.Should().BePositive("the banner must exist");
        errorIndex.Should().BeGreaterThan(bannerIndex, "the banner renders above the error line");
    }

    [Fact]
    public void TheTicketBanner_RefreshesPerSettledSymbolAndAfterEveryFailure()
    {
        var source = TicketViewModel;

        // The throttled symbol-driven refresh mirrors the fee-estimate
        // reference read's quiet-window discipline (the bootstrapping
        // initial emission prices the banner at dialog open).
        source.Should().Contain(".Select(_ => Observable.FromAsync(() => RefreshDailyLossBannerAsync(force: false)))")
            .And.Contain(".Switch()")
            .And.Contain(".ObserveOnUiThread()");

        // Every failed placement (the main catch, the OCO pair abort, both
        // risk-rejection sites, the bracket catch) plus the operator's reset
        // re-read the banner state honestly.
        Regex.Matches(source, "RefreshDailyLossBannerAsync\\(force: true\\)")
            .Should().HaveCountGreaterThanOrEqualTo(
                6,
                "the banner re-reads after every failure site and the reset — a stale banner never stands");

        // A faulted read hides the banner (a display convenience, never a
        // gate — the submit pipeline re-checks the truth at every placement).
        source.Should().Contain("the daily loss banner read failed; the banner stays hidden");
    }

    [Fact]
    public void TheTicketReset_AskOneConfirmationThenLiftsTheBlock()
    {
        var source = TicketViewModel;

        var handlerIndex = source.IndexOf("private async Task HandleResetDailyLossAsync()", StringComparison.Ordinal);
        var confirmIndex = source.IndexOf("_confirmation.RequestConfirmationAsync(", handlerIndex, StringComparison.Ordinal);
        var resetIndex = source.IndexOf("await _dailyLoss.ResetAsync()", confirmIndex, StringComparison.Ordinal);
        var refreshIndex = source.IndexOf("RefreshDailyLossBannerAsync(force: true)", resetIndex, StringComparison.Ordinal);

        handlerIndex.Should().BePositive("the reset handler must exist");
        confirmIndex.Should().BeGreaterThan(handlerIndex, "the reset asks its confirmation first");
        resetIndex.Should().BeGreaterThan(confirmIndex, "the marker writes only after a confirmed ask");
        refreshIndex.Should().BeGreaterThan(resetIndex, "the banner re-reads the fresh state after the write");
    }

    [Fact]
    public void TheOrderGate_ExemptsReduceOnlyAndRidesBeforeTheConfirmation()
    {
        var source = RiskManager;

        // The design decision pinned in source: the reduce-only exemption
        // precedes any governor read at all, and the loss gate precedes the
        // confirmation threshold — a blocked order never reaches a dialog.
        var exemptIndex = source.IndexOf(
            "if (!request.ReduceOnly && _dailyLoss is { } dailyLoss)",
            StringComparison.Ordinal);
        var gateIndex = source.IndexOf(
            "await dailyLoss.ReadStateAsync(request.Symbol, ct)",
            StringComparison.Ordinal);
        var thresholdIndex = source.IndexOf(
            "if (estimatedValue > MaxOrderValueUsd)",
            StringComparison.Ordinal);

        exemptIndex.Should().BePositive("the reduce-only exemption must exist");
        gateIndex.Should().BeGreaterThan(exemptIndex, "the exemption precedes any engine read");
        thresholdIndex.Should().BeGreaterThan(gateIndex, "the loss gate rides before the confirmation threshold");
    }

    [Fact]
    public void TheComposition_WiresTheGovernorBeforeTheRiskManager()
    {
        var source = Composition;

        var wiringIndex = source.IndexOf("services.AddRiskLimitServices();", StringComparison.Ordinal);
        var managerIndex = source.IndexOf(
            "services.AddSingleton<IRiskManager, DefaultRiskManager>();",
            StringComparison.Ordinal);

        wiringIndex.Should().BePositive("the governor's registration must exist");
        managerIndex.Should().BeGreaterThan(wiringIndex, "the manager's optional dependency resolves from the earlier registration");
    }

    [Fact]
    public void TheDailyLossVocabulary_ExistsInBothCultures()
    {
        foreach (var (culture, resx) in new[] { ("en", EnglishResx), ("ru", RussianResx) })
        {
            foreach (var key in DailyLossResxKeys)
            {
                resx.Should().Contain(
                    $"<data name=\"{key}\"",
                    "the {0} resx carries the T-189 key {1} — the feature speaks both cultures", culture, key);
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
