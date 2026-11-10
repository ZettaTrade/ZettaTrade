namespace ZettaTrade.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;
using ZettaTrade.Core.Enums;

/// <summary>
/// EF Core context for the persisted chart drawings (T-091). One small
/// table stores the drawings' rows — a drawing is its kind plus one row
/// per anchor; the layer loads and saves whole, so the schema mirrors the
/// shape and nothing else.
/// </summary>
public sealed class ChartDrawingDbContext(DbContextOptions<ChartDrawingDbContext> options) : DbContext(options)
{
    #region Properties

    /// <summary>The drawings' anchor rows, keyed by (DrawingId, SortOrder).</summary>
    public DbSet<ChartDrawingEntity> Drawings => Set<ChartDrawingEntity>();

    #endregion Properties

    #region Protected Methods

    /// <inheritdoc/>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ChartDrawingEntity>(entity =>
        {
            // Composite identity: a drawing owns its anchor rows in draw
            // order; a rewrite replaces the rows whole.
            entity.HasKey(entry => new { entry.DrawingId, entry.SortOrder });
            entity.ToTable("Drawings");

            entity.Property(entry => entry.TimeFrame).HasConversion<string>();

            // Reusing the shared persistence converters (the candle cache
            // and order history columns ride the exact ones): invariant-
            // culture TEXT for timestamps and decimals.
            entity.Property(entry => entry.Time).HasConversion(OrderHistoryConversions.Timestamp);
            entity.Property(entry => entry.Price).HasConversion(OrderHistoryConversions.Amount);

            // The layer read keys on (adapter, symbol, interval).
            entity.HasIndex(entry => new { entry.AdapterId, entry.Symbol, entry.TimeFrame });
        });
    }

    #endregion Protected Methods
}
