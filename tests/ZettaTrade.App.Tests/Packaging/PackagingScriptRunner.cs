namespace ZettaTrade.App.Tests.Packaging;

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using FluentAssertions;

/// <summary>
/// Runs one T-257 packaging script (stage + lint; the .dmg/AppImage/.deb
/// builds stay CI-only where the OS tools are absent) in a fresh
/// interpreter process and asserts a clean, passing exit.
/// </summary>
internal static class PackagingScriptRunner
{
    public static string Run(string repoRoot, string scriptRelativePath, string outDir, string publishDir)
    {
        var scriptPath = Path.Combine(repoRoot, scriptRelativePath.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(scriptPath).Should().BeTrue($"the packaging script must exist: {scriptRelativePath}");

        var interpreter = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "powershell.exe" : "pwsh";
        var start = new ProcessStartInfo
        {
            FileName = interpreter,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-ExecutionPolicy");
        start.ArgumentList.Add("Bypass");
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(scriptPath);
        start.ArgumentList.Add("-OutDir");
        start.ArgumentList.Add(outDir);
        start.ArgumentList.Add("-PublishDir");
        start.ArgumentList.Add(publishDir);

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("The PowerShell interpreter could not be started.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(180_000).Should().BeTrue("the packaging staging smoke must finish");
        process.ExitCode.Should().Be(0, $"the staging smoke must pass cleanly. stdout:\n{stdout}\nstderr:\n{stderr}");
        return stdout;
    }

    public static string NewTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "zt-t257-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    public static void WriteFakePublishTree(string directory)
    {
        Directory.CreateDirectory(directory);
        // The staging lints pin structure, not content: a minimal tree with
        // the two names every script requires is enough for a stage-only
        // run (the embedded-version smoke is the release-dry-run.ps1 job,
        // which the gate evidence runs against a real publish).
        File.WriteAllBytes(Path.Combine(directory, "ZettaTrade.App"), Array.Empty<byte>());
        File.WriteAllBytes(Path.Combine(directory, "ZettaTrade.App.dll"), Array.Empty<byte>());
    }
}
