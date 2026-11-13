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

    /// <inheritdoc/>
    public void OpenInBrowser(string url)
    {
        // The honest guard: only the web schemes the shell may resolve.
        // Anything else (a file://, a custom protocol, garbage) is
        // refused here, never handed to the OS.
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("Only an absolute http(s) URL opens in the browser.", nameof(url));
        }

        // Shell execution resolves the platform's own open path for the
        // URL scheme (ShellExecute on Windows, the open/xdg-open helpers
        // on macOS/Linux) — the browser choice stays the OS default's.
        Process.Start(new ProcessStartInfo
        {
            UseShellExecute = true,
            FileName = uri.AbsoluteUri,
        });
    }
}
