namespace ZettaTrade.App.Tests.Packaging;

using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using Xunit;

/// <summary>
/// The T-257 release-door presence pins: .github/workflows/release.yml
/// must keep the T-125 bare-archive matrix untouched, invoke the new
/// packaging scripts on the matching OS legs, digest-pin the AppImage
/// tooling, carry the platform-native names in the release job's
/// expected-artifacts assert, and docs/SECURITY.md must say exactly what
/// is unsigned. These facts run anywhere; what they CANNOT prove locally
/// is that the artifacts install or launch on macOS and Linux - that is
/// CI's job (the runner .deb install smoke) plus the target-OS acceptance.
/// </summary>
public sealed class ReleaseDoorWorkflowPinsTests
{
    private static readonly string RepoRoot = FindRepositoryRoot();

    private static string WorkflowText =>
        File.ReadAllText(Path.Combine(RepoRoot, ".github", "workflows", "release.yml"));

    private static string SecurityText =>
        File.ReadAllText(Path.Combine(RepoRoot, "docs", "SECURITY.md"));

    [Fact]
    public void TheMatrix_KeepsTheFourBareArchiveLegs_OfTheT125Shape()
    {
        var lines = File.ReadAllLines(Path.Combine(RepoRoot, ".github", "workflows", "release.yml"));
        var legs = new (string Rid, string Ext)[]
        {
            ("win-x64", ".zip"),
            ("osx-x64", ".zip"),
            ("osx-arm64", ".zip"),
            ("linux-x64", ".tar.gz"),
        };
        foreach (var (rid, ext) in legs)
        {
            var legLines = lines.Where(line => line.Contains("rid: " + rid)).ToList();
            legLines.Should().HaveCount(1, $"the {rid} matrix leg must exist exactly once");
            legLines[0].Should().Contain($"ext: \"{ext}\"", $"the {rid} leg keeps its T-125 archive extension");
        }
    }

    [Fact]
    public void TheOsxLegs_InvokeTheDmgPackagingScript_OverThePipelinePublish()
    {
        var text = WorkflowText;
        text.CountOccurrences("if: runner.os == 'macOS'")
            .Should().Be(2, "the .dmg build step and its upload both ride the macOS condition");
        text.Should().Contain(
            "pwsh -NoProfile -File scripts/macos-dmg.ps1 -PublishDir publish -OutDir dmg-out -Rid '${{ matrix.rid }}'",
            "the macOS legs must stage and build through the packaging script, consuming the leg's own publish tree");
        text.Should().Contain("path: dmg-out/zettatrade-*-osx-*.dmg",
            "the .dmg artifact upload must pick up the release-naming family");
    }

    [Fact]
    public void TheLinuxLeg_InvokesTheAppImageAndDebScripts_AndInstallsTheDeb()
    {
        var text = WorkflowText;
        text.CountOccurrences("if: runner.os == 'Linux'")
            .Should().Be(4, "the packaging step, the install smoke and the two uploads all ride the Linux condition");
        text.Should().Contain(
            "pwsh -NoProfile -File scripts/linux-appimage.ps1 -PublishDir publish -OutDir appimage-out",
            "the linux leg must build the AppImage through the packaging script");
        text.Should().Contain(
            "pwsh -NoProfile -File scripts/linux-deb.ps1 -PublishDir publish -OutDir deb-out",
            "the linux leg must build the .deb through the packaging script");
        text.Should().Contain("sudo dpkg -i $deb",
            "the linux leg must install the .deb on the runner - the CI-only install acceptance");
        text.Should().Contain("install ok installed $version",
            "the install smoke must assert the clean dpkg status, not just a zero exit");
    }

    [Fact]
    public void TheAppImageTool_IsDownloadedFromItsOwnFeed_DigestPinned()
    {
        var text = WorkflowText;
        text.Should().Contain(
            "https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage",
            "the AppImage tooling must come from the project's own release feed");
        text.Should().Contain(
            "E8C1169C4AF66C3184D7EE110713D4BE04BEFE671FA358E2C1591EE840553B03",
            "the downloaded tooling must be checked against the pinned digest");
        text.Should().Contain("then re-pin the digest here and in docs/SECURITY.md",
            "the mismatch failure must tell the operator the re-pin procedure");
    }

    [Fact]
    public void TheReleaseJob_AssertsEveryPlatformNativeDoor_NextToTheBareArchives()
    {
        var text = WorkflowText;
        var smokeStart = text.IndexOf("Smoke - every archive is present and non-empty", StringComparison.Ordinal);
        var sumsStart = text.IndexOf("SHA-256 sums next to the archives", StringComparison.Ordinal);
        smokeStart.Should().BeGreaterThan(0, "the release job must carry the archive presence smoke");
        sumsStart.Should().BeGreaterThan(smokeStart, "the sums step must follow the presence smoke");
        var block = text.Substring(smokeStart, sumsStart - smokeStart);
        block.Should().Contain("foreach ($rid in @('win-x64', 'osx-x64', 'osx-arm64'))",
            "the release job must keep the T-125 zip family loop");
        block.Should().Contain("\"zettatrade-$version-$rid.zip\"",
            "the release job must keep the zip family assert");
        foreach (var name in new[]
                 {
                     "zettatrade-$version-linux-x64.tar.gz",
                     "zettatrade-$version-win-x64-setup.exe",
                     "zettatrade-$version-osx-x64.dmg",
                     "zettatrade-$version-osx-arm64.dmg",
                     "zettatrade-$version-linux-x64.AppImage",
                     "zettatrade-$version-linux-x64.deb",
                 })
        {
            block.Should().Contain(name, $"the release job's expected-artifacts assert must carry {name}");
        }
    }

    [Fact]
    public void TheDocs_SayExactlyWhatIsUnsigned_ForAllThreeDoors()
    {
        // The prose wraps mid-phrase; normalize line breaks so the pins
        // read like the sentences they pin.
        var text = SecurityText
            .Replace("\r\n", " ")
            .Replace('\n', ' ')
            .Replace("  ", " ");
        text.Should().Contain("no Developer ID Application signature and no notarization ticket",
            "the macOS caveat must name the exact missing credentials");
        text.Should().Contain("right-click `ZettaTrade.app` and choose Open",
            "the macOS caveat must carry the Gatekeeper first-launch workaround");
        text.Should().Contain("notarization stays deferred until the Apple Developer account decision",
            "the macOS caveat must state the deferral decision");
        text.Should().Contain("The AppImage carries no signature",
            "the AppImage caveat must state its unsigned posture");
        text.Should().Contain("The .deb is unsigned too",
            "the .deb caveat must state its unsigned posture");
        text.Should().Contain("digest-pinned", "the tooling supply-chain posture must be documented");
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

/// <summary>Small text helper shared by the release-door pins.</summary>
internal static class ReleaseDoorTextPins
{
    public static int CountOccurrences(this string text, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
