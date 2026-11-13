namespace ZettaTrade.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;

/// <summary>
/// EF Core context for the persistent portfolio-value history (T-209). One
/// small table (<see cref="PortfolioValueEntry"/>) stores nothing but the
/// value snapshots' accounting rows — the schema grows only when the
/// record itself grows.
/// </summary>
public sealed class PortfolioValueDbContext(DbContextOptions<PortfolioValueDbContext> options) : DbContext(options)
{
    #region Properties

    /// <summary>The value snapshot rows, keyed by (ExchangeId, TimestampUtc).</summary>
    public DbSet<PortfolioValueEntry> Snapshots => Set<PortfolioValueEntry>();

    #endregion Properties

    #region Protected Methods

    /// <inheritdoc/>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PortfolioValueEntry>(entity =>
        {
            // Composite identity: a value history is per trading account,
            // and one account's rows are ordered by the moment they were
            // taken.
            entity.HasKey(entry => new { entry.ExchangeId, entry.TimestampUtc });
            entity.ToTable("PortfolioValue");

            entity.Property(entry => entry.TimestampUtc).HasConversion(OrderHistoryConversions.Timestamp);
            entity.Property(entry => entry.TotalValue).HasConversion(OrderHistoryConversions.Amount);
        });
    }

    #endregion Protected Methods
}
