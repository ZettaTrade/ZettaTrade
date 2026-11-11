namespace ZettaTrade.App.Tests.Views;

using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using Xunit;

/// <summary>
/// The product identity pins (T-162): the shipped binaries speak the
/// in-house mark — the exe file's icon and the taskbar ride the committed
/// ICO set (a real 16px frame for the taskbar's own pick), every window
/// carries the same asset through its Icon attribute, the stock Avalonia
/// logo is gone from the assets entirely, and the shell's title-bar strip
/// passes the palette-tinted logomark through the kit's WindowChrome.Logo
/// contract. The kit-side facts (the slot's hit-invisibility — the strip
/// keeps its drag surface — and the both-variants wiring) live in the
/// Livery suite, the owning repo's own pins; this file pins the consumer
/// side.
/// </summary>
public sealed class BrandingPinsTests
{
    private static readonly string RepoRoot = FindRepositoryRoot();

    private static readonly string AppProjectDirectory =
        Path.Combine(RepoRoot, "src", "ZettaTrade.App");

    [Fact]
    public void TheProductMark_ShipsTheIconSet_WithTheVectorSourceAndNoStockLogo()
    {
        var icoPath = Path.Combine(AppProjectDirectory, "Assets", "ZettaTrade.ico");
        File.Exists(icoPath).Should().BeTrue("the committed ICO set is the shipped mark");

        // A real 16px frame: the taskbar's own pick must not be an upscaled
        // larger frame (the acceptance's 16px legibility fact).
        var bytes = File.ReadAllBytes(icoPath);
        var frameCount = BitConverter.ToUInt16(bytes, 4);
        frameCount.Should().BeGreaterThan(1, "the ICO set carries the taskbar-ready sizes");
        var hasSixteen = false;
        for (var index = 0; index < frameCount; index++)
        {
            var baseOffset = 6 + 16 * index;
            var width = bytes[baseOffset];
            var height = bytes[baseOffset + 1];
            if (width == 16 && height == 16)
            {
                hasSixteen = true;
            }
        }

        hasSixteen.Should().BeTrue("the ICO set carries a real 16px frame for the taskbar");

        File.Exists(Path.Combine(AppProjectDirectory, "Assets", "zettatrade-mark.svg"))
            .Should().BeTrue("the vector source of truth ships beside the ICO set");

        File.Exists(Path.Combine(AppProjectDirectory, "Assets", "avalonia-logo.ico"))
            .Should().BeFalse("no stock Avalonia logo remains in the shipped assets");
    }

    [Fact]
    public void TheExe_CarriesTheMark_AsItsApplicationIcon()
    {
        var csproj = File.ReadAllText(
            Path.Combine(AppProjectDirectory, "ZettaTrade.App.csproj"));

        csproj.Should().Contain(
            "<ApplicationIcon>Assets\\ZettaTrade.ico</ApplicationIcon>",
            "the exe file's icon (and the pinned taskbar icon) is the product mark");
    }

    [Fact]
    public void EveryWindow_CarriesTheProductIcon()
    {
        var windows = Directory.EnumerateFiles(
                Path.Combine(AppProjectDirectory, "Views"),
                "*.axaml",
                SearchOption.TopDirectoryOnly)
            .ToList();

        windows.Should().HaveCountGreaterThan(
            15,
            "the walk covers every window (a shrunk walk is a false green)");

        var offenders = windows.Where(window =>
            !File.ReadAllText(window).Contains("Icon=\"/Assets/ZettaTrade.ico\"", StringComparison.Ordinal));
        offenders.Should().BeEmpty(
            "every window rides the product mark through its Icon attribute: {0}",
            string.Join(", ", offenders));
    }

    [Fact]
    public void TheShellStrip_CarriesTheLogomark_ThroughTheKitContract()
    {
        var main = File.ReadAllText(
            Path.Combine(AppProjectDirectory, "Views", "MainWindow.axaml"));

        main.Should().Contain(
            "<livery:WindowChrome.Logo>",
            "the shell's strip shows the mark through the kit's WindowChrome.Logo contract");

        // The mark rides the palette keys (the tile Highlight, the Z the
        // derived readable caption ink — the scored pairing), never a hex
        // literal: the strip retints with the theme.
        main.Should().Contain("CornerRadius=\"3\" Background=\"{DynamicResource Highlight}\"",
            "the logomark's tile is the palette's on-state accent");
        main.Should().Contain("Stroke=\"{DynamicResource ChipForeground}\"",
            "the logomark's Z reads through the derived readable caption ink");
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
