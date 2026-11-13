namespace ZettaTrade.Infrastructure.Persistence;

using ZettaTrade.Core.Entities;

/// <summary>
/// One journal page row (T-211): the Core <see cref="Order"/> the panels
/// serve, plus the row's user trade annotations as they sit in the journal.
/// Null Note/Tags is the honest unannotated — the stats surfaces gate the
/// tag lane and stamp the realized ledger from exactly these fields.
/// </summary>
public sealed record OrderPageRow(
    Order Order,
    string? Note,
    string? Tags);
