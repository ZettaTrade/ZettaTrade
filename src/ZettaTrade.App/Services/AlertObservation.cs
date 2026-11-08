namespace ZettaTrade.App.Services;

/// <summary>
/// One factual observation the engine publishes per evaluated tick (T-096):
/// the manager's price column rides it live, and the stream-driven design
/// leaves exactly one source — an arriving ticker tick. Nothing in the
/// alerts module fabricates observations: no timer, no fetch, no poll.
/// </summary>
/// <param name="SymbolName">The normalized symbol the tick belongs to.</param>
/// <param name="OccurredUtc">The UTC moment the tick carries (the venue's own stamp).</param>
/// <param name="Price">The tick's last price.</param>
public sealed record AlertObservation(string SymbolName, DateTimeOffset OccurredUtc, decimal Price);
