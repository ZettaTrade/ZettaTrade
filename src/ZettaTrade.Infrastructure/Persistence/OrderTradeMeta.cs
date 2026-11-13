namespace ZettaTrade.Infrastructure.Persistence;

/// <summary>
/// One journal row's user trade annotations (T-211): the note's free text and
/// the canonical comma-joined tag list, keyed by the row's journal identity.
/// Null fields are the honest "not written" — the annotation was never made
/// or was explicitly cleared.
/// </summary>
public sealed record OrderTradeMeta(
    string ExchangeId,
    string OrderId,
    string? Note,
    string? Tags);
