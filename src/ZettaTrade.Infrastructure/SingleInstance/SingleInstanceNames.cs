namespace ZettaTrade.Infrastructure.SingleInstance;

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using ZettaTrade.Infrastructure.Configuration;

/// <summary>
/// The well-known dispatch identifiers, composed once from the resolved
/// profile paths. Both identifiers are per-user by construction (they hash
/// the profile's config directory), so two different users never release
/// each other's files or pipes, and two different applications never share
/// a namespace. The handoff pipe name never carries raw path characters —
/// a hash of the config root stays a safe named-object name on Windows,
/// macOS and Linux alike.
/// </summary>
public static class SingleInstanceNames
{
    /// <summary>The profile-agnostic base name of the claim file.</summary>
    private const string LockFileName = "single-instance.lock";

    /// <summary>The handoff pipe name prefix (the app's namespace claim).</summary>
    private const string PipeNamePrefix = "zettatrade-handoff-";

    /// <summary>How much hex of the SHA-256 profile hash the pipe name carries.</summary>
    private const int PipeNameHashCharacters = 16;

    /// <summary>Path of the claim file in the resolved data directory.</summary>
    public static string LockFilePath => Path.Combine(ConfigPathResolver.DataDirectory, LockFileName);

    /// <summary>
    /// Name of the handoff pipe: the app prefix plus a per-profile hash, so
    /// the product's own future sibling profiles cannot cross-contaminate.
    /// </summary>
    public static string PipeName => PipeNamePrefix + HashProfile(ConfigPathResolver.ConfigDirectory);

    /// <summary>Stable lowercase hex digest of a path (the name-composition step).</summary>
    private static string HashProfile(string profileRoot)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(profileRoot));
        return Convert.ToHexString(digest)[..PipeNameHashCharacters].ToLowerInvariant();
    }
}
