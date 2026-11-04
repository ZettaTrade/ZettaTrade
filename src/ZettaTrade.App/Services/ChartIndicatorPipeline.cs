namespace ZettaTrade.App.Services;

using Microsoft.Extensions.Logging;
using ZettaTrade.Charting;
using ZettaTrade.Core.Entities;

/// <summary>
/// The chart indicator compute pipeline: turns the persisted indicator rows
/// and the loaded candle series into the drawable overlay lines. Marshal
/// -free and pure apart from logging: it runs wherever its callers land and
/// every unusable row is skipped with a warning, never clamped or defaulted.
/// </summary>
public static class ChartIndicatorPipeline
{
    /// <summary>
    /// Compute the overlay lines for the given bars: every enabled settings
    /// row resolves through the catalog and applies to the full series (the
    /// draw pass does the window slicing). Rows the parse reports are
    /// logged and skipped; rows the catalog cannot resolve skip quietly (a
    /// missing plugin is recorded at the scan, not per compute).
    /// </summary>
    public static IReadOnlyList<ChartIndicatorLine> Compute(
        IReadOnlyList<Bar> bars,
        string? indicatorSettingsJson,
        IIndicatorCatalog catalog,
        ILogger logger)
    {
        if (bars.Count == 0)
        {
            return [];
        }

        var (configs, problems) = ChartIndicatorConfig.ParseList(indicatorSettingsJson);
        foreach (var problem in problems)
        {
            logger.LogWarning("The chart indicator settings row is skipped: {Problem}", problem);
        }

        List<ChartIndicatorLine> lines = [];
        foreach (var config in configs)
        {
            if (!config.Enabled)
            {
                continue;
            }

            var indicator = catalog.Create(config.Name, config.Period);
            if (indicator is null)
            {
                continue;
            }

            lines.Add(new ChartIndicatorLine(indicator.Name, indicator.Apply(bars)));
        }

        return lines;
    }
}
