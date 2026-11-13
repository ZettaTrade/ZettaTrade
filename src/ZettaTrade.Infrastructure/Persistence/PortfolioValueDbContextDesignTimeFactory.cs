namespace ZettaTrade.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using ZettaTrade.Infrastructure.Configuration;

/// <summary>
/// Design-time factory for <c>dotnet ef migrations</c> over the
/// portfolio-value context: the runtime options are built inside the DI
/// aggregate method, which the EF tooling does not discover. Tooling-only —
/// the application never constructs contexts here.
/// </summary>
public sealed class PortfolioValueDbContextDesignTimeFactory : IDesignTimeDbContextFactory<PortfolioValueDbContext>
{
    #region Public Methods

    /// <summary>Create a context wired to the platform data directory.</summary>
    public PortfolioValueDbContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<PortfolioValueDbContext>()
            .UseSqlite($"Data Source={ConfigPathResolver.PortfolioValueDatabaseFile}")
            .Options);

    #endregion Public Methods
}
