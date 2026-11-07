namespace ZettaTrade.App.Services;

using ZettaTrade.Core.ValueObjects;

/// <summary>
/// Round-size metadata lookup for the position sizing helpers: what the
/// app already holds per instrument. The seam exists so the order ticket's
/// sizing row can gate itself on real facts — an instrument without
/// metadata keeps the row hidden, and no sizing path ever invents a step.
/// The lookup is deliberately synchronous: metadata is a local registry
/// read, never a network resolve.
/// </summary>
public interface IInstrumentMetadataProvider
{
    /// <summary>Round-size metadata for the instrument, or null when the app holds none for it.</summary>
    /// <param name="symbol">The instrument to look up (matched by its normalized name).</param>
    /// <returns>The metadata found, or null — the caller keeps hidden / un-gated.</returns>
    InstrumentSizeMetadata? Find(Symbol symbol);
}
