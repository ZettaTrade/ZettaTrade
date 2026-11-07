namespace ZettaTrade.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using ZettaTrade.Infrastructure.Configuration;

/// <summary>
/// Design-time factory for <c>dotnet ef migrations</c>: the runtime options
/// are built inside the DI aggregate method, which the EF tooling does not
/// discover. Tooling-only — the application never constructs contexts here.
/// </summary>
public sealed class CandleCacheDbContextDesignTimeFactory : IDesignTimeDbContextFactory<CandleCacheDbContext>
{
    #region Public Methods

    /// <summary>Create a context wired to the platform data directory.</summary>
    public CandleCacheDbContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<CandleCacheDbContext>()
            .UseSqlite($"Data Source={ConfigPathResolver.CandleCacheDatabaseFile}")
            .Options);

    #endregion Public Methods
}
