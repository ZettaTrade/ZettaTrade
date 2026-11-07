namespace ZettaTrade.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Infrastructure.Configuration;

/// <summary>
/// DI wiring for the local candle cache. The store is a singleton
/// (per-operation contexts inside it) and initializes through
/// <see cref="ZettaTrade.Core.Interfaces.IAsyncInitializable"/> during the
/// composition root's startup walk — migrations apply before any surface
/// can read or write. Register it after the settings service (its policy
/// load reads the <c>Caching</c> section during that walk).
/// </summary>
public static class CandleCacheDependencyInjection
{
    /// <summary>Register the SQLite candle cache.</summary>
    public static IServiceCollection AddCandleCache(this IServiceCollection services) => services
        .AddSingleton(sp => new DbContextOptionsBuilder<CandleCacheDbContext>()
            .UseSqlite($"Data Source={ConfigPathResolver.CandleCacheDatabaseFile}")
            .Options)
        .AddSingleton<ICandleCache>(sp => new SqliteCandleCacheStore(
            sp.GetRequiredService<DbContextOptions<CandleCacheDbContext>>(),
            sp.GetRequiredService<ISettingsService>(),
            sp.GetRequiredService<ILogger<SqliteCandleCacheStore>>()));
}
