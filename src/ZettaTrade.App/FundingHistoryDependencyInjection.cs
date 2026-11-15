namespace ZettaTrade.App;

using Microsoft.Extensions.DependencyInjection;
using ZettaTrade.App.Services;

/// <summary>
/// DI wiring for the funding-history front (T-240): the chart's funding pane
/// resolves the venue's settled events through this session singleton — the
/// active-adapter probe, the bounded stale-aware per-symbol cache. No view
/// model of its own: the chart ViewModel consumes the service directly.
/// </summary>
public static class FundingHistoryDependencyInjection
{
    /// <summary>Register the funding-history front.</summary>
    public static IServiceCollection AddFundingHistoryServices(this IServiceCollection services) => services
        .AddSingleton<FundingHistoryService>();
}
