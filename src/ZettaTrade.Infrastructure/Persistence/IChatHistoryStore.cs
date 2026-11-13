namespace ZettaTrade.Infrastructure.Persistence;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// The persistent AI chat history's store contract (T-206): the
/// conversations' list for the picker (newest update first) and the
/// per-conversation transcripts (insertion order). The chat ViewModel owns
/// the in-memory surface — this store is the history of record the picker
/// fills from and every restore replays.
/// </summary>
public interface IChatHistoryStore
{
    /// <summary>
    /// The stored conversations, newest update first (a fresh picker reads
    /// the same order the trader left behind). An untitled conversation
    /// rides its row — the restore renders the localized default for it.
    /// </summary>
    Task<IReadOnlyList<ChatConversation>> ListConversationsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The conversation's transcript in the order it was spoken. An unknown
    /// conversation reads empty — never a fault for absence.
    /// </summary>
    Task<IReadOnlyList<ChatHistoryMessage>> LoadMessagesAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Open a fresh conversation (untitled — the first message auto-titles
    /// it) and return its stored record, identity included.
    /// </summary>
    Task<ChatConversation> CreateConversationAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Append one message to the conversation and bump its update moment
    /// (the newest-first list follows). An unknown conversation is a logged
    /// drop, never an exception — the chat never breaks on its history.
    /// </summary>
    Task AppendMessageAsync(
        Guid conversationId,
        ChatHistoryMessage message,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Rename the conversation (the first message's auto-title): the title
    /// only — a rename never reorders the picker.
    /// </summary>
    Task RenameConversationAsync(
        Guid conversationId,
        string? title,
        CancellationToken cancellationToken = default);
}
