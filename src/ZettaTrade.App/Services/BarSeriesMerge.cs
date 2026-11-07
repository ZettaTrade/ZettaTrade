namespace ZettaTrade.App.Services;

using ZettaTrade.Core.Entities;

/// <summary>
/// Pure window-merge math for the chart's older-history paging: the
/// currently loaded series (strictly ascending open times, the T-023
/// contract) meets the fetched previous page (the adapter's natural
/// ascending order). The result is the strictly-older, strictly-ascending
/// slice to prepend, deduped against the loaded head by open time. A guard
/// trip returns null — the caller keeps the view exactly as it stood (the
/// honest-view rule), never a repaired or defaulted substitute.
/// </summary>
public static class BarSeriesMerge
{
    /// <summary>
    /// Dedupe the fetched page against the loaded series' first open time
    /// and hand back the bars to prepend:
    /// <list type="bullet">
    ///   <item>The overlap is dropped by open time: the fetched boundary bar
    ///   whose open equals the loaded head (both sides requesting the same
    ///   candle) contributes nothing — the equal-open bar is excluded, so
    ///   the merged series stays strictly ascending.</item>
    ///   <item>An empty fetched page or an all-overlap page yields an empty
    ///   result: nothing older arrived (the caller treats it as deeper
    ///   history being exhausted at that anchor).</item>
    ///   <item>Any strictly-ascending violation inside the fetched page, or
    ///   inside the loaded series, trips the guard: <c>null</c>.</item>
    /// </list>
    /// </summary>
    /// <param name="existing">The loaded series (non-empty by the caller's
    /// contract; an empty one has no head to dedupe against).</param>
    /// <param name="fetched">The adapter's previous-window page.</param>
    /// <returns>The prepend slice in ascending order, empty when nothing
    /// older arrived, <c>null</c> when a contract guard tripped.</returns>
    public static IReadOnlyList<Bar>? PrependOlder(IReadOnlyList<Bar> existing, IReadOnlyList<Bar> fetched)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(fetched);

        if (existing.Count == 0 || fetched.Count == 0)
        {
            return Array.Empty<Bar>();
        }

        if (!IsStrictlyAscending(existing))
        {
            return null;
        }

        if (!IsStrictlyAscending(fetched))
        {
            return null;
        }

        var head = existing[0].Time;
        var prepend = new List<Bar>(fetched.Count);
        foreach (var bar in fetched)
        {
            if (bar.Time < head)
            {
                prepend.Add(bar);
            }
        }

        return prepend;
    }

    /// <summary>
    /// Set-union merge of two ascending series by open time (T-086's repair
    /// lane): the result is one strictly-ascending series where an
    /// equal-open bar keeps the EXISTING side — the displayed state stays
    /// authoritative, the fetch/cached overlaps conflate in the cache store,
    /// and the merge only adds what is absent. Either side empty yields the
    /// other verbatim; a contract trip (any side not strictly ascending)
    /// returns <c>null</c> — the caller keeps the view exactly as it stood
    /// (the honest-view rule), never a repaired or defaulted substitute.
    /// </summary>
    /// <param name="existing">The loaded series (ascending open times).</param>
    /// <param name="incoming">The additions (ascending open times), each
    /// possibly overlapping the other side's coverage.</param>
    /// <returns>The merged strictly-ascending series, or <c>null</c> when a
    /// guard tripped.</returns>
    public static IReadOnlyList<Bar>? UnionAscending(IReadOnlyList<Bar> existing, IReadOnlyList<Bar> incoming)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(incoming);

        if (existing.Count == 0)
        {
            return incoming;
        }

        if (incoming.Count == 0)
        {
            return existing;
        }

        if (!IsStrictlyAscending(existing) || !IsStrictlyAscending(incoming))
        {
            return null;
        }

        var merged = new List<Bar>(existing.Count + incoming.Count);
        var i = 0;
        var j = 0;
        while (i < existing.Count || j < incoming.Count)
        {
            // On an equal open the EXISTING bar wins (see the summary) and the
            // equal-open duplicate of the other side is skipped beneath.
            var takeExisting = j >= incoming.Count ||
                (i < existing.Count && existing[i].Time <= incoming[j].Time);
            var chosen = takeExisting ? existing[i++] : incoming[j++];
            if (merged.Count > 0 && merged[^1].Time == chosen.Time)
            {
                continue;
            }

            merged.Add(chosen);
        }

        return merged;
    }

    /// <summary>Strictly-ascending open times: no equal opens, no order drift.</summary>
    private static bool IsStrictlyAscending(IReadOnlyList<Bar> bars)
    {
        for (var i = 1; i < bars.Count; i++)
        {
            if (bars[i].Time <= bars[i - 1].Time)
            {
                return false;
            }
        }

        return true;
    }
}
