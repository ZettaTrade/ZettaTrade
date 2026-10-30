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
}
