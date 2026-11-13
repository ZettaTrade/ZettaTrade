namespace ZettaTrade.Infrastructure.Persistence;

using System;

/// <summary>
/// A persisted chat conversation row (T-206): the identity plus the
/// timestamp bookkeeping the picker orders by and the title the trader's
/// first message auto-writes. The messages live in their own table keyed
/// by the conversation — this row never carries content.
/// </summary>
public sealed class ChatConversationEntry
{
    #region Properties

    /// <summary>The store identity of the conversation.</summary>
    public Guid Id { get; set; }

    /// <summary>
    /// The conversation's title (the first user turn's auto-title), or
    /// <c>null</c> while nothing was ever said in it.
    /// </summary>
    public string? Title { get; set; }

    /// <summary>When the conversation was created (UTC).</summary>
    public DateTimeOffset CreatedUtc { get; set; }

    /// <summary>
    /// When the conversation last grew (every appended message bumps it);
    /// the picker reads the newest first.
    /// </summary>
    public DateTimeOffset UpdatedUtc { get; set; }

    #endregion Properties
}
