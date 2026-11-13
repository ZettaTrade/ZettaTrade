namespace ZettaTrade.Infrastructure.Shell;

/// <summary>
/// Abstraction over platform shell operations. Platform selection stays in
/// the Infrastructure implementation; other layers only ask for a location
/// to open, never for a specific process.
/// </summary>
public interface IShellUtils
{
    /// <summary>
    /// Open a file system location with the operating system file manager.
    /// </summary>
    /// <param name="path">Directory (or file) path to open.</param>
    void OpenInFileManager(string path);

    /// <summary>
    /// Open a web URL with the operating system's default browser (the
    /// update announcement's release page route, T-213). Only http and
    /// https URLs are accepted: the shell never executes an arbitrary
    /// string that arrived over the network.
    /// </summary>
    /// <param name="url">The absolute http(s) URL to open.</param>
    void OpenInBrowser(string url);
}
