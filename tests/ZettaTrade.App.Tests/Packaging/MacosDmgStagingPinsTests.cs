namespace ZettaTrade.App.Tests.Packaging;

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

/// <summary>
/// The T-257 macOS .dmg script pins: the version parse mirrors the
/// release pipeline's single source (the committed Directory.Build.props),
/// the stage-only honesty is explicit where hdiutil is absent, the bundle
/// layout and Info.plist invariants are pinned in the script, the committed
/// .icns is a valid PNG-frame container, and a live stage-only run over a
/// fake publish tree produces the pinned bundle layout on generated temp
/// files. What is NOT verified locally: the hdiutil build itself (the
/// UDZO image, the koly trailer assert) - CI's macOS legs carry it.
/// </summary>
public sealed class MacosDmgStagingPinsTests
{
    private static readonly string RepoRoot = FindRepositoryRoot();

    private static string ScriptText =>
        File.ReadAllText(Path.Combine(RepoRoot, "scripts", "macos-dmg.ps1"));

    [Fact]
    public void TheVersionParse_MirrorsThePipeline_PropsIsTheSingleSource()
    {
        var text = ScriptText;
        text.Should().Contain("<VersionPrefix>([^<>]+)</VersionPrefix>",
            "the props parse regex must be the pipeline's exact shape");
        text.Should().Contain("<VersionSuffix>([^<>]+)</VersionSuffix>",
            "the props parse regex must be the pipeline's exact shape");
        text.Should().Contain("[Parameter(Mandatory = $true)]",
            "-OutDir and -PublishDir stay caller-given and mandatory (no baked defaults)");
        text.Should().Contain("never a repo-relative or hardcoded",
            "the caller-given path contract must be documented in the script");
    }

    [Fact]
    public void TheStageOnlyHonesty_IsExplicit_WhereHdiutilIsAbsent()
    {
        var text = ScriptText;
        text.Should().Contain("STAGE-ONLY SMOKE OK.",
            "the script must say plainly that the local run does not claim the build");
        text.Should().Contain("hdiutil was not found on this machine (a macOS-only tool)",
            "the limitation must name the missing tool");
        text.Should().Contain("records the limitation instead of claiming the acceptance",
            "the honest-acceptance posture must be stated in the script");
    }

    [Fact]
    public void TheBundleLayout_AndInfoPlistPins_ArePinnedInTheScript()
    {
        var text = ScriptText;
        foreach (var pin in new[]
                 {
                     "Contents/MacOS",
                     "Contents/Resources/ZettaTrade.icns",
                     "<string>ZettaTrade.App</string>",
                     "<string>io.github.zettatrade.ZettaTrade</string>",
                     "<string>12.0</string>",
                     "<string>public.app-category.finance</string>",
                     "<key>NSHighResolutionCapable</key>",
                     "<string>APPL</string>",
                     "@@VERSION@@",
                 })
        {
            text.Should().Contain(pin, $"the bundle manifest/layout pin must travel in the script: {pin}");
        }
    }

    [Fact]
    public void TheDmgBuild_Branch_PinsTheUdifTrailer_AndTheReleaseNamingFamily()
    {
        var text = ScriptText;
        text.Should().Contain("-format UDZO", "the image must be the compressed UDZO form");
        text.Should().Contain("-ne 'koly'",
            "the built .dmg must be asserted against the UDIF trailer magic");
        text.Should().Contain("zettatrade-$version-$Rid.dmg",
            "the artifact name must stay in the release naming family");
    }

