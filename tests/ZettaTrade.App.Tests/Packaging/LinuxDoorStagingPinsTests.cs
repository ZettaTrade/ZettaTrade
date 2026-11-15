namespace ZettaTrade.App.Tests.Packaging;

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

/// <summary>
/// The T-257 Linux door script pins: both scripts parse the version from
/// the committed props (the pipeline's single source; the .deb additionally
/// pins the plain three-part form), the stage-only honesty is explicit
/// where appimagetool/dpkg-deb are absent, the .desktop entries, the
/// wrapper/AppRun exec chain, the .deb control fields and the PNG icon
/// facts are pinned in the scripts, and two live stage-only runs over a
/// fake publish tree produce the pinned layouts on generated temp files.
/// What is NOT verified locally: the AppImage and .deb builds (plus the
/// runner .deb install smoke) - CI's linux leg carries them.
/// </summary>
public sealed class LinuxDoorStagingPinsTests
{
    private static readonly string RepoRoot = FindRepositoryRoot();

    private static string AppImageScriptText =>
        File.ReadAllText(Path.Combine(RepoRoot, "scripts", "linux-appimage.ps1"));

    private static string DebScriptText =>
        File.ReadAllText(Path.Combine(RepoRoot, "scripts", "linux-deb.ps1"));

    [Fact]
    public void TheAppImageScript_PinsTheDesktopEntry_AndTheExecChain()
    {
        var text = AppImageScriptText;
        text.Should().Contain("Exec=zettatrade",
            "the AppImage desktop entry must target the launcher wrapper");
        text.Should().Contain("Icon=zettatrade",
            "the desktop entry must target the AppDir-root PNG");
        text.Should().Contain("StartupWMClass=ZettaTrade.App",
            "the window-class hint must name the actual host");
        text.Should().Contain("Categories=Office;Finance;",
            "the menu categories must be pinned");
        text.Should().Contain("exec \"$HERE/ZettaTrade.App\" \"$@\"",
            "the wrapper must exec the publish host");
        text.Should().Contain("exec \"$HERE/usr/bin/zettatrade\" \"$@\"",
            "the AppRun must exec the launcher wrapper");
        text.Should().Contain("STAGE-ONLY SMOKE OK.",
            "the script must say plainly that the local run does not claim the build");
        text.Should().Contain("appimagetool was not found on this machine",
            "the limitation must name the missing tool");
        text.Should().Contain("$appImageBytes[10] -ne 0x02",
            "the built AppImage must be asserted against the type-2 magic");
        text.Should().Contain("--appimage-extract-and-run",
            "the FUSE-less retry form must be pinned in the script");
    }

    [Fact]
    public void TheDebScript_PinsTheControlFields_AndTheContractLayout()
    {
        var text = DebScriptText;
        text.Should().Contain("Package: zettatrade",
            "the package name must be pinned");
        text.Should().Contain("Architecture: amd64",
            "the linux-x64 deb must declare amd64");
        text.Should().Contain("Depends: libc6 (>= 2.27), libfontconfig1, libx11-6, libssl3t64 | libssl3",
            "the dependency floor must be pinned");
        text.Should().Contain("Exec=/usr/bin/zettatrade",
            "the installed menu entry must target the public door");
        text.Should().Contain("exec /usr/lib/zettatrade/ZettaTrade.App \"$@\"",
            "the wrapper must exec the host on the contract path");
        text.Should().Contain("usr/share/icons/hicolor/256x256/apps/zettatrade.png",
            "the hicon slot must be pinned");
        text.Should().Contain("^[0-9]+\\.[0-9]+\\.[0-9]+$",
            "the deb Version field must pin the plain three-part props form");
        text.Should().Contain("--root-owner-group",
            "the build must not claim root ownership of the staged tree");
        text.Should().Contain("!<arch>",
            "the built .deb must be asserted against the ar magic");
        text.Should().Contain("debian-binary",
            "the built .deb must be asserted against the debian-binary first member");
        text.Should().Contain("STAGE-ONLY SMOKE OK.",
            "the script must say plainly that the local run does not claim the build");
    }

    [Fact]
    public void TheDebInstalledSize_IsComputed_NeverAGuessedConstant()
    {
        var text = DebScriptText;
        text.Should().Contain("Measure-Object -Property Length -Sum",
            "the Installed-Size field must come from the staged tree");
        text.Should().Contain("@@INSTALLED_SIZE@@",
            "the control template must take the computed size through the placeholder");
    }

    [Fact]
    public void TheCommittedPng_IsA256Square_WithThePinnedSignature()
    {
        var bytes = File.ReadAllBytes(Path.Combine(RepoRoot, "scripts", "assets", "zettatrade-256.png"));
        Encoding.ASCII.GetString(bytes, 1, 3).Should().Be("PNG", "the icon must be a real PNG");
        var width = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
        var height = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
        width.Should().Be(256, "the .desktop icon must be the 256 slot");
        height.Should().Be(256, "the .desktop icon must be the 256 slot");
    }

