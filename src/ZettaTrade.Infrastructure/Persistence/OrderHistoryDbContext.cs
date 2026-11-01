namespace ZettaTrade.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;

/// <summary>
/// EF Core context for the persistent order history. One small table
/// (<see cref="OrderHistoryEntry"/>) stores nothing but order fields — the
/// schema grows only when the order record itself grows.
/// </summary>
public sealed class OrderHistoryDbContext(DbContextOptions<OrderHistoryDbContext> options) : DbContext(options)
{
    #region Properties

    /// <summary>The journal rows, keyed by (ExchangeId, OrderId).</summary>
    public DbSet<OrderHistoryEntry> Orders => Set<OrderHistoryEntry>();

    #endregion Properties

    #region Protected Methods

    /// <inheritdoc/>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OrderHistoryEntry>(entity =>
        {
            // Composite identity: an order id is only unique inside its exchange.
            entity.HasKey(entry => new { entry.ExchangeId, entry.OrderId });
            entity.ToTable("Orders");

            entity.Property(entry => entry.Side).HasConversion<string>();
            entity.Property(entry => entry.Type).HasConversion<string>();
            entity.Property(entry => entry.Status).HasConversion<string>();
            entity.Property(entry => entry.TimeInForce).HasConversion<string>();

            entity.Property(entry => entry.Quantity).HasConversion(OrderHistoryConversions.Amount);
            entity.Property(entry => entry.Price).HasConversion(OrderHistoryConversions.Amount);
            entity.Property(entry => entry.FilledQuantity).HasConversion(OrderHistoryConversions.Amount);
            entity.Property(entry => entry.StopPrice).HasConversion(OrderHistoryConversions.NullableAmount);

            entity.Property(entry => entry.CreatedAt).HasConversion(OrderHistoryConversions.Timestamp);
            entity.Property(entry => entry.UpdatedAt).HasConversion(OrderHistoryConversions.NullableTimestamp);
            entity.Property(entry => entry.Expiry).HasConversion(OrderHistoryConversions.NullableTimestamp);
        });
    }

    #endregion Protected Methods
}
