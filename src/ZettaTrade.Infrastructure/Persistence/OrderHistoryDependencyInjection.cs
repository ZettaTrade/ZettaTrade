namespace ZettaTrade.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZettaTrade.Infrastructure.Configuration;

/// <summary>
/// DI wiring for the persistent order history. The store is a singleton
/// (per-operation contexts inside it) and initializes through
/// <see cref="ZettaTrade.Core.Interfaces.IAsyncInitializable"/> during the
/// composition root's startup walk. Since the walk screens typed
/// registrations through implementation-type assignability, both the
/// factory overload used here and a type-based registration initialize at
/// startup; the factory form is kept as-is (registration churn buys
/// nothing). The historical "type-based registrations are resolved lazily
/// on first use — too late for migrations" constraint was lifted by the
/// T-015 startup-walk fix.
/// </summary>
public static class OrderHistoryDependencyInjection
{
    /// <summary>Register the SQLite order history store.</summary>
    public static IServiceCollection AddOrderHistory(this IServiceCollection services) => services
        .AddSingleton(sp => new DbContextOptionsBuilder<OrderHistoryDbContext>()
            .UseSqlite($"Data Source={ConfigPathResolver.OrderHistoryDatabaseFile}")
            .Options)
        .AddSingleton<IOrderHistoryStore>(sp => new SqliteOrderHistoryStore(
            sp.GetRequiredService<DbContextOptions<OrderHistoryDbContext>>(),
            sp.GetRequiredService<ILogger<SqliteOrderHistoryStore>>()));
}
