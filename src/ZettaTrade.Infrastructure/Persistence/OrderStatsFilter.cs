namespace ZettaTrade.Infrastructure.Persistence;

/// <summary>
/// A read-only scope filter for the trade-statistics queries over the
/// journal. Every property is optional: null (or null/blank for the text
/// fields) means unfiltered on that axis. <see cref="From"/> is inclusive,
/// <see cref="To"/> exclusive, per the keyset continuation semantics of
/// <see cref="IOrderStatsQuery.ReadPageAsync"/>.
/// </summary>
public sealed record OrderStatsFilter(
    string? Symbol,
    string? Exchange,
    DateTimeOffset? From,
    DateTimeOffset? To,
    bool? IsPaper);
