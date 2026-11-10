namespace ZettaTrade.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ZettaTrade.Infrastructure.Configuration;

/// <summary>
/// DI wiring for the persisted chart drawings (T-091). The store is a
/// singleton (per-operation contexts inside it) and initializes through
/// <see cref="ZettaTrade.Core.Interfaces.IAsyncInitializable"/> during the
/// composition root's startup walk — migrations apply before any chart
/// surface can read or write. Registered after the candle cache's
/// registration keeps the store walk's order stable.
/// </summary>
public static class ChartDrawingDependencyInjection
{
    /// <summary>Register the SQLite chart drawings store.</summary>
    public static IServiceCollection AddChartDrawingStore(this IServiceCollection services) => services
        .AddSingleton(sp => new DbContextOptionsBuilder<ChartDrawingDbContext>()
            .UseSqlite($"Data Source={ConfigPathResolver.ChartDrawingsDatabaseFile}")
            .Options)
        .AddSingleton<IChartDrawingStore>(sp => new SqliteChartDrawingStore(
            sp.GetRequiredService<DbContextOptions<ChartDrawingDbContext>>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SqliteChartDrawingStore>>()));
}
