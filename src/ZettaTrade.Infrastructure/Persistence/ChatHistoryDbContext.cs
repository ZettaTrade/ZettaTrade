namespace ZettaTrade.Infrastructure.Persistence;

using System;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// EF Core context for the persistent AI chat history (T-206). Two small
/// tables — the conversations the picker lists and the messages that replay
/// as a transcript — so the chat's history of record survives the process.
/// </summary>
public sealed class ChatHistoryDbContext(DbContextOptions<ChatHistoryDbContext> options) : DbContext(options)
{
    #region Properties

    /// <summary>The conversations the picker lists (newest update first).</summary>
    public DbSet<ChatConversationEntry> Conversations => Set<ChatConversationEntry>();

    /// <summary>The messages' rows, keyed by conversation in insertion order.</summary>
    public DbSet<ChatMessageEntry> Messages => Set<ChatMessageEntry>();

    #endregion Properties

    #region Protected Methods

    /// <inheritdoc/>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ChatConversationEntry>(entity =>
        {
            entity.HasKey(entry => entry.Id);
            entity.ToTable("Conversations");

            // Reusing the shared persistence converters (the order history,
            // pnl and candle cache columns ride the exact ones):
            // invariant-culture roundtrip TEXT for timestamps.
            entity.Property(entry => entry.CreatedUtc).HasConversion(OrderHistoryConversions.Timestamp);
            entity.Property(entry => entry.UpdatedUtc).HasConversion(OrderHistoryConversions.Timestamp);

            // The picker reads the newest conversation first.
            entity.HasIndex(entry => entry.UpdatedUtc);
        });

        modelBuilder.Entity<ChatMessageEntry>(entity =>
        {
            // The auto-increment id IS the insertion order: a fresh read
            // replays the transcript in the order it was spoken.
            entity.HasKey(entry => entry.Id);
            entity.ToTable("Messages");
            entity.Property(entry => entry.Id).ValueGeneratedOnAdd();

            entity.Property(entry => entry.Kind).IsRequired();
            entity.Property(entry => entry.Content).IsRequired();
            entity.Property(entry => entry.TimestampUtc).HasConversion(OrderHistoryConversions.Timestamp);

            // A message belongs to its conversation; the FK keeps the rows
            // honest (cascade — the store's surface has no delete today,
            // but a cleaned-up parent must not strand its rows).
            entity.HasOne<ChatConversationEntry>()
                .WithMany()
                .HasForeignKey(entry => entry.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(entry => entry.ConversationId);
        });
    }

    #endregion Protected Methods
}
