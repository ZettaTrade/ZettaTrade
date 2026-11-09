namespace ZettaTrade.Infrastructure.SingleInstance;

/// <summary>
/// The allowlist grammar's selection: at most one symbol-and-exchange pair
/// per vector. The symbol text is kept exactly as handed over — the real
/// parsers judge it on the receiver side (the exchange's format when an
/// exchange rides along, the normalized format otherwise); the parser is
/// never a symbol validator itself.
/// </summary>
/// <param name="SymbolText">The raw symbol token, or null when the vector carries no symbol. Never flag-shaped (the grammar refuses those positions before constructing a selection).</param>
/// <param name="ExchangeId">The raw exchange token, or null when the vector carries no exchange. Exchange-only selections are valid: the handoff then moves the active venue.</param>
public sealed record HandoffSelection(string? SymbolText, string? ExchangeId)
{
    /// <summary>Whether the selection names anything at all.</summary>
    public bool HasSelection => SymbolText is not null || ExchangeId is not null;
}
