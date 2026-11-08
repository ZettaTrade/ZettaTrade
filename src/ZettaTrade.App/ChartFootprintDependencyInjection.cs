namespace ZettaTrade.App;

using Microsoft.Extensions.DependencyInjection;
using ZettaTrade.App.Services;
using ZettaTrade.Infrastructure.PnL;

/// <summary>
/// DI wiring for the chart footprint feed (T-122): the app-side composition
/// of the trading lane's journals and reads into the chart overlays'
/// keyed inventory. Registered in the composition root AFTER the order
/// history and PnL registrations (the feed's boot read wants the journals
/// initialized first) and BEFORE the chart ViewModel resolves it.
/// </summary>
public static class ChartFootprintDependencyInjection
{
    /// <summary>
    /// Register the chart footprint feed singleton. The feed's positions
    /// stream rides the PnL feed's standing snapshots (the same stream the
    /// portfolio panel reads) — explicitly registered so the feed's
    /// activation resolves the real engine stream, never an uninjected
    /// member.
    /// </summary>
    public static IServiceCollection AddChartFootprintServices(this IServiceCollection services) => services
        .AddSingleton<IObservable<PnlSnapshot>>(sp => sp
            .GetRequiredService<PnlFeedService>()
            .Snapshots) // the feed's boot/compose stream: the PnL engine's own state feed.
        .AddSingleton<ChartFootprintService>();
}
