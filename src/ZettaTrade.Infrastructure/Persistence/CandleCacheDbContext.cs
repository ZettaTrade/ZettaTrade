namespace ZettaTrade.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;

/// <summary>
/// EF Core context for the local candle cache. One small table
/// (<see cref="CandleCacheEntry"/>) stores nothing but the served bars —
/// the cache is disposable bookkeeping, so its schema grows only when the
/// bar record itself grows.
/// </summary>
public sealed class CandleCacheDbContext(DbContextOptions<CandleCacheDbContext> options) : DbContext(options)
{
    #region Properties

    /// <summary>The cached bar rows, keyed by (AdapterId, Symbol, TimeFrame, OpenTime).</summary>
    public DbSet<CandleCacheEntry> Candles => Set<CandleCacheEntry>();

    #endregion Properties

    #region Protected Methods

    /// <inheritdoc/>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CandleCacheEntry>(entity =>
        {
            // Composite identity: a bar is only unique inside its adapter +
            // symbol + interval series, and upserts key on the open time.
            entity.HasKey(entry => new { entry.AdapterId, entry.Symbol, entry.TimeFrame, entry.OpenTime });
            entity.ToTable("Candles");

            entity.Property(entry => entry.TimeFrame).HasConversion<string>();

            // Reusing the shared persistence converters (the order history
            // and PnL columns ride the exact ones): invariant-culture TEXT
            // for timestamps and decimals, a queue-stable sort order.
            entity.Property(entry => entry.OpenTime).HasConversion(OrderHistoryConversions.Timestamp);

            entity.Property(entry => entry.Open).HasConversion(OrderHistoryConversions.Amount);
            entity.Property(entry => entry.High).HasConversion(OrderHistoryConversions.Amount);
            entity.Property(entry => entry.Low).HasConversion(OrderHistoryConversions.Amount);
            entity.Property(entry => entry.Close).HasConversion(OrderHistoryConversions.Amount);
            entity.Property(entry => entry.Volume).HasConversion(OrderHistoryConversions.Amount);
        });
    }

    #endregion Protected Methods
}
