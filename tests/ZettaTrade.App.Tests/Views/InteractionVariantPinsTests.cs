namespace ZettaTrade.App.Tests.Views;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

/// <summary>
/// The interaction-variant source pins (T-181): every window-scoped Button
/// class style that paints a Background ships its <c>:pointerover</c> and
/// <c>:pressed</c> siblings — the window's styles apply after the app
/// styles, so Livery's generic hover/pressed fills never reach a classed
/// button (the kit's own strip-icon discipline), and a family that skips
/// them goes dead under the pointer. The walk covers the whole App project
/// (the windows, the panels and the window-included theme dictionaries),
/// the rule itself is pinned against the ticket's pre-fix shape naming the
/// family and the missing sibling, and the ticket's own variants are pinned
/// to the kit's language: the emphasis ring answers the hover, the strip's
/// dim step answers the press, and neither ever touches the state fill (a
/// trading segment keeps its side under the pointer).
/// </summary>
public sealed class InteractionVariantPinsTests
{
    private static readonly string RepoRoot = FindRepositoryRoot();

    private static readonly string AppProjectDirectory =
        Path.Combine(RepoRoot, "src", "ZettaTrade.App");

    /// <summary>The opening tag of every Style block, selector captured.</summary>
    private static readonly Regex StyleOpen = new(
        "<Style\\s+Selector=\"(?<selector>[^\"]+)\"",
        RegexOptions.Compiled);

    /// <summary>
    /// A plain classed Button selector — only dot-joined class tokens, no
    /// pseudo-class, no template lane (the pseudo styles are the siblings
    /// themselves, not the carriers).
    /// </summary>
    private static readonly Regex ClassedButton = new(
        "^Button\\.(?<classes>[a-z][a-z0-9-]*(?:\\.[a-z][a-z0-9-]+)*)$",
        RegexOptions.Compiled);

    /// <summary>
    /// The ticket's pre-fix shape (T-181's origin): the state fills ride the
    /// class styles with no interaction sibling anywhere — both families go
    /// dead under the pointer.
    /// </summary>
    private const string PreFixTicketStyles = """
        <Style Selector="Button.side.buy.selected">
            <Setter Property="Background" Value="{DynamicResource CandleUp}" />
            <Setter Property="Foreground" Value="{DynamicResource ChipForeground}" />
        </Style>
        <Style Selector="Button.cta.buy">
            <Setter Property="Background" Value="{DynamicResource CandleUp}" />
            <Setter Property="Foreground" Value="{DynamicResource ChipForeground}" />
        </Style>
        """;

    [Fact]
    public void EveryBackgroundCarryingButtonFamily_ShipsItsInteractionSiblings()
    {
        var files = Directory
            .EnumerateFiles(AppProjectDirectory, "*.axaml", SearchOption.AllDirectories)
            .ToList();

        files.Should().HaveCountGreaterThan(
            20,
            "the walk covers the whole App project (a shrunk walk is a false green)");

        var offenders = files
            .SelectMany(file => FindFamiliesWithoutSiblings(File.ReadAllText(file))
                .Select(offense => $"{file}: {offense}"))
            .ToList();

        offenders.Should().BeEmpty(
            "every window-scoped Button class family that paints a background answers the pointer: {0}",
            string.Join(", ", offenders));
    }

    [Fact]
    public void ThePreFixTicketShape_ReportsBothFamiliesWithTheirMissingSiblings()
    {
        var offenses = FindFamiliesWithoutSiblings(PreFixTicketStyles);

        offenses.Should().BeEquivalentTo(
        [
            "side (Button.side.buy.selected) — missing :pointerover, :pressed",
            "cta (Button.cta.buy) — missing :pointerover, :pressed",
        ], "the pre-fix ticket is exactly the hole the rule names: the family and the missing siblings");
    }

    [Fact]
    public void TheTicketVariants_RideTheKitRingAndTheStripDimStep_NeverTheStateFill()
    {
        var source = File.ReadAllText(
            Path.Combine(AppProjectDirectory, "Views", "NewOrderDialogWindow.axaml"));

        // The hover answers through the kit's emphasis ring (the row
        // grammar's own hover border), the press through the strip's dim
        // step — the palette-keyed shift the acceptance's contrast rule
        // holds (the state fills stay, so the scored chip pairs stay).
        BlockOf(source, "Button.side:pointerover").Should().Contain(
            "<Setter Property=\"BorderBrush\" Value=\"{DynamicResource Highlight}\" />",
            "the side segments' hover rides the kit's emphasis ring");
        BlockOf(source, "Button.cta:pointerover").Should().Contain(
            "<Setter Property=\"BorderBrush\" Value=\"{DynamicResource Highlight}\" />",
            "the CTA's hover rides the kit's emphasis ring");
        BlockOf(source, "Button.side:pressed").Should().Contain(
            "<Setter Property=\"Opacity\" Value=\"0.8\" />",
            "the side segments' press rides the strip's dim step");
        BlockOf(source, "Button.cta:pressed").Should().Contain(
            "<Setter Property=\"Opacity\" Value=\"0.8\" />",
            "the CTA's press rides the strip's dim step");

        // The variants never paint: a trading segment keeps its side under
        // the pointer, and the scored state fills stay exactly where the
        // T-152 battery pinned them.
        foreach (var selector in new[]
                 {
                     "Button.side:pointerover", "Button.side:pressed",
                     "Button.cta:pointerover", "Button.cta:pressed",
                 })
        {
            BlockOf(source, selector).Should().NotContain(
                "Property=\"Background\"",
                "the {0} variant shifts state, never the state fill", selector);
        }
    }

