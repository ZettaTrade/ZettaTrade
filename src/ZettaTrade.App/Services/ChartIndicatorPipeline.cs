namespace ZettaTrade.App.Services;

using Microsoft.Extensions.Logging;
using ZettaTrade.Charting;
using ZettaTrade.Core.Entities;
using ZettaTrade.Plugins.Abstractions;

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
        IndicatorBar[]? contractBars = null;
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

            // The single adaptation site (T-163): the app's candle entity
            // maps to the SDK-owned IndicatorBar shape once per compute -
            // and only when a usable indicator exists - so the plugins
            // never see the application's internal Bar.
            contractBars ??= MapContractBars(bars);

            // The pane declaration rides the descriptor (T-199, contract
            // 2.2.0): the kind, the optional fixed scale bounds and the
            // series flow together into the drawable line - a pre-2.2.0
            // implementation answers the defaults and stays an overlay.
            lines.Add(new ChartIndicatorLine(
                indicator.Name,
                indicator.Apply(contractBars),
                indicator.PaneKind,
                indicator.ScaleMin,
                indicator.ScaleMax));
        }

        return lines;
    }

    /// <summary>
    /// Map the app's bars to the contract bars once per compute: the OHLC
    /// prices carry over, the entity-only time and volume stay behind (the
    /// contract 2.0.0 shape is time-free).
    /// </summary>
    private static IndicatorBar[] MapContractBars(IReadOnlyList<Bar> bars)
    {
        var contract = new IndicatorBar[bars.Count];
        for (var i = 0; i < bars.Count; i++)
        {
            var bar = bars[i];
            contract[i] = new IndicatorBar(bar.Open, bar.High, bar.Low, bar.Close);
        }

        return contract;
    }
}
