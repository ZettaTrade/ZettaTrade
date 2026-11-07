namespace ZettaTrade.App.Services;

using System.Collections.Generic;

/// <summary>
/// A behavioral snapshot of the palette's instrument source: the flat list
/// over ALL registered adapters (registration order, each adapter's own
/// order kept) and the settled flag separating "the attempt finished" from
/// "nothing completed yet" — the flag that keeps the palette honest about
/// loading versus genuinely empty.
/// </summary>
/// <remarks>
/// A faulting adapter counts as settled-zero contributions (its rows are
/// simply absent; the other adapters' rows survive), per the no-defaults
/// rule: a broken adapter never invents instruments, only silence.
/// A later refresh replaces the aggregate wholesale; while one runs, the
/// previous snapshot stays the subject's current value — each adapter's
/// list is self-contained, so the previous rows stay honest for as long
/// as they hang around.
/// </remarks>
/// <param name="Instruments">The aggregated instruments, in registration order.</param>
/// <param name="Settled">Whether at least one fetch attempt completed; a first-attempt failure publishes settled-zero.</param>
public sealed record PaletteSnapshot(
    IReadOnlyList<PaletteInstrument> Instruments,
    bool Settled);
