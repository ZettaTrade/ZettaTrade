namespace ZettaTrade.App.Services;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// One downloadable file the release metadata lists: the GitHub release
/// asset's own name and its <c>browser_download_url</c>. The updater never
/// constructs one from a guess — the URL rides exactly what the releases
/// payload served (the hash is pinned from the release's own sums file,
/// never from the download response).
/// </summary>
/// <param name="Name">The asset's file name, e.g. <c>zettatrade-0.2.2-win-x64.zip</c>.</param>
/// <param name="DownloadUrl">The asset's browser download URL (the HTTPS-only rule checks it).</param>
public sealed record UpdateAsset(string Name, string DownloadUrl);

/// <summary>
/// The release-artifact grammar the updater resolves against (the T-125
/// delivery pipeline's own naming): archives spell the project's shape —
/// the <c>zettatrade-</c> prefix, the release version, the running RID,
/// and the archive's own extension (a <c>zip</c> or a <c>tar.gz</c>,
/// e.g. <c>zettatrade-0.2.2-win-x64.zip</c>) — and the sums file spells
/// the same prefix and version with the <c>-sha256.txt</c> suffix, in the
/// <c>sha256sum -c</c> two-space convention. Every resolution is a strict
/// parse of the names the release metadata actually carried: a name that
/// does not parse, an asset missing for the running RID, or a sums file
/// without the archive's line is an honest refusal — never a guessed
/// substitute (the trading-safety canon).
/// </summary>
public static class UpdateAssetCatalog
{
    #region Constants

    /// <summary>The archive name prefix every release asset of this project carries.</summary>
    private const string NamePrefix = "zettatrade-";

    /// <summary>The sums file's name suffix (its version slot sits right after the prefix).</summary>
    private const string HashFileSuffix = "-sha256.txt";

    /// <summary>The archive extensions the delivery pipeline publishes (T-125: zip on Windows and macOS, tar.gz on Linux).</summary>
    private static readonly string[] ArchiveExtensions = [".zip", ".tar.gz"];

    #endregion Constants

    #region Archive resolution

