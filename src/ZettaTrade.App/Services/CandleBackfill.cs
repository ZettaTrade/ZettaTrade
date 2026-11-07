namespace ZettaTrade.App.Services;

using System;
using System.Collections.Generic;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// The deep-history fetcher request (T-086): the coverage window the caller
/// needs, the adapter and the exact series key to fetch through.
/// <see cref="From"/> is the oldest open still needed (inclusive);
/// <see cref="To"/> is the EXCLUSIVE upper boundary (the known bar right
/// above the hole). The segments of one pass walk back from that upper edge,
/// so the batching stays deterministic against a rate limiter (T-106).
/// </summary>
public sealed record CandleBackfillRequest(
    IMarketDataAdapter Adapter,
    Symbol Symbol,
    TimeFrame TimeFrame,
    DateTimeOffset From,
    DateTimeOffset To)
{
    /// <summary>The requested window lower than the upper boundary is the
    /// caller's contract; a degenerate one is an error, never a silent
    /// no-op (the standing rule: never substitute defaults for invalid
    /// input).</summary>
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Adapter);
        ArgumentNullException.ThrowIfNull(Symbol);
        if (From >= To)
        {
            throw new ArgumentOutOfRangeException(
                nameof(To),
                To,
                "The backfill window's exclusive upper boundary must be above its oldest needed open.");
        }
    }
}

/// <summary>
/// One stitched segment of a backfill pass (T-086): ascending bars inside
/// the covered window, overlap-free against everything the pass already
/// served, each raw adapter page already persisted through the local cache
/// write-before-emit. <see cref="EndOfHistory"/> is set only on the FINAL
/// batch when the feed refused to go deeper (an empty answer where bars were
/// expected): <see cref="DepthFloor"/> carries the open the feed's series
/// ends at, so the caller surfaces the limit and never fabricates what the
/// feed does not serve.
/// </summary>
public sealed record CandleBackfillBatch(
    IReadOnlyList<Bar> Bars,
    bool EndOfHistory,
    DateTimeOffset? DepthFloor);
