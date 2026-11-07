namespace ZettaTrade.App;

using Microsoft.Extensions.DependencyInjection;
using ZettaTrade.App.Services;

/// <summary>
/// DI wiring for the deep-history backfill fetcher (T-086): the stateless
/// pass service behind the chart's subscription gap-repair — bounded
/// segments, write-through before emit, a bounded Channel leg for
/// backpressure. The chart view model resolves it through its constructor.
/// </summary>
public static class CandleBackfillDependencyInjection
{
    /// <summary>
    /// Register the backfill fetcher (stateless: every pass rides the
    /// caller's request with its own adapter and cache).
    /// </summary>
    public static IServiceCollection AddCandleBackfillServices(this IServiceCollection services) => services
        .AddSingleton<CandleBackfillService>();
}