    /// <summary>
    /// Pick the archive asset the running RID updates from: the asset whose
    /// name parses as this release's archive (its parsed version matches the
    /// release's) and whose RID equals the first RID candidate the platform
    /// named. No match answers null — the staging degrades honestly.
    /// </summary>
    /// <param name="assets">The release's asset list (never null here).</param>
    /// <param name="releaseVersion">The release's display version.</param>
    /// <param name="ridCandidates">The platform's RID spellings, best match first.</param>
    /// <returns>The matching archive asset, or null when none carries this RID.</returns>
    public static UpdateAsset? PickArchive(
        IReadOnlyList<UpdateAsset> assets,
        string releaseVersion,
        IReadOnlyList<string> ridCandidates)
    {
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(releaseVersion);
        ArgumentNullException.ThrowIfNull(ridCandidates);

        var release = UpdateVersionComparer.Parse(releaseVersion);
        if (release is null || ridCandidates.Count == 0)
        {
            return null;
        }

        foreach (var rid in ridCandidates)
        {
            var match = assets.FirstOrDefault(asset =>
                TryParseArchive(asset.Name, out var version, out var assetRid)
                && version.Equals(release.Value)
                && string.Equals(assetRid, rid, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    /// <summary>
    /// Pick the release's own SHA-256 sums file: the asset named
    /// <c>zettatrade-</c> + the release's display version +
    /// <c>-sha256.txt</c>. The hash is pinned from THIS file — never from
    /// the download response, never from a guessed URL.
    /// </summary>
    public static UpdateAsset? PickHashFile(IReadOnlyList<UpdateAsset> assets, string releaseVersion)
    {
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(releaseVersion);

        return assets.FirstOrDefault(asset =>
            string.Equals(asset.Name, $"{NamePrefix}{releaseVersion}{HashFileSuffix}", StringComparison.Ordinal));
    }

    /// <summary>
    /// Parse one archive asset name into its parts: the version (parsed with
    /// the same comparer the checker ranks by — a name whose version does not
    /// parse is not an archive of this project) and the RID. The extension
    /// must be one of the published spellings (<c>zip</c>, <c>tar.gz</c>).
    /// </summary>
    public static bool TryParseArchive(string name, out UpdateVersion version, out string rid)
    {
        version = default;
        rid = string.Empty;

        if (string.IsNullOrWhiteSpace(name) || !name.StartsWith(NamePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var body = name[NamePrefix.Length..];
        var extension = ArchiveExtensions.FirstOrDefault(body.EndsWith);
        if (extension is null)
        {
            return false;
        }

        var withoutExtension = body[..^extension.Length];

        // The version and the RID both carry hyphens (a pre-release version
        // is "0.1.0-alpha.159", a RID is "win-x64"), so the separator is a
        // hyphen position whose left side round-trips as a version and whose
        // right side is a RID spelling — scanned from the right, so a
        // pre-release suffix stays on the version side (the grammar's own
        // shape), never glued to the RID.
        for (var separator = withoutExtension.LastIndexOf('-');
             separator > 0;
             separator = withoutExtension.LastIndexOf('-', separator - 1))
        {
            var ridCandidate = withoutExtension[(separator + 1)..];
            var versionText = withoutExtension[..separator];
            var parsed = UpdateVersionComparer.Parse(versionText);
            if (parsed is null
                || !parsed.Value.ToDisplayString().Equals(versionText, StringComparison.Ordinal)
                || !IsRidShape(ridCandidate))
            {
                continue;
            }

            version = parsed.Value;
            rid = ridCandidate;
            return true;
        }

        return false;
    }

    /// <summary>The architecture tokens the .NET RID grammar's tail publishes.</summary>
    private static readonly HashSet<string> ArchitectureNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "x64",
        "x86",
        "arm",
        "arm64",
        "riscv64",
        "s390x",
        "ppc64le",
        "loongarch64",
    };

    /// <summary>
    /// Whether one string is a RID spelling: hyphen-separated tokens whose
    /// last one names a .NET architecture (the RID grammar's own tail —
    /// <c>win-x64</c>, <c>linux-musl-arm64</c>). The architecture family is
    /// the closed set the runtime itself publishes; anything else
    /// (<c>unknown-rid</c>) is not a RID spelling and never matches an
    /// artifact.
    /// </summary>
    private static bool IsRidShape(string rid)
    {
        var tokens = rid.Split('-');
        return tokens.Length >= 2 && ArchitectureNames.Contains(tokens[^1]);
    }

    /// <summary>Whether one asset name is the archive family (the hash file is not).</summary>
    public static bool IsArchiveName(string name) =>
        TryParseArchive(name, out _, out _);

    #endregion Archive resolution

    #region The sums file

    /// <summary>
    /// The hash the sums file lists for ONE file name, in the
    /// <c>sha256sum -c</c> two-space convention: the hex digest, two
    /// spaces, the file name — one line per artifact, comment lines
    /// (<c>#</c>) skipped. A file not listed or a malformed line answers
    /// null — the caller refuses to stage without an expected hash, never
    /// substitutes one.
    /// </summary>
    /// <param name="hashFileText">The sums file's whole text (any line ending).</param>
    /// <param name="assetName">The file name the expected hash is looked up by.</param>
    public static string? ExpectedHashFor(string hashFileText, string assetName)
    {
        if (string.IsNullOrEmpty(hashFileText) || string.IsNullOrEmpty(assetName))
        {
            return null;
        }

        foreach (var rawLine in hashFileText.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            // The two-space convention: exactly one separator run between the
            // hash and the name, and the name carries no spaces itself — the
            // artifact names never do (the T-125 grammar).
            var separator = line.IndexOf("  ", StringComparison.Ordinal);
            if (separator != 64)
            {
                continue;
            }

            var hash = line[..separator];
            var listedName = line[(separator + 2)..].Trim();
            if (!IsSha256Hex(hash) || !string.Equals(listedName, assetName, StringComparison.Ordinal))
            {
                continue;
            }

            return hash;
        }

        return null;
    }

    /// <summary>Whether a staged download's SHA-256 matches the release's expected hash (hex, case-insensitive).</summary>
    public static bool HashMatches(string? expected, string? actual) =>
        !string.IsNullOrEmpty(expected)
        && !string.IsNullOrEmpty(actual)
        && IsSha256Hex(expected)
        && IsSha256Hex(actual)
        && string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether one string is a well-formed 64-hex-digit SHA-256 spelling.</summary>
    private static bool IsSha256Hex(string text) =>
        text.Length == 64 && text.All(static c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F'));

    #endregion The sums file

    #region Hashing

    /// <summary>Compute one file's SHA-256 as lowercase hex (the sums file's own spelling).</summary>
    public static async Task<string> ComputeSha256HexAsync(string filePath, CancellationToken ct = default)
    {
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    #endregion Hashing
}
