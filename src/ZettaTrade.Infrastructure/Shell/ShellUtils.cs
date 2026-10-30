namespace ZettaTrade.Infrastructure.Shell;

using System.Diagnostics;

/// <summary>
/// Cross-platform launch of the operating system file manager:
/// explorer.exe on Windows, open on macOS and xdg-open on Linux — started
/// through <see cref="ProcessStartInfo"/> with shell execution, so PATH
/// lookup and the platform open conventions stay with the OS.
/// </summary>
public sealed class ShellUtils : IShellUtils
{
    /// <inheritdoc/>
    public void OpenInFileManager(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = true,
        };

        if (OperatingSystem.IsWindows())
        {
            startInfo.FileName = "explorer.exe";
        }
        else if (OperatingSystem.IsMacOS())
        {
            startInfo.FileName = "open";
        }
        else if (OperatingSystem.IsLinux())
        {
            startInfo.FileName = "xdg-open";
        }
        else
        {
            throw new PlatformNotSupportedException(
                $"{nameof(OpenInFileManager)} is not supported on this platform.");
        }

        // ArgumentList quotes the path itself; a location with spaces needs
        // no manual escaping.
        startInfo.ArgumentList.Add(path);
        Process.Start(startInfo);
    }
}
