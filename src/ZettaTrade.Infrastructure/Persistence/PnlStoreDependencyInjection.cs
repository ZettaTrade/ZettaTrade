namespace ZettaTrade.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ZettaTrade.Infrastructure.Configuration;
using ZettaTrade.Infrastructure.PnL;

/// <summary>
/// DI wiring for the PnL accounting store (T-082): the SQLite realized
/// history options plus the headless accounting engine. The engine is a
/// singleton (per-operation contexts inside it) and initializes through
/// <see cref="Livery.Contracts.IAsyncInitializable"/> during the
/// composition root's startup walk — the walk order matters: registered
/// after the order history store, so the ledger source is migrated and
/// ready before the engine's boot replay reads it.
/// </summary>
public static class PnlStoreDependencyInjection
{
    /// <summary>Register the realized-history store and the accounting engine.</summary>
    public static IServiceCollection AddPnlStore(this IServiceCollection services) => services
        .AddSingleton(sp => new DbContextOptionsBuilder<PnlDbContext>()
            .UseSqlite($"Data Source={ConfigPathResolver.PnlDatabaseFile}")
            .Options)
        .AddSingleton<PnlAccountingService>();
}
