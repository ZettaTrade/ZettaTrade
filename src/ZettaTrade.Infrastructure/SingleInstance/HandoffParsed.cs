namespace ZettaTrade.Infrastructure.SingleInstance;

/// <summary>
/// One argument vector's grammar result: either the single allowlisted
/// selection the vector carries (null when there is none — an empty vector
/// is a valid prompt redirect) or the one refusal that killed it. The
/// parser never blends the two: a rejected vector owns no selection.
/// </summary>
/// <param name="Selection">The selection when the vector grammar accepted it; null when the vector denies any selection.</param>
/// <param name="Rejection">The refusal reason when the grammar rejected the vector; null when it accepted.</param>
public sealed record HandoffParsed(HandoffSelection? Selection, HandoffRejectionReason? Rejection)
{
    /// <summary>The accepted vector's result with no selection (nothing to apply — the redirect itself remains valid).</summary>
    public static HandoffParsed NoSelection { get; } = new(null, null);

    /// <summary>The refused vector's result (the reason says which position failed).</summary>
    public static HandoffParsed Rejected(HandoffRejectionReason reason) => new(null, reason);
}
