namespace ZettaTrade.App.ViewModels;

/// <summary>
/// The workspace's chart-face layouts (T-201): how many chart panels the
/// workspace column shows and how they tile. The numeric order is the
/// View menu's display order; the persisted settings value is the enum
/// name (<c>UI.ChartLayout</c>), so a renumber stays out of stored files.
/// </summary>
public enum ChartLayoutMode
{
    /// <summary>One chart panel — the layout every session had before the grid existed.</summary>
    Single,

    /// <summary>Two panels stacked vertically: the primary on top, slot 1 under it.</summary>
    OneByTwo,

    /// <summary>Four panels tiled 2x2: the primary and slot 1 on the top row, slots 2 and 3 on the bottom row.</summary>
    TwoByTwo,
}
