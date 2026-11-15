namespace ZettaTrade.App.Services;

using System;
using System.Collections.Generic;
using System.Globalization;

/// <summary>
/// One discovered release the update checker may announce: the remote
/// version's display label (the parsed tag without any build metadata),
/// the release page URL the notification routes to, and the release's
/// own asset list the auto-update stages from (T-258). A value the
/// checker emits is always a REAL release the endpoint returned — never
/// a fabricated placeholder.
/// </summary>
/// <param name="Version">The release's display version, e.g. <c>0.1.0-alpha.159</c>.</param>
/// <param name="Url">The release's HTML page URL (the release link the notification carries).</param>
/// <param name="Assets">The release's downloadable assets (name + browser URL), exactly as the payload carried them; null when it listed none.</param>
public sealed record UpdateRelease(string Version, string Url, IReadOnlyList<UpdateAsset>? Assets = null);

/// <summary>
/// The pre-release-aware version comparison the update check rides
/// (T-213): the running and the remote spellings are semantic versions
/// with a numeric alpha suffix (<c>0.1.0-alpha.158</c>, the
/// <c>Directory.Build.props</c> shape), which <see cref="Version"/>
/// cannot parse — the comparison is done here, by hand, with the semver
/// pre-release rule: a version without a pre-release identifier
/// outranks one with it, and two pre-releases rank by their numeric
/// alpha number (never by the string, where <c>9</c> beats <c>10</c>).
/// Unparsable tags yield no version at all — the checker skips them,
/// never guesses.
/// </summary>
public static class UpdateVersionComparer
{
    #region Public Methods

    /// <summary>Parse one version string into its comparable shape; null when it is not a version.</summary>
    /// <param name="text">The raw tag or version string (one leading <c>v</c> and build metadata tolerated).</param>
    /// <returns>The parsed version, or null for an unparsable string.</returns>
    public static UpdateVersion? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var trimmed = text.Trim();
        if (trimmed.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[1..];
        }

        // Build metadata (+git sha) never ranks: the SDK appends it in
        // repositories, the display and comparison shapes live without it.
        var withoutMetadata = trimmed.Split('+')[0];
        if (withoutMetadata.Length == 0)
        {
            return null;
        }

        var parts = withoutMetadata.Split('-');
        var core = parts[0].Split('.');
        if (core.Length is 0 or > 3)
        {
            return null;
        }

        var numbers = new int[3];
        for (var i = 0; i < core.Length; i++)
        {
            if (!int.TryParse(core[i], NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            {
                return null;
            }

            numbers[i] = number;
        }

        // The pre-release shapes this project ships (and the only ones the
        // checker needs to rank honestly): "alpha.N" and "beta.N" with a
        // numeric N. Any other shape stays unparsable — the checker skips
        // the tag.
        if (parts.Length == 1)
        {
            return new UpdateVersion(numbers[0], numbers[1], numbers[2], PreReleaseKind.None, 0);
        }

        PreReleaseKind kind;
        var numberText = parts[1];
        if (numberText.StartsWith("alpha.", StringComparison.Ordinal))
        {
            kind = PreReleaseKind.Alpha;
            numberText = numberText["alpha.".Length..];
        }
        else if (numberText.StartsWith("beta.", StringComparison.Ordinal))
        {
            kind = PreReleaseKind.Beta;
            numberText = numberText["beta.".Length..];
        }
        else
        {
            return null;
        }

        if (numberText.Length == 0
            || !int.TryParse(numberText, NumberStyles.None, CultureInfo.InvariantCulture, out var preReleaseNumber))
        {
            return null;
        }

        return new UpdateVersion(numbers[0], numbers[1], numbers[2], kind, preReleaseNumber);
    }

    /// <summary>Whether <paramref name="candidate"/> ranks strictly above <paramref name="current"/> (both must parse).</summary>
    public static bool IsNewer(string? current, string? candidate) =>
        Parse(candidate) is { } candidateVersion
        && Parse(current) is { } currentVersion
        && candidateVersion.CompareTo(currentVersion) > 0;

    #endregion Public Methods
}

/// <summary>The pre-release identifier family a parsed version carries.</summary>
public enum PreReleaseKind
{
    /// <summary>No pre-release identifier (a stable version).</summary>
    None = 0,

    /// <summary>An <c>alpha.N</c> identifier with the numeric suffix carried alongside.</summary>
    Alpha = 1,

    /// <summary>A <c>beta.N</c> identifier with the numeric suffix carried alongside.</summary>
    Beta = 2,
}

/// <summary>
/// One parsed semantic version in comparable shape: the three core
/// numbers, the pre-release kind and the pre-release number. The semver
/// rule lives in <see cref="CompareTo"/>: a stable outranks any
/// pre-release of the same core, two pre-releases rank by their kind (a
/// beta outranks an alpha) and then by their number.
/// </summary>
/// <param name="Major">The major number.</param>
/// <param name="Minor">The minor number.</param>
/// <param name="Patch">The patch number.</param>
/// <param name="PreKind">The pre-release identifier family (None for a stable).</param>
/// <param name="PreReleaseNumber">The numeric pre-release suffix; zero when stable.</param>
public readonly record struct UpdateVersion(
    int Major,
    int Minor,
    int Patch,
    PreReleaseKind PreKind,
    int PreReleaseNumber) : IComparable<UpdateVersion>
{
    /// <summary>The semver ordering: the core numbers first, then the pre-release rule.</summary>
    public int CompareTo(UpdateVersion other)
    {
        if (Major != other.Major)
        {
            return Major.CompareTo(other.Major);
        }

        if (Minor != other.Minor)
        {
            return Minor.CompareTo(other.Minor);
        }

        if (Patch != other.Patch)
        {
            return Patch.CompareTo(other.Patch);
        }

        // The core ties: a stable outranks every pre-release; two
        // pre-releases of the same core rank by their kind (a beta
        // outranks an alpha) and then by the numeric suffix (never by
        // the string, where 9 beats 10).
        var thisIsStable = PreKind == PreReleaseKind.None;
        var otherIsStable = other.PreKind == PreReleaseKind.None;
        if (thisIsStable != otherIsStable)
        {
            return thisIsStable ? 1 : -1;
        }

        if (thisIsStable)
        {
            return 0;
        }

        if (PreKind != other.PreKind)
        {
            return PreKind.CompareTo(other.PreKind);
        }

        return PreReleaseNumber.CompareTo(other.PreReleaseNumber);
    }

    /// <summary>
    /// The display spelling the announcement carries: the core trio, the
    /// pre-release suffix restored (e.g. <c>0.1.0-alpha.159</c>, <c>1.0.0-beta.2</c>)
    /// — the same shape <see cref="AppVersion.Current"/> prints, never the
    /// tag's private decorations.
    /// </summary>
    public string ToDisplayString() => PreKind switch
    {
        PreReleaseKind.Alpha => string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}-alpha.{PreReleaseNumber}"),
        PreReleaseKind.Beta => string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}-beta.{PreReleaseNumber}"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}"),
    };
}
