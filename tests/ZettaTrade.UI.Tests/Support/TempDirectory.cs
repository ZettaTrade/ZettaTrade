namespace ZettaTrade.UI.Tests.Support;

using System.IO;

/// <summary>
/// A single-use temp folder for test files, deleted together with the run.
/// </summary>
internal sealed class TempDirectory : IDisposable
{
    /// <summary>
    /// Create a unique temp directory under the user's temp path.
    /// </summary>
    public TempDirectory()
    {
        FolderPath = Path.Combine(Path.GetTempPath(), $"zettatrade-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(FolderPath);
    }

    /// <summary>Full path of the created directory.</summary>
    public string FolderPath { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(FolderPath, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // Already gone: removal is best effort.
        }
    }
}
