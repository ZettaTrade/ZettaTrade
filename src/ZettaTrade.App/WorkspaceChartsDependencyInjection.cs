namespace ZettaTrade.App;

using Livery.Contracts;
using Livery.Localization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZettaTrade.App.Services;
using ZettaTrade.App.ViewModels;
using ZettaTrade.Charting;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Infrastructure.Export;
using ZettaTrade.Infrastructure.Indicators;
using ZettaTrade.Infrastructure.Persistence;
using ZettaTrade.Strategies;

/// <summary>
/// DI wiring for the multi-chart grid (T-201): the workspace's layout-mode
/// ViewModel and its lazy slot factory. The slots ride full chart
/// ViewModels — the factory duplicates the primary singleton's resolution
/// list with the slot number appended, so every slot shares the same
/// services (the relay, the rail, the footprint feed) without sharing the
/// primary's persistence keys.
/// </summary>
public static class WorkspaceChartsDependencyInjection
{
    /// <summary>
    /// Register the chart layout ViewModel (IAsyncInitializable: the
    /// startup walk restores the layout mode and the seam sizes before any
    /// window opens, and a restored tiled layout creates its slots right
    /// there — their init walks ride the ViewModel's serialized queue).
    /// Registered AFTER the chart ViewModel: the factory closes over the
    /// same seams, and registration keeps the one-liner beside it.
    /// </summary>
    public static IServiceCollection AddWorkspaceChartsServices(this IServiceCollection services) =>
        services.AddSingleton<WorkspaceChartsViewModel>(sp =>
            new WorkspaceChartsViewModel(
                sp.GetRequiredService<ISettingsService>(),
                slotId => new ChartViewModel(
                    sp.GetRequiredService<ActiveExchangeService>(),
                    sp.GetRequiredService<SymbolListProvider>(),
                    sp.GetRequiredService<ISettingsService>(),
                    sp.GetRequiredService<IIndicatorCatalog>(),
                    sp.GetRequiredService<ICsvExportService>(),
                    sp.GetRequiredService<ILocalizationManager>(),
                    sp.GetRequiredService<ICandleCache>(), // T-085: the bootstrap page serves the cache first.
                    sp.GetRequiredService<CandleBackfillService>(), // T-086: the repair chain's fetcher.
                    sp.GetRequiredService<IChartDrawingStore>(), // T-091: the persisted drawings the page mirrors.
                    sp.GetRequiredService<StrategySignalService>(), // T-112: the stances the markers read.
                    sp.GetRequiredService<SignalRailViewModel>(), // T-112: the rail strip's VM.
                    sp.GetRequiredService<SignalSeriesRelay>(), // T-112: the loads the host feeds from.
                    sp.GetRequiredService<ChartFootprintService>().Updates, // T-122: the footprint feed's stream.
                    sp.GetRequiredService<FundingHistoryService>(), // T-240: the funding pane's history front.
                    sp.GetRequiredService<OIHistoryService>(), // T-241: the OI pane's history front.
                    sp.GetRequiredService<LeverageBracketsService>(), // T-243: the liquidation levels' bracket front.
                    sp.GetRequiredService<ILogger<ChartViewModel>>(),
                    slotId),
                sp.GetRequiredService<ILogger<WorkspaceChartsViewModel>>()));
}
