namespace ZettaTrade.Core.Entities;

using System;
using System.Collections.Generic;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// One grouped liquidation cascade (T-242): the contract, the run's span,
/// its served order count and total notional (a FLOOR — the venues publish
/// only the first forced order of a cascade), and the dominant side (the
/// force-closed side whose served notional carries the run — the pressure
/// direction the tape's readers name).
/// </summary>
/// <param name="Symbol">The contract the run belongs to.</param>
/// <param name="StartedAt">The run's first event's venue moment.</param>
/// <param name="EndedAt">The run's last event's venue moment.</param>
/// <param name="OrderCount">The served forced orders in the run (the under-reporting floor).</param>
/// <param name="TotalNotional">The run's summed served notional (the under-reporting floor).</param>
/// <param name="DominantSide">The force-closed side whose served notional carries the run.</param>
public sealed record LiquidationCascade(
    Symbol Symbol,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    int OrderCount,
    decimal TotalNotional,
    LiquidationSide DominantSide);

/// <summary>
/// One contract's liquidation window state as the feed serves it (T-242):
/// the recent events still in the feed's ring, their rolled-up totals, and
/// the open cascade the window currently qualifies (null when the window's
/// runs all sit under the threshold — an honest no-cascade, never a
/// fabricated one).
/// </summary>
/// <param name="Recent">The ring's events, oldest first.</param>
/// <param name="RecentCount">The ring's event count.</param>
/// <param name="RecentNotional">The ring's summed served notional.</param>
/// <param name="OpenCascade">The window's currently qualifying cascade, or null.</param>
public sealed record LiquidationsWindowState(
    IReadOnlyList<LiquidationEvent> Recent,
    int RecentCount,
    decimal RecentNotional,
    LiquidationCascade? OpenCascade);
