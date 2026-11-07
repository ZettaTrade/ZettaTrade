namespace ZettaTrade.App.Services;

using ZettaTrade.Core.ValueObjects;

/// <summary>
/// One palette instrument: an adapter's symbol with the palette's identity
/// pieces precomputed once per snapshot — the serving exchange and the
/// uppercase match key the fuzzy scorer aligns against. Nothing is derived
/// per keystroke: a keystroke only scans these prepared keys.
/// </summary>
/// <param name="Symbol">The trading symbol in its normalized shape.</param>
/// <param name="ExchangeId">The serving adapter's id — the apply seam's switch key.</param>
/// <param name="ExchangeName">The serving adapter's display name — the row's right label.</param>
/// <param name="MatchKey">The uppercase normalized name the scorer matches (case is fixed at the source, never per query).</param>
public sealed record PaletteInstrument(
    Symbol Symbol,
    string ExchangeId,
    string ExchangeName,
    string MatchKey);
