namespace ZettaTrade.AI.Chat;

/// <summary>
/// One conversation turn as it travels to the provider. Roles use the wire
/// spellings of the chat-completion world: <c>user</c> for the trader's
/// message, <c>assistant</c> for the model's.
/// </summary>
public sealed record ChatTurn(string Role, string Content)
{
    /// <summary>The trader's turn.</summary>
    public const string UserRole = "user";

    /// <summary>The model's turn.</summary>
    public const string AssistantRole = "assistant";
}
