namespace ZettaTrade.App.Tests.Views;

using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

/// <summary>
/// The type-token consumer pins (T-155): the ramp, the spacing scale and
/// the numeric stack publish once from the Livery package (its own repo
/// pins the token shapes), so the views carry no bare FontSize literals
/// (the unset ones ride the platform default of 12, the ramp steps ride
/// the resources), the numeric surfaces set digits in the shared
/// NumericFont stack and no scale-step value stays a bare spacing
/// literal. The repo has no headless render harness yet (the T-144 lane
/// is queued), so the facts ride the source of truth (the chrome/access
/// pins' precedent); the language-flip alignment render pin waits for
/// that lane.
/// </summary>
public sealed class TokensPinsTests
{
    private static readonly string RepoRoot = FindRepositoryRoot();

    private static string AppXaml =>
        File.ReadAllText(Path.Combine(RepoRoot, "src", "ZettaTrade.App", "App.axaml"));

    /// <summary>Every axaml file the App project ships, normalized to LF.</summary>
    private static string[] ViewSources =>
        Directory.GetFiles(Path.Combine(RepoRoot, "src", "ZettaTrade.App"), "*.axaml", SearchOption.AllDirectories)
            .Select(File.ReadAllText)
            .Select(s => s.Replace("\r\n", "\n"))
            .ToArray();

    [Fact]
    public void TheViews_CarryNoBareFontSizeLiterals()
    {
        // The Avalonia TextElement default is 12, so an unset FontSize IS
        // the platform default; every explicit size must name a ramp step.
        foreach (var source in ViewSources)
        {
            Regex.Matches(source, "FontSize=\"[0-9.]+\"")
                .Should().BeEmpty("a bare FontSize literal is an ad-hoc value the ramp must own");
        }
    }

    [Fact]
    public void TheRamp_StepsAreAllInUse()
    {
        var sources = ViewSources;

        foreach (var step in new[] { "CaptionFontSize", "BodyFontSize", "SubtitleStrongFontSize", "TitleFontSize" })
        {
            sources.Count(s => s.Contains(step, StringComparison.Ordinal))
                .Should().BeGreaterThan(0, "the ramp's {0} step is a published vocabulary, not dead weight", step);
        }
    }

    [Fact]
    public void TheNumericSurfaces_RideTheSharedStack()
    {
        var stats = File.ReadAllText(Path.Combine(RepoRoot, "src", "ZettaTrade.App", "Views", "TradeStatsWindow.axaml")).Replace("\r\n", "\n");
        var positions = File.ReadAllText(Path.Combine(RepoRoot, "src", "ZettaTrade.App", "Views", "PositionsPanelWindow.axaml")).Replace("\r\n", "\n");
        var history = File.ReadAllText(Path.Combine(RepoRoot, "src", "ZettaTrade.App", "Views", "OrderHistoryWindow.axaml")).Replace("\r\n", "\n");
        var ticket = File.ReadAllText(Path.Combine(RepoRoot, "src", "ZettaTrade.App", "Views", "NewOrderDialogWindow.axaml")).Replace("\r\n", "\n");
        var amend = File.ReadAllText(Path.Combine(RepoRoot, "src", "ZettaTrade.App", "Views", "AmendOrderDialogWindow.axaml")).Replace("\r\n", "\n");
        var main = File.ReadAllText(Path.Combine(RepoRoot, "src", "ZettaTrade.App", "Views", "MainWindow.axaml")).Replace("\r\n", "\n");
        var backtest = File.ReadAllText(Path.Combine(RepoRoot, "src", "ZettaTrade.App", "Views", "BacktestWindow.axaml")).Replace("\r\n", "\n");
        var screener = File.ReadAllText(Path.Combine(RepoRoot, "src", "ZettaTrade.App", "Views", "ScreenerWindow.axaml")).Replace("\r\n", "\n");

        Regex.Matches(stats, "FontFamily=\"{StaticResource NumericFont}\"")
            .Should().HaveCount(29, "all fifteen stats tiles (T-251 adds the five risk tiles) plus the realized table's closed/quantity/entry/exit/pnl cells and the bucket tables' hour/trades/wins/losses/pnl cells (the weekday bucket label stays a localized word) set their values in the tabular stack");
        Regex.Matches(positions, "FontFamily=\"{StaticResource NumericFont}\"")
            .Should().HaveCount(13, "the three sections' numeric columns (5 positions + 3 open orders + 4 session journal) and the reduce input set their digits in the tabular stack (T-157; T-187 adds the positions table's stop-loss and take-profit cells)");
        Regex.Matches(history, "FontFamily=\"{StaticResource NumericFont}\"")
            .Should().HaveCount(4, "the history table's opened/quantity/price/fee columns hold their digits");
        Regex.Matches(ticket, "FontFamily=\"{StaticResource NumericFont}\"")
            .Should().HaveCount(7, "the ticket's quantity/price/trigger/trail/OCO-cap/risk fields and the bracket's take-profit input carry the stack");
        Regex.Matches(amend, "FontFamily=\"{StaticResource NumericFont}\"")
            .Should().HaveCount(2, "the amend dialog's quantity and price fields carry the stack");
        Regex.Matches(main, "FontFamily=\"{StaticResource NumericFont}\"")
            .Should().HaveCount(1, "the HUD's readout dedupes onto the token (its precedent)");
        Regex.Matches(backtest, "FontFamily=\"{StaticResource NumericFont}\"")
            .Should().HaveCount(5, "the report box plus the sweep table's net/drawdown/win-rate/profit-factor columns carry the digits (T-212 adds the sweep table)");
        Regex.Matches(screener, "FontFamily=\"{StaticResource NumericFont}\"")
            .Should().HaveCount(4, "the screener table's price/change/volume/funding columns hold their digits (T-195; T-197 adds the funding cell)");

        // The stack lives once: no view re-types the font family literal.
        foreach (var source in ViewSources)
        {
            source.Should().NotContain(
                "Consolas",
                "the monospaced stack is the Livery NumericFont token, never a per-view literal");
        }
    }

    [Fact]
    public void TheScaleSteps_AreAdoptedWhereTheyChangeNothing()
    {
        // The scale's own steps (4/8/12/16) ride the tokens wherever the
        // value already matched — a no-change vocabulary adoption; the
        // between-step drift (2/3/6/7/10/14/15/20) is a layout-tuning
        // decision that belongs to the surface redesigns (T-156/T-157/T-161).
        foreach (var source in ViewSources)
        {
            Regex.Matches(source, "(Padding|Margin|Spacing|RowSpacing|ColumnSpacing)=\"(4|8|12|16)\"")
                .Should().BeEmpty("a bare scale-step literal is an unadopted token");
        }
    }

    [Fact]
    public void TheTokensDictionary_IsMergedOnceAtAppLevel()
    {
        var source = AppXaml;

        source.Should().Contain(
            "<ResourceInclude Source=\"avares://Livery/Themes/Tokens.axaml\" />",
            "the token dictionary merges exactly once, above every view");
    }

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
