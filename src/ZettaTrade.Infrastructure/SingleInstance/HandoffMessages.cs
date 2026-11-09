namespace ZettaTrade.Infrastructure.SingleInstance;

using System;
using System.Collections.Generic;
using System.Text.Json;

/// <summary>
/// The one framed message the second start writes down the handoff pipe:
/// the raw argument vector, exactly as the shell handed it over. The
/// running instance re-validates the whole thing (the parse and the
/// semantic checks run on the receiver side), so the wire never smuggles
/// a decision.
/// </summary>
/// <param name="Args">The raw vector.</param>
public sealed record HandoffPayload(IReadOnlyList<string> Args)
{
    /// <summary>A framed payload (and its reply) never exceeds this — a hostile or broken peer's oversized frame is refused by position, never buffered.</summary>
    public const int MaxFramedBytes = 65_536;
}

/// <summary>
/// The receiver's settled word back over the pipe: what the handoff did to
/// the workspace. Both ends may log it; nothing user-facing renders it.
/// </summary>
/// <param name="Applied">True when a selection reached the workspace symbol route (including the no-change case where the workspace already stood there). False when nothing was applied.</param>
/// <param name="Reason">The machine-word of the outcome (the parse refusal, the semantic refusal or the applied shape), for the file logs only.</param>
public sealed record HandoffReceipt(bool Applied, string Reason)
{
    /// <summary>The receipt for a forwarded vector that carries no selection at all.</summary>
    public static HandoffReceipt NoSelection { get; } = new(false, "the forwarded vector carries no allowlisted selection");

    /// <summary>The receipt for a forwarded vector the grammar refused (a never-seen peer's tokens cannot trick the framing).</summary>
    public static HandoffReceipt GrammarRefused { get; } = new(false, "the forwarded vector failed the allowlist grammar");

    /// <summary>The receipt for a payload that could not even be decoded from the frame.</summary>
    public static HandoffReceipt Undecodable { get; } = new(false, "the forwarded payload could not be decoded");
}

/// <summary>The JSON options both handoff ends share (camelCase naming on both shapes).</summary>
internal static class HandoffJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}
