namespace ZettaTrade.Infrastructure.Persistence;

using System;

/// <summary>
/// One persisted chat conversation (T-206): the picker's row and the
/// transcript's home. The title is what the first user turn auto-wrote —
/// <c>null</c> while the conversation never grew, and the restore renders
/// the localized default in its place (the stored record stays honest).
/// </summary>
/// <param name="Id">The store identity of the conversation.</param>
/// <param name="Title">The stored title, or <c>null</c> while untitled.</param>
/// <param name="CreatedUtc">When the conversation was created (UTC).</param>
/// <param name="UpdatedUtc">When the conversation last grew (UTC).</param>
public sealed record ChatConversation(
    Guid Id,
    string? Title,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);
