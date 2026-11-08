namespace ZettaTrade.App;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZettaTrade.App.Services;
using ZettaTrade.Infrastructure.Persistence;

/// <summary>
/// The notification center's composition (T-097). Registered by the
/// composition root AFTER <c>AddOrderHistory()</c>: the journal tap below
/// replaces the interface binding (the service collection's last-wins rule
/// distributes it to every consumer resolution, while the startup walk still
/// initializes the real store once through the tap's delegating
/// <see cref="ZettaTrade.Core.Interfaces.IAsyncInitializable"/>), and the
/// adapter state source must resolve after the health fold's registration
/// (its stream is the T-098 fold's output).
/// </summary>
public static class NotificationDependencyInjection
{
    /// <summary>
    /// Register the notification services. The decorated journal binding
    /// reuses the exact SQLite artifacts the base registration carries
    /// (the same connection-string options and logger), so the walk still
    /// applies the real store's migrations exactly once.
    /// </summary>
    public static IServiceCollection AddNotificationServices(this IServiceCollection services) => services
        .AddSingleton<NotificationCenter>()
        // The journal tap: the last IOrderHistoryStore binding wins — the
        // flows keep resolving the interface (now the notifying wrapper),
        // the fetch pathways ride pass-through reads, and a persistence
        // fault journals its error exactly where the base store's contract
        // already holds it.
        .AddSingleton<IOrderHistoryStore>(sp => new NotifyingOrderHistoryStore(
            new SqliteOrderHistoryStore(
                sp.GetRequiredService<DbContextOptions<OrderHistoryDbContext>>(),
                sp.GetRequiredService<ILogger<SqliteOrderHistoryStore>>()),
            sp.GetRequiredService<NotificationCenter>(),
            sp.GetRequiredService<ILogger<NotifyingOrderHistoryStore>>()))
        // The adapter state tap (the T-098 fold source): starts with the walk.
        .AddSingleton<AdapterStateNotificationSource>();
}
