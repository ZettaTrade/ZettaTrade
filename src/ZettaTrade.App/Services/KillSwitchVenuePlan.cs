namespace ZettaTrade.App.Services;

using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// One trading venue's plan inside a kill-switch sweep (T-188): the venue
/// the pass will command, its localized display name, and the working
/// orders and open positions the venue served at the plan read — the facts
/// the ONE confirmation lists. The plan is the confirmed target set; the
/// execution phase re-reads positions for sizing (the display rows are
/// never the sizing source), never to widen the set.
/// </summary>
/// <param name="VenueName">The venue's display name (the localized paper tag or the venue's own).</param>
/// <param name="Venue">The trading adapter the pass commands.</param>
/// <param name="Orders">The venue's working orders at plan time.</param>
/// <param name="Positions">The venue's open positions at plan time.</param>
public sealed record KillSwitchVenuePlan(
    string VenueName,
    ITradingAdapter Venue,
    IReadOnlyList<Order> Orders,
    IReadOnlyList<Position> Positions);
