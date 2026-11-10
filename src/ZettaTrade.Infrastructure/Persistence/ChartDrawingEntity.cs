namespace ZettaTrade.Infrastructure.Persistence;

using ZettaTrade.Core.Enums;

/// <summary>
/// One stored drawing-anchor row (SQLite, T-091): a drawing persists as
/// its kind plus its (time, price) anchors, one row per anchor in draw
/// order — the composite identity (drawing id, order) is the row key, so
/// replacing one drawing replaces its rows whole.
/// </summary>
public sealed class ChartDrawingEntity
{
    /// <summary>The drawing's identity; every anchor row of one drawing carries it.</summary>
    public required Guid DrawingId { get; set; }

    /// <summary>The anchor's draw order (0 = the first corner); part of the row key.</summary>
    public required int SortOrder { get; set; }

    /// <summary>Adapter identifier the drawing was drawn over.</summary>
    public required string AdapterId { get; set; }

    /// <summary>Normalized symbol (BASE/QUOTE) the drawing was drawn over.</summary>
    public required string Symbol { get; set; }

    /// <summary>Candle interval the drawing was drawn over.</summary>
    public required TimeFrame TimeFrame { get; set; }

    /// <summary>The drawing kind's enum name (the trustworthy round-trip is a parse-or-drop).</summary>
    public required string Kind { get; set; }

    /// <summary>The anchor's bar-open time (the drawings anchor to times, never bar indices).</summary>
    public required DateTimeOffset Time { get; set; }

    /// <summary>The anchor's price.</summary>
    public required decimal Price { get; set; }
}
