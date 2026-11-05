namespace ZettaTrade.UI.Charting;

using System;
using System.Globalization;
using ZettaTrade.Appearance.Localization;

/// <summary>
/// The culture the chart renders its numeric and date labels in: the APP's
/// UI language (owned by <see cref="LocalizationManager"/>), never the
/// ambient thread culture. The chart draws on Avalonia's render thread,
/// where <c>CultureInfo.CurrentCulture</c> still holds whatever the process
/// started with — an in-app language switch rewrites only the UI thread's
/// culture, so an ambient read there would mix two languages on one screen.
/// </summary>
public static class ChartTickCulture
{
    /// <summary>
    /// Resolve the label culture for one redraw. The manager's language wins
    /// (it is the applied UI language, updated live); without a wired manager
    /// (design mode, previews) the OS UI culture is the best-effort fallback.
    /// <see cref="CultureInfo.GetCultureInfo(string)"/> is the runtime's own
    /// per-name cache: the returned instances are shared and must never be
    /// mutated, and both the lookup and the cross-thread read of the applied
    /// language are safe (a stale read at worst renders one extra old frame
    /// before the language-change invalidation).
    /// </summary>
    public static CultureInfo Resolve()
    {
        if (LocalizationManager.IsInitialized)
        {
            try
            {
                return CultureInfo.GetCultureInfo(LocalizationManager.Instance.CurrentLanguage);
            }
            catch (CultureNotFoundException)
            {
                // An unavailable language code falls through to the OS
                // culture instead of blocking a redraw.
            }
        }

        return CultureInfo.CurrentUICulture;
    }
}
