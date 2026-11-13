namespace ZettaTrade.Charting;

/// <summary>
/// The chart drawing entity (T-091): one tool-shaped annotation over the
/// price-time plane, anchored to (bar-open time, price) pairs. The identity
/// rides a <see cref="Guid"/> so the store, the layer and the control agree
/// on which drawing is selected without fighting surrounding entries.
/// Anchors must stay ascending by time — the placement flow writes them in
/// pointer order, drag/edit reorders on the edit, and validation rejects
/// anything else loudly (a silently flipped segment would read backwards).
/// </summary>
public sealed record ChartDrawing
{
    /// <summary>The drawing's stable identity.</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Which tool placed the drawing.</summary>
    public required ChartDrawingKind Kind { get; init; }

    /// <summary>The (time, price) anchors, ascending by time.</summary>
    public required IReadOnlyList<ChartDrawingAnchor> Anchors { get; init; }

    /// <summary>
    /// The text a <see cref="ChartDrawingKind.Text"/> annotation renders at
    /// its anchor (T-200); every other kind carries null. The string ships
    /// with the drawing through the store (the annotation prompt's answer)
    /// — an annotation without text draws its marker alone.
    /// </summary>
    public string? Text { get; init; }

    /// <summary>
    /// The exact anchor count each tool needs: a horizontal level pins one
    /// price; the trend line and the retracement carry two corners (the
    /// retracement's levels derive from both); a text annotation pins one
    /// spot; the zone's two corners bound the band; the measure ruler's two
    /// ends frame the read move.
    /// </summary>
    public static int AnchorCount(ChartDrawingKind kind) => kind switch
    {
        ChartDrawingKind.TrendLine => 2,
        ChartDrawingKind.HorizontalLevel => 1,
        ChartDrawingKind.FibRetracement => 2,
        ChartDrawingKind.Zone => 2,
        ChartDrawingKind.Text => 1,
        ChartDrawingKind.Measure => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown drawing kind."),
    };

    /// <summary>
    /// Validation over one drawing's anchor list (the kind checked too):
    /// the tool's anchor count, ascending times and no duplicate anchors
    /// (a zero-length segment is a placement bug, not a drawing). Throws
    /// with the offending rule named, so the callers' drop paths can log
    /// an honest reason.
    /// </summary>
    public static void Validate(ChartDrawingKind kind, IReadOnlyList<ChartDrawingAnchor> anchors)
    {
        var expected = AnchorCount(kind);
        if (anchors.Count != expected)
        {
            throw new ArgumentException(
                $"A {kind} drawing carries {anchors.Count} anchor(s); the tool places exactly {expected}.");
        }

        for (var i = 1; i < anchors.Count; i++)
        {
            if (anchors[i].Time < anchors[i - 1].Time)
            {
                throw new ArgumentException("Drawing anchors must stay ascending by time.");
            }

            if (anchors[i] == anchors[i - 1])
            {
                throw new ArgumentException("A drawing carries duplicate anchors.");
            }
        }
    }
}

/// <summary>
/// One drawing anchor: the bar-open time the anchor clings to (a pan
/// prepend or a series rebase keeps the time standing) plus the price it
/// sits on.
/// </summary>
public readonly record struct ChartDrawingAnchor(DateTimeOffset Time, decimal Price);
