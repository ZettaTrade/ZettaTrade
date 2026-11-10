namespace ZettaTrade.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using ZettaTrade.Infrastructure.Configuration;

/// <summary>
/// Design-time factory for <c>dotnet ef migrations</c>: the runtime options
/// are built inside the DI aggregate method, which the EF tooling does not
/// discover. Tooling-only — the application never constructs contexts here.
/// </summary>
public sealed class ChartDrawingDbContextDesignTimeFactory : IDesignTimeDbContextFactory<ChartDrawingDbContext>
{
    #region Public Methods

    /// <summary>Create a context wired to the platform data directory.</summary>
    public ChartDrawingDbContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<ChartDrawingDbContext>()
            .UseSqlite($"Data Source={ConfigPathResolver.ChartDrawingsDatabaseFile}")
            .Options);

    #endregion Public Methods
}
