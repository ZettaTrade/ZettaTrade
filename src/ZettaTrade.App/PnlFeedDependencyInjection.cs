namespace ZettaTrade.App;

using Microsoft.Extensions.DependencyInjection;
using ZettaTrade.App.Services;
using ZettaTrade.Infrastructure.Persistence;

/// <summary>
/// DI wiring for the PnL feed (T-082): the Infrastructure accounting store
/// (the realized-history context + the headless engine) plus the app-side
/// feed wrapper. Registered in the composition root AFTER the order
/// history (the engine's boot replay reads that ledger, so the walk must
/// initialize it first) and BEFORE every consumer — the T-083+ panels
/// resolve <see cref="PnlFeedService"/>.
/// </summary>
public static class PnlFeedDependencyInjection
{
    /// <summary>Register the PnL accounting store and the feed wrapper.</summary>
    public static IServiceCollection AddPnlServices(this IServiceCollection services) => services
        .AddPnlStore()
        .AddSingleton<PnlFeedService>();
}
