namespace ZettaTrade.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;

/// <summary>
/// EF Core context for the persistent realized-trade history. One small
/// table (<see cref="PnlRealizedEntry"/>) stores nothing but the accounting
/// of executed fills — the schema grows only when the accounting record
/// itself grows.
/// </summary>
public sealed class PnlDbContext(DbContextOptions<PnlDbContext> options) : DbContext(options)
{
    #region Properties

    /// <summary>The realized-trade rows, keyed by (ExchangeId, OrderId).</summary>
    public DbSet<PnlRealizedEntry> Realized => Set<PnlRealizedEntry>();

    #endregion Properties

    #region Protected Methods

    /// <inheritdoc/>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PnlRealizedEntry>(entity =>
        {
            // Composite identity: an order id is only unique inside its
            // exchange, mirroring the order journal it is accounted from.
            entity.HasKey(entry => new { entry.ExchangeId, entry.OrderId });
            entity.ToTable("PnlRealized");

            entity.Property(entry => entry.Side).HasConversion<string>();
            entity.Property(entry => entry.Effect).HasConversion<string>();

            entity.Property(entry => entry.Quantity).HasConversion(OrderHistoryConversions.Amount);
            entity.Property(entry => entry.Price).HasConversion(OrderHistoryConversions.Amount);
            entity.Property(entry => entry.Commission).HasConversion(OrderHistoryConversions.Amount);
            entity.Property(entry => entry.RealizedAmount).HasConversion(OrderHistoryConversions.Amount);

            entity.Property(entry => entry.RealizedAt).HasConversion(OrderHistoryConversions.Timestamp);
        });
    }

    #endregion Protected Methods
}
