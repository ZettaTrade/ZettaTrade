namespace ZettaTrade.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZettaTrade.Infrastructure.Configuration;

/// <summary>
/// DI wiring for the portfolio-value history store (T-209): the SQLite
/// value-snapshot options plus the bounded read/write store. The store is
/// a singleton (per-operation contexts inside it) and initializes through
/// <see cref="Livery.Contracts.IAsyncInitializable"/> during the
/// composition root's startup walk — registered before the snapshot
/// service, so the migrations are applied before the service's first write.
/// </summary>
public static class PortfolioValueDependencyInjection
{
    /// <summary>Register the portfolio-value history store.</summary>
    public static IServiceCollection AddPortfolioValueStore(this IServiceCollection services) => services
        .AddSingleton(sp => new DbContextOptionsBuilder<PortfolioValueDbContext>()
            .UseSqlite($"Data Source={ConfigPathResolver.PortfolioValueDatabaseFile}")
            .Options)
        .AddSingleton<IPortfolioValueStore>(sp => new SqlitePortfolioValueStore(
            sp.GetRequiredService<DbContextOptions<PortfolioValueDbContext>>(),
            sp.GetRequiredService<ILogger<SqlitePortfolioValueStore>>()));
}