    [Fact]
    public void TheStagedAppDir_LintsClean_AndCarriesThePinnedLayout_OnGeneratedTempFiles()
    {
        var root = PackagingScriptRunner.NewTempRoot();
        try
        {
            var publishDir = Path.Combine(root, "publish-linux-x64");
            PackagingScriptRunner.WriteFakePublishTree(publishDir);
            var outDir = Path.Combine(root, "out");

            var stdout = PackagingScriptRunner.Run(RepoRoot, "scripts/linux-appimage.ps1", outDir, publishDir);

            stdout.Should().Contain("AppDir lint OK",
                "the staging lint must pass on the generated AppDir");
            if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
            {
                stdout.Should().Contain("STAGE-ONLY SMOKE OK.",
                    "appimagetool is absent on a Windows box: the local run stays stage-only");
            }

            var appDir = Path.Combine(outDir, "AppDir");
            File.Exists(Path.Combine(appDir, "usr", "bin", "ZettaTrade.App"))
                .Should().BeTrue("the app host must ride usr/bin");
            File.Exists(Path.Combine(appDir, "usr", "bin", "ZettaTrade.App.dll"))
                .Should().BeTrue("the managed assembly must ride usr/bin next to the host");
            File.Exists(Path.Combine(appDir, "AppRun")).Should().BeTrue("the entry point must exist");
            File.Exists(Path.Combine(appDir, "zettatrade.desktop")).Should().BeTrue("the desktop entry must exist");
            File.Exists(Path.Combine(appDir, "zettatrade-256.png")).Should().BeTrue("the icon must exist");

            var desktopLines = File.ReadAllLines(Path.Combine(appDir, "zettatrade.desktop"));
            desktopLines[0].Should().Be("[Desktop Entry]");
            desktopLines.Should().Contain("Exec=zettatrade");
            desktopLines.Should().Contain("Icon=zettatrade");
            desktopLines.Should().Contain("Type=Application");
            desktopLines.Should().Contain("Terminal=false");
            desktopLines.Should().Contain("StartupWMClass=ZettaTrade.App");
            desktopLines.Should().Contain("Categories=Office;Finance;");

            var wrapper = File.ReadAllText(Path.Combine(appDir, "usr", "bin", "zettatrade"));
            wrapper.Should().StartWith("#!/bin/sh");
            wrapper.Should().Contain("exec \"$HERE/ZettaTrade.App\" \"$@\"");

            var appRun = File.ReadAllText(Path.Combine(appDir, "AppRun"));
            appRun.Should().StartWith("#!/bin/sh");
            appRun.Should().Contain("exec \"$HERE/usr/bin/zettatrade\" \"$@\"");

            // Generated Unix text stays LF-only (CRLF wrappers break /bin/sh).
            File.ReadAllText(Path.Combine(appDir, "zettatrade.desktop")).Contains('\r')
                .Should().BeFalse("the generated desktop entry must stay LF-only");
            wrapper.Contains('\r').Should().BeFalse("the generated wrapper must stay LF-only");
            appRun.Contains('\r').Should().BeFalse("the generated AppRun must stay LF-only");
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void TheStagedDebTree_LintsClean_AndCarriesThePinnedControl_OnGeneratedTempFiles()
    {
        var root = PackagingScriptRunner.NewTempRoot();
        try
        {
            var publishDir = Path.Combine(root, "publish-linux-x64");
            PackagingScriptRunner.WriteFakePublishTree(publishDir);
            var outDir = Path.Combine(root, "out");

            var stdout = PackagingScriptRunner.Run(RepoRoot, "scripts/linux-deb.ps1", outDir, publishDir);

            stdout.Should().Contain("Deb lint OK",
                "the staging lint must pass on the generated package tree");
            if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
            {
                stdout.Should().Contain("STAGE-ONLY SMOKE OK.",
                    "dpkg-deb is absent on a Windows box: the local run stays stage-only");
            }

            var staging = Path.Combine(outDir, "deb-staging");
            File.Exists(Path.Combine(staging, "DEBIAN", "control"))
                .Should().BeTrue("the control file must exist");
            File.Exists(Path.Combine(staging, "usr", "bin", "zettatrade"))
                .Should().BeTrue("the launcher wrapper must ride usr/bin");
            File.Exists(Path.Combine(staging, "usr", "lib", "zettatrade", "ZettaTrade.App"))
                .Should().BeTrue("the app host must ride the package lib dir");
            File.Exists(Path.Combine(staging, "usr", "lib", "zettatrade", "ZettaTrade.App.dll"))
                .Should().BeTrue("the managed assembly must ride the package lib dir");
            File.Exists(Path.Combine(staging, "usr", "share", "applications", "zettatrade.desktop"))
                .Should().BeTrue("the menu entry must exist");
            File.Exists(Path.Combine(staging, "usr", "share", "icons", "hicolor", "256x256", "apps", "zettatrade.png"))
                .Should().BeTrue("the hicolor icon must exist");

            var control = File.ReadAllText(Path.Combine(staging, "DEBIAN", "control"));
            control.Should().Contain("Package: zettatrade");
            control.Should().Contain($"Version: {PropsVersion()}");
            control.Should().Contain("Architecture: amd64");
            control.Should().Contain("Maintainer: ZettaTrade maintainers <zettatrade@users.noreply.github.com>");
            control.Should().Contain("Depends: libc6 (>= 2.27), libfontconfig1, libx11-6, libssl3t64 | libssl3");
            control.Should().Contain("Description: Multi-exchange desktop trading terminal");
            new Regex(@"^Installed-Size: \d+$", RegexOptions.Multiline).IsMatch(control)
                .Should().BeTrue("the installed size must be a computed number");

            var desktopLines = File.ReadAllLines(Path.Combine(staging, "usr", "share", "applications", "zettatrade.desktop"));
            desktopLines[0].Should().Be("[Desktop Entry]");
            desktopLines.Should().Contain("Exec=/usr/bin/zettatrade");
            desktopLines.Should().Contain("Icon=zettatrade");

            var wrapper = File.ReadAllText(Path.Combine(staging, "usr", "bin", "zettatrade"));
            wrapper.Should().StartWith("#!/bin/sh");
            wrapper.Should().Contain("exec /usr/lib/zettatrade/ZettaTrade.App \"$@\"");

            control.Contains('\r').Should().BeFalse("the generated control must stay LF-only");
            wrapper.Contains('\r').Should().BeFalse("the generated wrapper must stay LF-only");
        }
        finally
        {
            TryDelete(root);
        }
    }

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
