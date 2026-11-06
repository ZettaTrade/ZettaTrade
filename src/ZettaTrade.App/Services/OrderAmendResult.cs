namespace ZettaTrade.App.Services;

/// <summary>
/// Outcome of one amend attempt, with the user-facing localized message the
/// panel renders. A null <see cref="Message"/> means there is nothing to
/// report (a declined confirmation is a deliberate stop, not a failure).
/// </summary>
public sealed record OrderAmendResult
{
    /// <summary>Whether the amend reached its goal (replacement working or the amend performed in place).</summary>
    public required bool Succeeded { get; init; }

    /// <summary>The localized message to render (null hides the whole line).</summary>
    public required string? Message { get; init; }

    /// <summary>A completed amend with its localized outcome line.</summary>
    public static OrderAmendResult Done(string message) => new() { Succeeded = true, Message = message };

    /// <summary>A refused amend with its localized reason.</summary>
    public static OrderAmendResult Failed(string message) => new() { Succeeded = false, Message = message };

    /// <summary>A deliberate stop (declined confirmation) with no user-facing line.</summary>
    public static OrderAmendResult Silent() => new() { Succeeded = false, Message = null };
}
