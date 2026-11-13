namespace ZettaTrade.App.Services;

using System.Collections.Generic;
using ZettaTrade.Charting;

/// <summary>
/// One open position's footprint payload keyed by its normalized symbol
/// (T-122): the position the chart's workspace symbol resolves onto.
/// The feed publishes its trading lane's whole inventory; the chart VM
/// picks the one row matching ITS workspace symbol — the feed itself is
/// symbol-agnostic (no chart knowledge lives in the feed).
/// </summary>
/// <param name="Symbol">The normalized symbol the position carries.</param>
/// <param name="Overlay">The chart's drawing payload for the position.</param>
public sealed record FootprintPosition(string Symbol, PositionOverlay Overlay);

/// <summary>One working order's footprint payload keyed by its normalized symbol.</summary>
/// <param name="Symbol">The normalized symbol the order carries.</param>
/// <param name="Line">The chart's drawing payload for the order.</param>
public sealed record FootprintOrder(string Symbol, WorkingOrderLine Line);

/// <summary>One execution's footprint payload keyed by its normalized symbol.</summary>
/// <param name="Symbol">The normalized symbol the fill carries.</param>
/// <param name="Marker">The chart's drawing payload for the fill.</param>
public sealed record FootprintFill(string Symbol, FillMarker Marker);

/// <summary>
/// One working stop order's stop context (T-200): the stop price the
/// measure ruler's R-multiple row reads against, keyed by its normalized
/// symbol. The feed is symbol-agnostic — the chart VM picks its
/// workspace symbol's row and the nearest price.
/// </summary>
/// <param name="Symbol">The normalized symbol the stop order carries.</param>
/// <param name="Price">The stop order's stop trigger price.</param>
public sealed record FootprintStop(string Symbol, decimal Price);

/// <summary>
/// The complete footprint inventory of one trading lane, published whole
/// on every change: the overlays the chart's workspace symbol picks from.
/// Immutable arrays throughout — a published snapshot never mutates.
/// </summary>
public sealed record ChartFootprintSnapshot(
    IReadOnlyList<FootprintPosition> Positions,
    IReadOnlyList<FootprintOrder> Orders,
    IReadOnlyList<FootprintFill> Fills,
    IReadOnlyList<FootprintStop> Stops)
{
    /// <summary>The nothing-to-draw state before the first refresh lands.</summary>
    public static ChartFootprintSnapshot Empty { get; } = new([], [], [], []);
}