    [Fact]
    public void TheFleetFixes_RideTheSameKitLanguage()
    {
        // The settings star: the quiet glyph brightens on hover and holds
        // through the press (the copy-pin's quiet-control language); the
        // armed state rides after, so the pinned star stays gold.
        var settings = File.ReadAllText(
            Path.Combine(AppProjectDirectory, "Views", "SettingsWindow.axaml"));
        var starHover = BlockOf(settings, "Button.pair-star:pointerover");
        var starPressed = BlockOf(settings, "Button.pair-star:pressed");
        starHover.Should().Contain(
            "<Setter Property=\"Foreground\" Value=\"{DynamicResource TextPrimary}\"",
            "the quiet star brightens to the primary text ink under the pointer");
        starPressed.Should().Contain(
            "<Setter Property=\"Foreground\" Value=\"{DynamicResource TextPrimary}\"",
            "the quiet star holds the brightened ink through the press");
        settings.IndexOf(BlockOf(settings, "Button.pair-star.on"), StringComparison.Ordinal)
            .Should().BeGreaterThan(
                settings.IndexOf(starPressed, StringComparison.Ordinal),
                "the armed state's block rides after the variants — the pinned star never greys");

        // The row grammar: the family's pressed step lands after the
        // secondary's rest and before the destructive blocks, so the ghost's
        // own pressed keeps winning and the quiet sibling presses alive.
        var rowGrammar = File.ReadAllText(
            Path.Combine(AppProjectDirectory, "Themes", "RowGrammar.axaml"));
        rowGrammar.Should().Contain(
            "<Style Selector=\"Button.row-action:pressed\">\n        <Setter Property=\"Background\" Value=\"{DynamicResource Border}\"",
            "the row-action family's pressed step is the generic kit fill, stated explicitly");
        rowGrammar.IndexOf(BlockOf(rowGrammar, "Button.row-action:pressed"), StringComparison.Ordinal)
            .Should().BeGreaterThan(
                rowGrammar.IndexOf(BlockOf(rowGrammar, "Button.row-action.secondary"), StringComparison.Ordinal),
                "the pressed step rides after the secondary's rest — the outlined sibling presses alive");
        rowGrammar.IndexOf(BlockOf(rowGrammar, "Button.row-action.destructive:pressed"), StringComparison.Ordinal)
            .Should().BeGreaterThan(
                rowGrammar.IndexOf(BlockOf(rowGrammar, "Button.row-action:pressed"), StringComparison.Ordinal),
                "the ghost's own pressed rides last — the subdued state never picks up the family fill");
    }

    /// <summary>
    /// Walk the source's Style blocks: a classed Button selector carrying no
    /// pseudo-class whose block paints a Background is a background carrier;
    /// its family (the first class token) must own a <c>:pointerover</c> and
    /// a <c>:pressed</c> sibling somewhere in the same file. Returns one
    /// offense per family that misses at least one sibling, naming the
    /// family, the carrier selector and the missing siblings.
    /// </summary>
    internal static IReadOnlyList<string> FindFamiliesWithoutSiblings(string source)
    {
        var selectors = new List<string>();
        var carriers = new List<(string Family, string Selector)>();
        for (var match = StyleOpen.Match(source); match.Success; match = match.NextMatch())
        {
            var selector = match.Groups["selector"].Value;
            selectors.Add(selector);

            var classed = ClassedButton.Match(selector);
            if (!classed.Success)
            {
                continue;
            }

            var blockEnd = source.IndexOf("</Style>", match.Index + match.Length, StringComparison.Ordinal);
            var block = blockEnd < 0
                ? source[(match.Index + match.Length)..]
                : source[(match.Index + match.Length)..blockEnd];
            if (block.Contains("Property=\"Background\"", StringComparison.Ordinal))
            {
                carriers.Add((classed.Groups["classes"].Value.Split('.')[0], selector));
            }
        }

        var offenses = new List<string>();
        foreach (var family in carriers.Select(carrier => carrier.Family).Distinct())
        {
            var missing = new List<string>();
            foreach (var sibling in new[] { "pointerover", "pressed" })
            {
                var siblingPattern = new Regex(
                    $"^Button\\.{Regex.Escape(family)}(?:\\.[a-z][a-z0-9-]+)*:{sibling}$",
                    RegexOptions.Compiled);
                if (!selectors.Any(candidate => siblingPattern.IsMatch(candidate)))
                {
                    missing.Add($":{sibling}");
                }
            }

            if (missing.Count > 0)
            {
                var carrier = carriers.First(entry => entry.Family == family);
                offenses.Add($"{family} ({carrier.Selector}) — missing {string.Join(", ", missing)}");
            }
        }

        return offenses;
    }

    /// <summary>The body of the named Style block, opening tag included.</summary>
    private static string BlockOf(string source, string selector)
    {
        var open = source.IndexOf($"<Style Selector=\"{selector}\">", StringComparison.Ordinal);
        open.Should().BeGreaterThanOrEqualTo(0, $"the block <Style Selector=\"{selector}\"> must exist");
        var end = source.IndexOf("</Style>", open, StringComparison.Ordinal);
        return source[open..(end + "</Style>".Length)];
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
