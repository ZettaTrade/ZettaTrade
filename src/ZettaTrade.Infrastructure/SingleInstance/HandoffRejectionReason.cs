namespace ZettaTrade.Infrastructure.SingleInstance;

/// <summary>
/// Why the handoff argument grammar refused a vector. Every reason is a
/// hard rejection: nothing substitutes a default, nothing forwards a
/// half-valid shape, nothing ever reaches the workspace symbol route. The
/// allowlist principle is exclusion by position — a reason says exactly
/// which position of the vector failed the gate.
/// </summary>
public enum HandoffRejectionReason
{
    /// <summary>A token that belongs to no allowlisted form (this covers every order-shaped token: `--order`, `--cancel`, `--side`, quantity or price flags — the grammar never inspects them further, they are refused by position).</summary>
    UnknownToken = 1,

    /// <summary>A flag ended the vector, or its value position was taken by another flag.</summary>
    MissingValue = 2,

    /// <summary>A flag's value is missing or whitespace-only.</summary>
    EmptyValue = 3,

    /// <summary>A value position looked like a flag itself (values can never carry `--` shapes).</summary>
    FlagShapedValue = 4,

    /// <summary>A protocol string did not parse as a URI from within the frame of the shell launch.</summary>
    MalformedProtocol = 6,

    /// <summary>The protocol string names no allowlisted route host.</summary>
    UnrecognizedProtocolRoute = 7,

    /// <summary>The protocol string's query carries a key outside the allowlist.</summary>
    UnknownProtocolQueryKey = 8,

    /// <summary>More than one allowlisted selection source (flags plus one or more protocol strings, or several protocol strings) contributed to one vector.</summary>
    DuplicateSelectionSource = 9,
}
