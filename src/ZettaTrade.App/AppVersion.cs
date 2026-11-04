namespace ZettaTrade.App;

using System.Reflection;

/// <summary>
/// The running application version, resolved from the exe's
/// <see cref="AssemblyInformationalVersionAttribute"/> — MSBuild bakes
/// <c>Directory.Build.props</c> <c>$(Version)</c> (<c>0.1.0-alpha.N</c>) into
/// it. Shown in the status bar.
/// </summary>
public static class AppVersion
{
    #region Constants

    /// <summary>
    /// Fallback when the informational version is missing (never expected for
    /// the App assembly, but the chip must never be blank).
    /// </summary>
    private const string UnknownVersion = "unknown";

    #endregion

    #region Properties

    /// <summary>
    /// The application version for display, e.g. <c>0.1.0-alpha.12</c>. The
    /// read is resolved once per process.
    /// </summary>
    public static string Current { get; } = ReadEntryVersion();

    #endregion

    #region Public Methods

    /// <summary>
    /// Trim the informational version for display: the SDK appends a
    /// plus sign and the git commit as build metadata in repositories,
    /// which is noise for a chip — keep the semantic version only.
    /// </summary>
    /// <param name="informationalVersion">Raw informational version or null.</param>
    /// <returns>The display version, never null or empty.</returns>
    public static string ToDisplayVersion(string? informationalVersion) =>
        string.IsNullOrEmpty(informationalVersion)
            ? UnknownVersion
            : informationalVersion.Split('+')[0];

    #endregion

    #region Private Methods

    private static string ReadEntryVersion() =>
        ToDisplayVersion(
            Assembly.GetEntryAssembly()
                ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion);

    #endregion
}
