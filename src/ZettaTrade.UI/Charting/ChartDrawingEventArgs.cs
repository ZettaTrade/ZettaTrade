namespace ZettaTrade.UI.Charting;

using ZettaTrade.Charting;

/// <summary>
/// The drawings layer's interaction event payload (T-091): which drawing
/// (identity — present when the event names an existing drawing, null when
/// the event carries a fresh placement) and the anchors involved. The
/// ViewModel consumes the commit/edit events, persists them and publishes
/// the layer back through the <see cref="CandlestickChart.Drawings"/>
/// property.
/// </summary>
public sealed class ChartDrawingEventArgs : EventArgs
{
    /// <summary>The event's drawing identity (null for a fresh commit naming no store identity yet).</summary>
    public Guid? DrawingId { get; }

    /// <summary>The committed or edited kind.</summary>
    public ChartDrawingKind Kind { get; }

    /// <summary>The committed or edited anchors.</summary>
    public IReadOnlyList<ChartDrawingAnchor> Anchors { get; }

    /// <summary>Builds the event args.</summary>
    public ChartDrawingEventArgs(Guid? drawingId, ChartDrawingKind kind, IReadOnlyList<ChartDrawingAnchor> anchors)
    {
        DrawingId = drawingId;
        Kind = kind;
        Anchors = anchors;
    }
}
