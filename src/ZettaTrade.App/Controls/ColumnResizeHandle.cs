namespace ZettaTrade.App.Controls;

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;

/// <summary>
/// The workspace's own column splitter: a Thumb riding the narrow track
/// column between two columns, shifting space between them on drag so the
/// boundary follows the pointer. Avalonia's GridSplitter only ever writes
/// the FIRST fixed column when its neighbors are both fixed — on the
/// fixed-to-fixed workspace seams the grabbed line never moves, which
/// reads as a dead handle (the user report behind T-172). This handle
/// writes both neighbors and honors the standard direction: a rightward
/// drag grows the column left of the handle. A star neighbor absorbs: the
/// handle writes only the fixed side and the star takes the rest. The
/// parent grid's TwoWay column bindings carry every write into the
/// workspace's persisted widths, and the drag-settle pipelines persist
/// them (the T-038 shape, unchanged).
/// </summary>
public class ColumnResizeHandle : Thumb
{
    /// <summary>Keyboard step for the arrow-key moves (logical pixels).</summary>
    public static readonly StyledProperty<double> KeyboardIncrementProperty =
        AvaloniaProperty.Register<ColumnResizeHandle, double>(nameof(KeyboardIncrement), 10d);

    /// <summary>The captured drag geometry, taken once at drag start.</summary>
    private DragState? _drag;

    /// <summary>
    /// The arrow-key step: each press moves the boundary by this many
    /// logical pixels (the GridSplitter parity the accessibility work
    /// relies on).
    /// </summary>
    public double KeyboardIncrement
    {
        get => GetValue(KeyboardIncrementProperty);
        set => SetValue(KeyboardIncrementProperty, value);
    }

    /// <summary>
    /// Begin one drag: capture the neighbors' current geometry once, so a
    /// drag burst reads stable start widths. A handle outside a grid or
    /// without two neighbor columns is a composition error — fail loud
    /// (no silent dead handles).
    /// </summary>
    private DragState Capture()
    {
        var grid = Parent as Grid
            ?? throw new InvalidOperationException(
                "ColumnResizeHandle must sit directly inside a Grid.");
        var index = Grid.GetColumn(this);
        if (index <= 0 || index >= grid.ColumnDefinitions.Count - 1)
        {
            throw new InvalidOperationException(
                $"ColumnResizeHandle at column {index} lacks two neighbor columns to resize.");
        }

        var state = new DragState(
            grid.ColumnDefinitions[index - 1],
            grid.ColumnDefinitions[index + 1]);
        _drag = state;
        return state;
    }

    /// <inheritdoc />
    protected override void OnDragStarted(VectorEventArgs e)
    {
        base.OnDragStarted(e);
        Capture();
    }

    /// <inheritdoc />
    protected override void OnDragDelta(VectorEventArgs e)
    {
        base.OnDragDelta(e);
        if (_drag is not null)
        {
            Apply(Math.Round(e.Vector.X), _drag);
        }
    }

    /// <inheritdoc />
    protected override void OnDragCompleted(VectorEventArgs e)
    {
        base.OnDragCompleted(e);
        _drag = null;
    }

    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.Key)
        {
            case Key.Left:
                Apply(-KeyboardIncrement, Capture());
                _drag = null;
                e.Handled = true;
                break;
            case Key.Right:
                Apply(KeyboardIncrement, Capture());
                _drag = null;
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// Move the boundary by the delta under the standard direction (a
    /// rightward drag grows the left column). Each event reads the
    /// neighbors' CURRENT widths — Thumb reports per-event deltas, so the
    /// writes accumulate across the sweep. A star side absorbs — the
    /// handle writes only the fixed side; two fixed sides trade the delta
    /// under their joint minimums.
    /// </summary>
    private static void Apply(double delta, DragState state)
    {
        if (delta == 0)
        {
            return;
        }

        if (state.LeftIsStar && state.RightIsStar)
        {
            throw new InvalidOperationException(
                "ColumnResizeHandle between two star columns has no workspace shape.");
        }

        if (state.LeftIsStar)
        {
            // The star absorbs: only the fixed right column carries the move.
            var right = Clamp(state.Right.ActualWidth - delta, state.RightMin, state.RightMax);
            state.Right.SetValue(ColumnDefinition.WidthProperty, new GridLength(right));
            return;
        }

        if (state.RightIsStar)
        {
            var left = Clamp(state.Left.ActualWidth + delta, state.LeftMin, state.LeftMax);
            state.Left.SetValue(ColumnDefinition.WidthProperty, new GridLength(left));
            return;
        }

        // Both fixed: the boundary moves — left takes the delta, right pays
        // it, with the joint clamp so neither side crosses its minimum.
        var leftNow = state.Left.ActualWidth;
        var rightNow = state.Right.ActualWidth;
        var d = Math.Min(delta, state.LeftMax - leftNow);
        d = Math.Min(d, rightNow - state.RightMin);
        d = Math.Max(d, state.LeftMin - leftNow);
        d = Math.Max(d, rightNow - state.RightMax);

        state.Left.SetValue(ColumnDefinition.WidthProperty, new GridLength(leftNow + d));
        state.Right.SetValue(ColumnDefinition.WidthProperty, new GridLength(rightNow - d));
    }

    private static double Clamp(double value, double min, double max) =>
        Math.Min(Math.Max(value, min), max);

    /// <summary>The once-captured drag geometry: the neighbors and their size clamps.</summary>
    private sealed class DragState
    {
        public DragState(ColumnDefinition left, ColumnDefinition right)
        {
            Left = left;
            Right = right;
            LeftIsStar = left.Width.IsStar;
            RightIsStar = right.Width.IsStar;
            LeftMin = left.MinWidth;
            RightMin = right.MinWidth;
            LeftMax = double.IsNaN(left.MaxWidth) ? double.PositiveInfinity : left.MaxWidth;
            RightMax = double.IsNaN(right.MaxWidth) ? double.PositiveInfinity : right.MaxWidth;
        }

        public ColumnDefinition Left { get; }

        public ColumnDefinition Right { get; }

        public bool LeftIsStar { get; }

        public bool RightIsStar { get; }

        public double LeftMin { get; }

        public double RightMin { get; }

        public double LeftMax { get; }

        public double RightMax { get; }
    }
}
