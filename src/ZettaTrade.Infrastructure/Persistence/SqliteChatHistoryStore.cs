namespace ZettaTrade.Infrastructure.Persistence;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Livery.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// SQLite <see cref="IChatHistoryStore"/> (T-206). One
/// <see cref="ChatHistoryDbContext"/> per operation (cheap: they share the
/// singleton options), so an append never races a load on live state. The
/// initialize step creates the data directory and applies migrations; every
/// read and write awaits the same one-time migration (the startup walk
/// triggers it, but a chat that loads first waits for it — the history
/// reads and writes only after the schema is real).
/// </summary>
public sealed class SqliteChatHistoryStore(
    DbContextOptions<ChatHistoryDbContext> options,
    ILogger<SqliteChatHistoryStore> logger) : IChatHistoryStore, IAsyncInitializable
{
    /// <summary>Serializes the one-time migration against concurrent first calls.</summary>
    private readonly SemaphoreSlim _ensureReady = new(1, 1);

    /// <summary>Whether the data directory and schema are proven ready.</summary>
    private bool _ready;

    #region Public Methods

    /// <inheritdoc/>
    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        EnsureReadyAsync(cancellationToken);

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ChatConversation>> ListConversationsAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken);

        await using var dbContext = new ChatHistoryDbContext(options);

        // Newest update first — the picker's order — with the timestamps'
        // tie (two conversations stamped the same instant) broken by the
        // creation moment and the identity, so the read order is always
        // deterministic.
        var rows = await dbContext.Conversations
            .OrderByDescending(entry => entry.UpdatedUtc)
            .ThenByDescending(entry => entry.CreatedUtc)
            .ThenByDescending(entry => entry.Id)
            .ToListAsync(cancellationToken);

        return rows
            .Select(entry => new ChatConversation(
                entry.Id,
                entry.Title,
                entry.CreatedUtc,
                entry.UpdatedUtc))
            .ToArray();
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ChatHistoryMessage>> LoadMessagesAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken);

        await using var dbContext = new ChatHistoryDbContext(options);

        // Insertion order by the auto-increment id: the transcript replays
        // in the order it was spoken. An unknown conversation reads empty.
        var rows = await dbContext.Messages
            .Where(entry => entry.ConversationId == conversationId)
            .OrderBy(entry => entry.Id)
            .ToListAsync(cancellationToken);

        return rows
            .Select(entry => new ChatHistoryMessage(
                entry.Kind,
                entry.Content,
                entry.TimestampUtc))
            .ToArray();
    }

    /// <inheritdoc/>
    public async Task<ChatConversation> CreateConversationAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var entry = new ChatConversationEntry
        {
            Id = Guid.NewGuid(),
            Title = null,
            CreatedUtc = now,
            UpdatedUtc = now,
        };

        await using var dbContext = new ChatHistoryDbContext(options);
        dbContext.Conversations.Add(entry);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new ChatConversation(entry.Id, entry.Title, entry.CreatedUtc, entry.UpdatedUtc);
    }

    /// <inheritdoc/>
    public async Task AppendMessageAsync(
        Guid conversationId,
        ChatHistoryMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        await EnsureReadyAsync(cancellationToken);

        await using var dbContext = new ChatHistoryDbContext(options);
        var conversation = await dbContext.Conversations
            .SingleOrDefaultAsync(entry => entry.Id == conversationId, cancellationToken);
        if (conversation is null)
        {
            // The honest drop (the MarkCancelledAsync discipline): an append
            // to a conversation that never existed is a logged warning, not
            // a broken chat.
            logger.LogWarning(
                "Chat message dropped: conversation {ConversationId} does not exist",
                conversationId);
            return;
        }

        dbContext.Messages.Add(new ChatMessageEntry
        {
            ConversationId = conversationId,
            Kind = message.Kind,
            Content = message.Content,
            TimestampUtc = message.TimestampUtc,
        });
        conversation.UpdatedUtc = DateTimeOffset.UtcNow;

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task RenameConversationAsync(
        Guid conversationId,
        string? title,
        CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken);

        await using var dbContext = new ChatHistoryDbContext(options);
        var conversation = await dbContext.Conversations
            .SingleOrDefaultAsync(entry => entry.Id == conversationId, cancellationToken);
        if (conversation is null)
        {
            logger.LogWarning(
                "Chat conversation rename dropped: conversation {ConversationId} does not exist",
                conversationId);
            return;
        }

        // The title only: a rename never reorders the picker.
        conversation.Title = title;

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    #endregion Public Methods

    #region Private Methods

    /// <summary>
    /// The one-time readiness gate: the data directory and the schema are
    /// proven exactly once, no matter which path (the startup walk or the
    /// first chat call) reaches them first.
    /// </summary>
    private async Task EnsureReadyAsync(CancellationToken cancellationToken)
    {
        if (_ready)
        {
            return;
        }

        await _ensureReady.WaitAsync(cancellationToken);
        try
        {
            if (_ready)
            {
                return;
            }

            await using var dbContext = new ChatHistoryDbContext(options);
            var file = dbContext.Database.GetConnectionString()
                ?? throw new InvalidOperationException("The chat history database has no connection string.");

            // Self-contained startup: the store must not assume someone else
            // created the data directory (freshly installed on a new machine).
            var directory = System.IO.Path.GetDirectoryName(
                file.StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase)
                    ? file["Data Source=".Length..]
                    : file);
            if (!string.IsNullOrEmpty(directory))
            {
                System.IO.Directory.CreateDirectory(directory);
            }

            await dbContext.Database.MigrateAsync(cancellationToken);
            logger.LogInformation(
                "Chat history store ready at {DatabaseFile} (migrations applied)",
                file);
            _ready = true;
        }
        finally
        {
            _ensureReady.Release();
        }
    }

    #endregion Private Methods
}
