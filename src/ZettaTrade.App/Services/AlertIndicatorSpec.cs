namespace ZettaTrade.App.Services;

/// <summary>
/// The indicator-cross alert's own facts (T-203): which loaded plugin
/// indicator the condition reads, the period the catalog builds it with,
/// what crosses what (<see cref="AlertCrossSource"/>) and the fixed level
/// the indicator's value compares with (unused for the price source — the
/// moving level there is the indicator's own value; the neutral zero stays
/// in the stored slot, the demark is the source, never a silent guess).
/// A pure data record the draft composes, the definition carries and the
/// codec stores verbatim.
/// </summary>
/// <param name="Name">The indicator's canonical plugin name ("SMA") — the catalog resolves it.</param>
/// <param name="Period">The period the catalog builds the implementation with (strictly positive).</param>
/// <param name="Source">What crosses what.</param>
/// <param name="Level">The fixed level the indicator's value crosses (the indicator-value source's fact).</param>
public sealed record AlertIndicatorSpec(
    string Name,
    int Period,
    AlertCrossSource Source,
    decimal Level);
