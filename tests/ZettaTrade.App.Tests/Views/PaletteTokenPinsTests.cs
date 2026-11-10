namespace ZettaTrade.App.Tests.Views;

using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

/// <summary>
/// The palette-token pins (T-150): the views may carry no Fluent system
/// brush key at all — every view color rides the theme palette's own keys
/// (the status bar keeps Surface + a top Border stroke, the separators and
/// the settings glyphs pick Border/TextSecondary), so a user JSON theme
/// recolors every view surface live. The pins walk the real view sources
/// (the headless render harness is still pending), exactly like the chrome
/// and accessibility pins they sit beside; the walk is deterministic for
/// tests and gate runs alike.
/// </summary>
public sealed class PaletteTokenPinsTests
{
    private static readonly string RepoRoot = FindRepositoryRoot();

    private static readonly string AppProjectDirectory =
        Path.Combine(RepoRoot, "src", "ZettaTrade.App");

    /// <summary>
    /// Every application view source drops its Fluent system brushes: the
    /// forbidden keys (<c>SystemAccentColor*</c>, <c>SystemControl*</c>)
    /// cannot tint a user theme. The walk pins every view surface and the
    /// application's own resource dictionary (App.axaml included), so the
    /// pin covers the whole compiled resource set.
    /// </summary>
    [Fact]
    public void EveryAppSource_DropsTheFluentSystemBrushKeys()
    {
        var sources = Directory.EnumerateFiles(
            AppProjectDirectory,
            "*.axaml",
            SearchOption.AllDirectories)
            .ToList();

        sources.Should().HaveCountGreaterThan(20,
            "the app view walk must cover the whole view tree (a shrunk walk is a false green)");

        var offenders = new List<string>();
        foreach (var source in sources)
        {
            var markup = File.ReadAllText(source);
            if (markup.Contains("SystemAccent", StringComparison.Ordinal) ||
                markup.Contains("SystemControl", StringComparison.Ordinal))
            {
                offenders.Add(source);
            }
        }

        offenders.Should().BeEmpty(
            "every view surface must ride the theme palette's own keys: {0}",
            string.Join(", ", offenders));
    }

    /// <summary>
    /// The status bar rides the palette: Surface fill over a top Border
    /// stroke — previously it painted the Fluent accent tint, which no user
    /// theme could retint (it kept `SystemAccentColorLight2` under every
    /// palette).
    /// </summary>
    [Fact]
    public void TheStatusBar_RidesThePaletteSurfaces()
    {
        var source = File.ReadAllText(
            Path.Combine(AppProjectDirectory, "Views", "MainWindow.axaml"));

        // The status bar block: a bottom-docked Border painting Surface with
        // the primary Border stroke riding the top edge.
        var pattern = @"DockPanel\.Dock=""Bottom""
\s+Background=""\{DynamicResource Surface\}""
\s+BorderThickness=""0,1,0,0""
\s+BorderBrush=""\{DynamicResource Border\}""";

        new Regex(pattern)
            .IsMatch(source)
            .Should().BeTrue("the status bar must ride Surface and a top Border stroke, the palette keys' own shapes");
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
