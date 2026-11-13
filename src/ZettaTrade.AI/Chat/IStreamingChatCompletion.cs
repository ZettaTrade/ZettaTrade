namespace ZettaTrade.AI.Chat;

/// <summary>
/// The streaming half of the chat surface (T-205): the reply arrives as
/// deltas in the provider's own generation order, so a caller renders the
/// answer while it is still being written. The contract mirrors
/// <see cref="IChatCompletion"/>: the caller owns the conversation, the
/// system prompt carries the workspace context, and the same failure
/// families apply — transport and status problems as
/// <see cref="HttpRequestException"/>, an unusable reply as
/// <see cref="InvalidOperationException"/> (including a stream that closes
/// without carrying any content), cancellation as
/// <see cref="OperationCanceledException"/>. Providers that cannot stream
/// stay out of the way: the caller falls back to the one-shot
/// <see cref="IChatCompletion"/> (the settings key, read at issue time).
/// </summary>
public interface IStreamingChatCompletion
{
    /// <summary>
    /// Stream a chat conversation: the system prompt carries the caller's
    /// workspace context, the turns carry the session history up to and
    /// including the newest user message. Each yielded string is one delta
    /// of the reply in arrival order; concatenating them reconstructs the
    /// full answer. No empty deltas are yielded.
    /// </summary>
    /// <param name="systemPrompt">Stable instruction text (workspace context).</param>
    /// <param name="turns">The conversation so far, oldest first.</param>
    /// <param name="ct">Cancellation of the stream (the chat panel's Stop
    /// affordance cancels it mid-flight).</param>
    /// <exception cref="HttpRequestException">The provider is unreachable,
    /// refused the connection, or answered with an error status — the
    /// "assistant off" family.</exception>
    /// <exception cref="InvalidOperationException">The provider answered
    /// but the reply is unusable: an unreadable line, or a stream that
    /// closed without carrying any content.</exception>
    /// <exception cref="OperationCanceledException">The stream was
    /// cancelled before it finished (the caller's token).</exception>
    IAsyncEnumerable<string> StreamAsync(
        string systemPrompt,
        IReadOnlyList<ChatTurn> turns,
        CancellationToken ct = default);
}
