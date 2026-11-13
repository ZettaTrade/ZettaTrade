namespace ZettaTrade.Infrastructure.Persistence;

using System;

/// <summary>
/// A persisted chat message row (T-206): one turn of one conversation.
/// The auto-increment id is the insertion order — a fresh read replays the
/// transcript in the order it was spoken. The kind is a free string (the
/// App's message kinds stay App-local); an unknown kind is the restore's
/// honest drop, never a schema opinion.
/// </summary>
public sealed class ChatMessageEntry
{
    #region Properties

    /// <summary>
    /// The insertion-order identity (auto-increment): the transcript's
    /// replay order.
    /// </summary>
    public long Id { get; set; }

    /// <summary>The owning conversation's identity.</summary>
    public Guid ConversationId { get; set; }

    /// <summary>The message's kind name (the store stays kind-agnostic).</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>The message's text content.</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>When the message was spoken (UTC).</summary>
    public DateTimeOffset TimestampUtc { get; set; }

    #endregion Properties
}