    [Fact]
    public void TheCommittedIcns_IsAValidPngFrameContainer_WithThePinnedFrames()
    {
        var bytes = File.ReadAllBytes(Path.Combine(RepoRoot, "scripts", "assets", "zettatrade.icns"));
        Encoding.ASCII.GetString(bytes, 0, 4).Should().Be("icns", "the container magic must be icns");

        var totalLength = BigEndianUInt32(bytes, 4);
        totalLength.Should().Be((uint)bytes.Length, "the container length field must match the file");

        var offset = 8;
        var types = new System.Collections.Generic.List<string>();
        while (offset < bytes.Length)
        {
            var type = Encoding.ASCII.GetString(bytes, offset, 4);
            var entryLength = (int)BigEndianUInt32(bytes, offset + 4);
            entryLength.Should().BeGreaterOrEqualTo(8, $"the {type} entry must carry a sane length");
            types.Add(type);
            offset += entryLength;
        }

        types.Should().Contain(new[] { "ic11", "ic07", "ic08", "ic13" },
            "the dock and Finder sizes that matter must be present");

        // The entries are PNG frames: verify the payload signatures and the
        // per-type pixel sizes (ic11=32, ic07=128, ic08=256, ic13=512).
        var expectedSizes = new (string Type, int Size)[] { ("ic11", 32), ("ic07", 128), ("ic08", 256), ("ic13", 512) };
        offset = 8;
        while (offset < bytes.Length)
        {
            var type = Encoding.ASCII.GetString(bytes, offset, 4);
            var entryLength = (int)BigEndianUInt32(bytes, offset + 4);
            var payloadStart = offset + 8;
            var payloadLength = entryLength - 8;
            Encoding.ASCII.GetString(bytes, payloadStart + 1, 3).Should().Be("PNG",
                $"the {type} frame payload must be a PNG");
            var width = (bytes[payloadStart + 16] << 24) | (bytes[payloadStart + 17] << 16) | (bytes[payloadStart + 18] << 8) | bytes[payloadStart + 19];
            var height = (bytes[payloadStart + 20] << 24) | (bytes[payloadStart + 21] << 16) | (bytes[payloadStart + 22] << 8) | bytes[payloadStart + 23];
            var expected = expectedSizes.Single(pair => pair.Type == type);
            width.Should().Be(expected.Size, $"the {type} frame must carry its pixel size");
            height.Should().Be(expected.Size, $"the {type} frame must carry its pixel size");
            payloadLength.Should().BeGreaterThan(0);
            offset += entryLength;
        }
    }

    [Fact]
    public void TheStagedBundle_LintsClean_AndCarriesThePinnedLayout_OnGeneratedTempFiles()
    {
        var root = PackagingScriptRunner.NewTempRoot();
        try
        {
            var publishDir = Path.Combine(root, "publish-osx-arm64");
            PackagingScriptRunner.WriteFakePublishTree(publishDir);
            var outDir = Path.Combine(root, "out");

            var stdout = PackagingScriptRunner.Run(RepoRoot, "scripts/macos-dmg.ps1", outDir, publishDir);

            stdout.Should().Contain("Bundle lint OK",
                "the staging lint must pass on the generated bundle");

            if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
            {
                stdout.Should().Contain("STAGE-ONLY SMOKE OK.",
                    "hdiutil is absent on a Windows box: the local run stays stage-only");
            }

            var bundleRoot = Path.Combine(outDir, "dmg-root", "ZettaTrade.app");
            File.Exists(Path.Combine(bundleRoot, "Contents", "MacOS", "ZettaTrade.App"))
                .Should().BeTrue("the app host must ride Contents/MacOS");
            File.Exists(Path.Combine(bundleRoot, "Contents", "MacOS", "ZettaTrade.App.dll"))
                .Should().BeTrue("the managed assembly must ride Contents/MacOS next to the host");
            File.Exists(Path.Combine(bundleRoot, "Contents", "Resources", "ZettaTrade.icns"))
                .Should().BeTrue("the committed mark asset must ride Contents/Resources");

            var plist = File.ReadAllText(Path.Combine(bundleRoot, "Contents", "Info.plist"));
            plist.Should().Contain("<string>ZettaTrade.App</string>",
                "CFBundleExecutable must name the publish host");
            plist.Should().Contain($"<string>{PropsVersion()}</string>",
                "both version keys must carry the props version verbatim");
            plist.Should().Contain("<string>io.github.zettatrade.ZettaTrade</string>",
                "the bundle identifier must be the pinned one");
            plist.Should().Contain("<string>12.0</string>",
                "LSMinimumSystemVersion must carry the .NET 10 macOS floor");

            // Generated Unix text stays LF-only.
            HasCrBytes(plist).Should().BeFalse("the generated Info.plist must stay LF-only");
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static uint BigEndianUInt32(byte[] bytes, int offset) =>
        ((uint)bytes[offset] << 24) | ((uint)bytes[offset + 1] << 16) | ((uint)bytes[offset + 2] << 8) | bytes[offset + 3];

    private static bool HasCrBytes(string text) => text.Contains('\r');

    private static string PropsVersion()
    {
        var props = File.ReadAllText(Path.Combine(RepoRoot, "Directory.Build.props"));
        var prefix = Regex.Match(props, "<VersionPrefix>([^<>]+)</VersionPrefix>").Groups[1].Value.Trim();
        var suffix = Regex.Match(props, "<VersionSuffix>([^<>]+)</VersionSuffix>").Groups[1].Value.Trim();
        return suffix.Length == 0 ? prefix : $"{prefix}-{suffix}";
    }

    private static void TryDelete(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // A temp cleanup race is not a fact failure.
        }
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
