namespace ZettaTrade.Infrastructure.Persistence;

using System;

/// <summary>
/// One persisted chat message (T-206): a turn of a conversation's
/// transcript. The kind is a free string — the App's message kinds stay
/// App-local — so the store carries the shape and the restore decides what
/// a row renders as (an unknown kind is the honest drop, never a fabrication).
/// </summary>
/// <param name="Kind">The message's kind name (one of the kind constants).</param>
/// <param name="Content">The message's text content.</param>
/// <param name="TimestampUtc">When the message was spoken (UTC).</param>
public sealed record ChatHistoryMessage(string Kind, string Content, DateTimeOffset TimestampUtc)
{
    #region Constants

    /// <summary>The kind name of a trader's turn.</summary>
    public const string UserKind = "user";

    /// <summary>The kind name of an assistant's answer (or streamed prefix).</summary>
    public const string AssistantKind = "assistant";

    /// <summary>The kind name of an in-chat failure line.</summary>
    public const string ErrorKind = "error";

    #endregion Constants
}
