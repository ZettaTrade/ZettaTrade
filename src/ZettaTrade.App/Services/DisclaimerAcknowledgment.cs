namespace ZettaTrade.App.Services;

using System.Threading.Tasks;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// The persisted acknowledgment of the first-run disclaimer. The settings
/// store is the single source of truth: the launch check reads the saved
/// version fresh (no cached copy for the settings hot-reload to fight) and
/// the acknowledge writes the shipped disclaimer's version; a future
/// wording update bumps the shipped version, and every previously
/// acknowledged install re-shows the dialog exactly once.
/// </summary>
public static class DisclaimerAcknowledgment
{
    /// <summary>
    /// Whether an acknowledged disclaimer version covers the wording shipped
    /// by this build (the shipped version, or a later one acknowledged by a
    /// newer build of the same wording).
    /// </summary>
    /// <param name="acknowledgedVersion">The version the user's acknowledge covers.</param>
    public static bool CoversCurrent(int acknowledgedVersion) =>
        acknowledgedVersion >= UiSettingsKeys.DisclaimerCurrentVersion;

    /// <summary>
    /// Read the acknowledged disclaimer version from the settings store.
    /// </summary>
    /// <param name="settings">The settings store of the running app.</param>
    public static Task<int> ReadAcknowledgedVersionAsync(ISettingsService settings) =>
        settings.GetAsync(
            UiSettingsKeys.Section,
            UiSettingsKeys.DisclaimerVersion,
            UiSettingsKeys.DefaultDisclaimerVersion);

    /// <summary>
    /// Persist the shipped disclaimer version as acknowledged.
    /// </summary>
    /// <param name="settings">The settings store of the running app.</param>
    public static Task AcknowledgeAsync(ISettingsService settings) =>
        settings.SetAsync(
            UiSettingsKeys.Section,
            UiSettingsKeys.DisclaimerVersion,
            UiSettingsKeys.DisclaimerCurrentVersion);
}
