namespace ZettaTrade.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZettaTrade.Infrastructure.Configuration;

/// <summary>
/// DI wiring for the persistent AI chat history (T-206). The store is a
/// singleton (per-operation contexts inside it) and initializes through
/// <see cref="ZettaTrade.Core.Interfaces.IAsyncInitializable"/> during the
/// composition root's startup walk — registered BEFORE the chat ViewModel
/// so its migrations apply ahead of the history restore.
/// </summary>
public static class ChatHistoryDependencyInjection
{
    /// <summary>Register the SQLite chat history store.</summary>
    public static IServiceCollection AddChatHistory(this IServiceCollection services) => services
        .AddSingleton(sp => new DbContextOptionsBuilder<ChatHistoryDbContext>()
            .UseSqlite($"Data Source={ConfigPathResolver.ChatHistoryDatabaseFile}")
            .Options)
        .AddSingleton<IChatHistoryStore>(sp => new SqliteChatHistoryStore(
            sp.GetRequiredService<DbContextOptions<ChatHistoryDbContext>>(),
            sp.GetRequiredService<ILogger<SqliteChatHistoryStore>>()));
}
