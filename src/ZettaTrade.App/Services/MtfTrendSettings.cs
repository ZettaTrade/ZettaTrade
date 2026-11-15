namespace ZettaTrade.App.Services;

using System;
using System.Threading;
using System.Threading.Tasks;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// The screener trend classifier's tuning (T-239): the EMA slope period,
/// the Wilder ADX period and the ADX trend threshold. The stored values
/// ride the UI section (<see cref="UiSettingsKeys"/>); a missing or
/// out-of-range stored value clamps into its allowed range — never an
/// exception, never a fabricated zero period (a period below the
/// classifier's floor would throw on every pass instead of degrading).
/// </summary>
/// <param name="EmaPeriod">The EMA slope period in bars (2..200), 20 by default.</param>
/// <param name="AdxPeriod">The Wilder ADX smoothing period in bars (2..100), 14 by default.</param>
/// <param name="AdxThreshold">The ADX level (1..100) a series must reach for a directional state, 25 by default.</param>
public sealed record MtfTrendSettings(int EmaPeriod, int AdxPeriod, decimal AdxThreshold)
{
    /// <summary>The shipped defaults: the classic 20-period EMA, the 14-period ADX at the 25 level.</summary>
    public static MtfTrendSettings Default { get; } = new(20, 14, 25m);

    /// <summary>The EMA period's allowed floor.</summary>
    public const int EmaPeriodMin = 2;

    /// <summary>The EMA period's allowed ceiling.</summary>
    public const int EmaPeriodMax = 200;

    /// <summary>The ADX period's allowed floor.</summary>
    public const int AdxPeriodMin = 2;

    /// <summary>The ADX period's allowed ceiling.</summary>
    public const int AdxPeriodMax = 100;

    /// <summary>The ADX threshold's allowed floor.</summary>
    public const decimal AdxThresholdMin = 1m;

    /// <summary>The ADX threshold's allowed ceiling.</summary>
    public const decimal AdxThresholdMax = 100m;

    /// <summary>
    /// Load the tuning from the settings store: each value clamps into its
    /// allowed range, so an absent or corrupted entry degrades to the
    /// boundary instead of breaking every pass.
    /// </summary>
    /// <param name="settings">The settings service (the UI section's single source of truth).</param>
    /// <param name="ct">The load's cancellation token.</param>
    /// <returns>The loaded tuning, every member inside its range.</returns>
    public static async Task<MtfTrendSettings> LoadAsync(ISettingsService settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var emaPeriod = Clamp(
            await settings.GetAsync<int>(UiSettingsKeys.Section, UiSettingsKeys.MtfTrendEmaPeriod, Default.EmaPeriod),
            EmaPeriodMin,
            EmaPeriodMax);
        var adxPeriod = Clamp(
            await settings.GetAsync<int>(UiSettingsKeys.Section, UiSettingsKeys.MtfTrendAdxPeriod, Default.AdxPeriod),
            AdxPeriodMin,
            AdxPeriodMax);
        var adxThreshold = Clamp(
            await settings.GetAsync<decimal>(UiSettingsKeys.Section, UiSettingsKeys.MtfTrendAdxThreshold, Default.AdxThreshold),
            AdxThresholdMin,
            AdxThresholdMax);

        return new(emaPeriod, adxPeriod, adxThreshold);
    }

    /// <summary>Clamp one value into its allowed range — the honest boundary, never a silent substitute.</summary>
    private static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;

    /// <summary>Clamp one value into its allowed range — the honest boundary, never a silent substitute.</summary>
    private static decimal Clamp(decimal value, decimal min, decimal max) => value < min ? min : value > max ? max : value;
}
