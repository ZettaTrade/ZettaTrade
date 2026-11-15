namespace ZettaTrade.App.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.ValueObjects;
/// <summary>
/// The liquidation feed's pure aggregation math (T-242): the cascade
/// grouping and the per-symbol window facts the feed's live lane serves.
/// Static and side-effect free so the goldens pin the semantics without a
/// transport, a clock or a scheduler.
/// <para>
/// A CASCADE is a maximal run of forced orders of one contract where each
/// event starts within <see cref="DefaultCascadeWindow"/> of the run's
/// previous event (the chain rule), whose summed notional reaches
/// <see cref="DefaultCascadeNotionalThreshold"/>. Runs below the threshold
/// are the tape's ordinary rows, never a cascade — the notional bound, not
/// the order count, is what qualifies a run: one oversized forced order
/// badges on its own, a constant stream of small ones never does.
/// </para>
/// <para>
/// The UNDER-REPORTING caveat the panel documents (the UI note): venues
/// publish only the FIRST forced order of a cascade (Bybit's
/// <c>allLiquidation</c> and Binance's <c>!forceOrder@arr</c> both), so a
/// cascade's served total is a floor, never the cascade's real size. The
/// aggregation never "corrects" for it — an estimate would be a
/// fabrication; the note says the numbers under-report.
/// </para>
/// </summary>
public static class LiquidationCascadeAggregator
{
    /// <summary>
    /// The chain gap that splits one run into two: two forced orders farther
    /// apart than this are separate liquidation episodes, one cascade never
    /// spans them.
    /// </summary>
    public static readonly TimeSpan DefaultCascadeWindow = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The notional a run must reach to be a cascade (in the contract's
    /// quote currency): the shipped "large-notional" bound — small enough
    /// that a real liquidation wave trips it on the majors, large enough
    /// that one retail forced order never does.
    /// </summary>
    public const decimal DefaultCascadeNotionalThreshold = 250_000m;

    /// <summary>
    /// Group the events of ONE contract (chronological order — the live
    /// order the wire serves) into cascades: the chain rule groups, the
    /// notional bound qualifies. Events carry their own symbol; a mixed
    /// list groups per symbol (a run never chains across contracts) and the
    /// groups come out in first-seen order, each run's events in arrival
    /// order.
    /// </summary>
    /// <param name="events">The events to group, oldest first (the wire's live order).</param>
    /// <param name="window">The chain gap that splits a run.</param>
    /// <param name="threshold">The notional a run must reach to be a cascade.</param>
    /// <returns>The qualifying cascades, oldest first; an empty list when no run qualifies.</returns>
    public static IReadOnlyList<LiquidationCascade> Group(
        IReadOnlyList<LiquidationEvent> events,
        TimeSpan window,
        decimal threshold)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (window < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(window), window, "The window must not be negative.");
        }

        if (threshold < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(threshold), threshold, "The threshold must not be negative.");
        }

        var cascades = new List<LiquidationCascade>();

        // Partition by contract FIRST, in first-seen order: a run never
        // chains across contracts, so the chain rule only ever sees ONE
        // contract's events. A mixed (interleaved) list still groups per
        // symbol — each partition keeps its events' arrival order, so the
        // per-contract chronology the chain rule reads is preserved.
        var partitions = new List<List<LiquidationEvent>>();
        var partitionIndex = new Dictionary<Symbol, List<LiquidationEvent>>();
        foreach (var ev in events)
        {
            if (!partitionIndex.TryGetValue(ev.Symbol, out var partition))
            {
                partition = new List<LiquidationEvent>();
                partitionIndex[ev.Symbol] = partition;
                partitions.Add(partition);
            }

            partition.Add(ev);
        }

        foreach (var partition in partitions)
        {
            ChainQualifyingRuns(partition, window, threshold, cascades);
        }

        return cascades;
    }

    /// <summary>
    /// The chain rule over ONE contract's events (chronological order): an
    /// event farther from the run's previous one than the window splits the
    /// run, every finished run qualifies through the notional bound.
    /// </summary>
    private static void ChainQualifyingRuns(
        IReadOnlyList<LiquidationEvent> events,
        TimeSpan window,
        decimal threshold,
        List<LiquidationCascade> cascades)
    {
        var run = new List<LiquidationEvent>();
        foreach (var ev in events)
        {
            if (run.Count > 0 && ev.Timestamp - run[^1].Timestamp > window)
            {
                AddIfQualifying(cascades, run, threshold);
                run.Clear();
            }

            run.Add(ev);
        }

        AddIfQualifying(cascades, run, threshold);
    }

    /// <summary>
    /// The live window facts of one contract's recent events: the events
    /// still in the lane's bounded ring, the open cascade the window
    /// currently qualifies — the LAST qualifying run whose last event is
    /// still inside the chain window of <paramref name="asOf"/> (a run
    /// could still be growing; a run whose tail went quiet past the window
    /// is closed history, and the badge clears) — and the ring's rolled-up
    /// totals. The feed's lane recomputes this per pulse tick and per
    /// event, so the badge clears within one tick of the openness expiry,
    /// never a stale latch.
    /// </summary>
    /// <param name="events">The contract's recent events, oldest first (the lane's bounded ring in its chronological order).</param>
    /// <param name="asOf">The moment the state is computed at (the lane's clock).</param>
    /// <param name="window">The chain gap that splits a run.</param>
    /// <param name="threshold">The notional a run must reach to be a cascade.</param>
    public static LiquidationsWindowState WindowState(
        IReadOnlyList<LiquidationEvent> events,
        DateTimeOffset asOf,
        TimeSpan window,
        decimal threshold)
    {
        ArgumentNullException.ThrowIfNull(events);

        var group = Group(events, window, threshold);
        var open = group.LastOrDefault(cascade => asOf - cascade.EndedAt <= window);
        return new LiquidationsWindowState(
            events,
            events.Count,
            events.Sum(ev => ev.Notional),
            open);
    }

    /// <summary>Qualify one finished run: reach the threshold, become a cascade fact; otherwise drop.</summary>
    private static void AddIfQualifying(
        List<LiquidationCascade> cascades,
        List<LiquidationEvent> run,
        decimal threshold)
    {
        if (run.Count == 0)
        {
            return;
        }

        var total = run.Sum(ev => ev.Notional);
        if (total < threshold)
        {
            return;
        }

        var longNotional = run
            .Where(ev => ev.LiquidatedSide == LiquidationSide.Long)
            .Sum(ev => ev.Notional);
        cascades.Add(new LiquidationCascade(
            run[0].Symbol,
            run[0].Timestamp,
            run[^1].Timestamp,
            run.Count,
            total,
            longNotional >= total - longNotional ? LiquidationSide.Long : LiquidationSide.Short));
    }
}
