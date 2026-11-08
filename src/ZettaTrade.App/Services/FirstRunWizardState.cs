namespace ZettaTrade.App.Services;

using System.Threading.Tasks;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// The persisted once-flag of the first-run wizard. The settings store is
/// the single source of truth: the launch check reads the flag fresh (no
/// cached copy for the settings hot-reload to fight) and every wizard exit —
/// finish, skip or a plain window close — writes it through one seam, so a
/// skipped wizard also never re-announces. Re-running the wizard is a
/// deliberate choice made in the settings window, which never touches this
/// flag.
/// </summary>
public static class FirstRunWizardState
{
    /// <summary>
    /// Read the wizard announcement flag from the settings store.
    /// </summary>
    /// <param name="settings">The settings store of the running app.</param>
    public static Task<bool> ReadShownAsync(ISettingsService settings) =>
        settings.GetAsync(
            UiSettingsKeys.Section,
            UiSettingsKeys.FirstRunWizardShown,
            UiSettingsKeys.DefaultFirstRunWizardShown);

    /// <summary>
    /// Whether the wizard should announce this launch: the flag absents on a
    /// fresh install (or an externally reset settings file) and exists after
    /// any wizard exit — the wizard shown once is never re-announced.
    /// </summary>
    /// <param name="settings">The settings store of the running app.</param>
    public static async Task<bool> ShouldBeShownAsync(ISettingsService settings) =>
        !await ReadShownAsync(settings);

    /// <summary>
    /// Persist the once-flag. Called after a completed wizard exit on the
    /// first-run launch path only: a first-run exit by ANY route (finish,
    /// skip, close) marks the announcement, and the mark lands exactly once
    /// per launch.
    /// </summary>
    /// <param name="settings">The settings store of the running app.</param>
    public static Task MarkShownAsync(ISettingsService settings) =>
        settings.SetAsync(
            UiSettingsKeys.Section,
            UiSettingsKeys.FirstRunWizardShown,
            true);
}
