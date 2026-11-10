namespace ZettaTrade.Charting;

/// <summary>
/// The placement state machine (T-091): the pure core of placing one
/// drawing over one press→move→release gesture. The control feeds it
/// pointer facts already mapped to (time, price) anchors and reads back
/// the preview state and the commit marker; the machine owns NOTHING about
/// pixels, stores or the ViewModel.
///
/// The flow (every tool, one uniform gesture): the press plants anchor #1,
/// the pointer's moves preview the second corner live, the release lands
/// the second corner (the level rides its press anchor alone) and the
/// machine validates the geometry — a degenerate drawing (duplicated
/// anchors, backwards times) is reported as a cancel, never placed. The
/// tool system wide STAYS armed after a commit — the trader draws several
/// levels in a row — and disarms only through the palette or Escape.
/// </summary>
public sealed class ChartDrawingPlacement
{
    /// <summary>The armed tool; <see cref="ChartDrawingTool.None"/> means no placement is running.</summary>
    public ChartDrawingTool Tool { get; private set; } = ChartDrawingTool.None;

    /// <summary>True while the pointer is held down (anchor #1 planted, the preview follows).</summary>
    public bool Dragging { get; private set; }

    /// <summary>The preview's second anchor: present only while dragging.</summary>
    public ChartDrawingAnchor? PreviewAnchor => Dragging ? _preview : null;

    /// <summary>
    /// The gesture's in-flight anchors for the preview rendering: the
    /// planted first plus the following preview while dragging (the two-
    /// corner tools render both), the planted anchor alone for the level,
    /// empty otherwise.
    /// </summary>
    public IReadOnlyList<ChartDrawingAnchor> PreviewAnchors => Dragging
        ? Tool == ChartDrawingTool.HorizontalLevel
            ? new[] { _first }
            : new[] { _first, _preview }
        : [];

    private ChartDrawingAnchor _preview;
    private ChartDrawingAnchor _first;

    /// <summary>Arms a tool (or re-arms on a switch): the state resets, the next press plants anchor #1.</summary>
    public void BeginTool(ChartDrawingTool tool)
    {
        Tool = tool;
        Dragging = false;
        _preview = default;
        _first = default;
    }

    /// <summary>
    /// A press while armed: anchor #1 lands. A press while the mode rests
    /// is ignored (the chart's usual pan/hover stays in charge).
    /// </summary>
    public ChartPlacementEvent OnPress(ChartDrawingAnchor anchor)
    {
        if (Tool == ChartDrawingTool.None)
        {
            return ChartPlacementEvent.Ignored;
        }

        _first = anchor;
        _preview = anchor;
        Dragging = true;
        return ChartPlacementEvent.PreviewStarted;
    }

    /// <summary>The pointer's move while dragging: the previewed corner follows it.</summary>
    public void OnMove(ChartDrawingAnchor anchor)
    {
        if (Dragging)
        {
            _preview = anchor;
        }
    }

    /// <summary>
    /// The release ends the gesture: the level commits its single anchor,
    /// the two-corner tools commit press+release as their corners — unless
    /// the geometry degenerated (a zero-length drag), which reports a
    /// cancel and returns to armed-with-empty (the user retries).
    /// </summary>
    public ChartPlacementEvent OnRelease(ChartDrawingAnchor anchor)
    {
        if (!Dragging || Tool == ChartDrawingTool.None)
        {
            return ChartPlacementEvent.Ignored;
        }

        Dragging = false;

        var anchors = Tool == ChartDrawingTool.HorizontalLevel
            ? new[] { _first }
            : new[] { _first, anchor };

        try
        {
            ChartDrawing.Validate(Tool.ToKind(), anchors);
        }
        catch (ArgumentException)
        {
            return ChartPlacementEvent.CancelledDegenerate;
        }

        var drawing = new ChartDrawing { Kind = Tool.ToKind(), Anchors = anchors };
        return new ChartPlacementEvent.Placed(drawing);
    }

    /// <summary>Discards the in-flight gesture (Escape) — the tool stays armed.</summary>
    public void Cancel()
    {
        Dragging = false;
        _preview = default;
        _first = default;
    }
}

/// <summary>
/// The placement machine's outcomes: nothing happened, the preview state
/// changed, the gesture collapsed into a degenerate geometry (cancelled —
/// e.g. both corners on the same spot), or a drawing was placed.
/// </summary>
public abstract record ChartPlacementEvent
{
    /// <summary>The input did not belong to a running placement.</summary>
    public static readonly ChartPlacementEvent Ignored = new IgnoredEvent();

    /// <summary>The preview started (anchor #1 in, the previewed corner follows the pointer).</summary>
    public static readonly ChartPlacementEvent PreviewStarted = new PreviewStartedEvent();

    /// <summary>The gesture collapsed: a degenerate drawing was never placed.</summary>
    public static readonly ChartPlacementEvent CancelledDegenerate = new CancelledEvent();

    /// <summary>The drawing is complete and ready for the VM (which persists it and republishes the layer).</summary>
    public sealed record Placed(ChartDrawing Drawing) : ChartPlacementEvent;

    private sealed record IgnoredEvent : ChartPlacementEvent;

    private sealed record PreviewStartedEvent : ChartPlacementEvent;

    private sealed record CancelledEvent : ChartPlacementEvent;
}
