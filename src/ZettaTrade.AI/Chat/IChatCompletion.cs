namespace ZettaTrade.AI.Chat;

/// <summary>
/// One-shot chat completion over a configured provider (local models first —
/// the Ollama HTTP server in this implementation). The caller owns the whole
/// conversation history: the turns are sent as-is every time, so there is no
/// server-side state and nothing to reset.
/// </summary>
public interface IChatCompletion
{
    /// <summary>
    /// Complete a chat conversation: the system prompt carries the caller's
    /// workspace context, the turns carry the session history up to and
    /// including the newest user message. Returns the assistant's reply.
    /// </summary>
    /// <param name="systemPrompt">Stable instruction text (workspace context).</param>
    /// <param name="turns">The conversation so far, oldest first.</param>
    /// <param name="ct">Cancellation of the request.</param>
    /// <exception cref="HttpRequestException">The provider is unreachable,
    /// refused the connection, or answered with an error status — the
    /// "assistant off" family.</exception>
    /// <exception cref="InvalidOperationException">The provider answered but
    /// the reply is unusable (unreadable/malformed/empty content).</exception>
    /// <exception cref="OperationCanceledException">The request gave up before
    /// an answer arrived (its own timeout or the caller's cancellation).</exception>
    Task<string> CompleteAsync(
        string systemPrompt,
        IReadOnlyList<ChatTurn> turns,
        CancellationToken ct = default);
}
