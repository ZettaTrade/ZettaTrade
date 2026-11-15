namespace ZettaTrade.App.Services;

using System;

/// <summary>The update channel a release may ride (the T-258 settings switch).</summary>
public enum UpdateChannelKind
{
    /// <summary>Only released stable versions count (no pre-release identifier).</summary>
    Stable = 0,

    /// <summary>Every release counts, pre-releases included (the beta posture).</summary>
    Beta = 1,
}

/// <summary>
/// The channel switch's vocabulary and rule: the stored spelling is the
/// canonical lowercase name, and the membership rule is semver's own — the
/// stable channel offers only released stable versions (no pre-release
/// identifier), the beta channel offers every parsable release, pre-releases
/// included. An unknown stored spelling never substitutes a channel
/// silently: the reader repairs to the default with a logged warning (the
/// settings-pick discipline the theme and AI-provider picks keep).
/// </summary>
public static class UpdateChannel
{
    #region Constants

    /// <summary>The stable channel's stored spelling.</summary>
    public const string StableName = "stable";

    /// <summary>The beta channel's stored spelling.</summary>
    public const string BetaName = "beta";

    #endregion Constants

    #region Public Methods

    /// <summary>Parse one stored channel spelling; the canonical names match case-insensitively.</summary>
    public static bool TryParse(string? stored, out UpdateChannelKind channel)
    {
        channel = UpdateChannelKind.Stable;
        if (string.IsNullOrWhiteSpace(stored))
        {
            return false;
        }

        var pick = stored.Trim();
        if (pick.Equals(StableName, StringComparison.OrdinalIgnoreCase))
        {
            channel = UpdateChannelKind.Stable;
            return true;
        }

        if (pick.Equals(BetaName, StringComparison.OrdinalIgnoreCase))
        {
            channel = UpdateChannelKind.Beta;
            return true;
        }

        return false;
    }

    /// <summary>The stored spelling one channel writes to the settings (the canonical lowercase name).</summary>
    public static string ToSettingName(UpdateChannelKind channel) => channel switch
    {
        UpdateChannelKind.Beta => BetaName,
        _ => StableName,
    };

    /// <summary>
    /// The membership rule: whether a release with this pre-release kind may
    /// offer itself on the channel. Stable offers released stables only; beta
    /// offers everything parsable.
    /// </summary>
    public static bool Offers(UpdateChannelKind channel, PreReleaseKind preKind) =>
        channel == UpdateChannelKind.Beta || preKind == PreReleaseKind.None;

    #endregion Public Methods
}
