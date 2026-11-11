namespace ZettaTrade.App.Controls;

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;

/// <summary>
/// The signal rail's own row splitter: a Thumb riding the narrow track row
/// between two rows, shifting space between them on drag so the boundary
/// follows the pointer — the T-172 ColumnResizeHandle's row-wise sibling.
/// The standard direction mirrors the columns': a downward drag grows the
/// row ABOVE the handle, so the rail below grows when the user drags the
/// boundary up toward the chart. A star neighbor absorbs: the handle
/// writes only the fixed side and the star takes the rest. The parent
/// grid's TwoWay row binding carries every write into the rail's persisted
/// height, and the drag-settle pipeline persists it (the T-038 shape,
/// unchanged).
/// </summary>
public class RowResizeHandle : Thumb
{
    /// <summary>Keyboard step for the arrow-key moves (logical pixels).</summary>
    public static readonly StyledProperty<double> KeyboardIncrementProperty =
        AvaloniaProperty.Register<RowResizeHandle, double>(nameof(KeyboardIncrement), 10d);

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
    /// drag burst reads stable start heights. A handle outside a grid or
    /// without two neighbor rows is a composition error — fail loud
    /// (no silent dead handles).
    /// </summary>
    private DragState Capture()
    {
        var grid = Parent as Grid
            ?? throw new InvalidOperationException(
                "RowResizeHandle must sit directly inside a Grid.");
        var index = Grid.GetRow(this);
        if (index <= 0 || index >= grid.RowDefinitions.Count - 1)
        {
            throw new InvalidOperationException(
                $"RowResizeHandle at row {index} lacks two neighbor rows to resize.");
        }

        var state = new DragState(
            grid.RowDefinitions[index - 1],
            grid.RowDefinitions[index + 1]);
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
            Apply(Math.Round(e.Vector.Y), _drag);
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
            case Key.Up:
                // The boundary moves up: the rail below it grows.
                Apply(-KeyboardIncrement, Capture());
                _drag = null;
                e.Handled = true;
                break;
            case Key.Down:
                Apply(KeyboardIncrement, Capture());
                _drag = null;
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// Move the boundary by the delta under the standard direction (a
    /// downward drag grows the top row). Each event reads the neighbors'
    /// CURRENT heights — Thumb reports per-event deltas, so the writes
    /// accumulate across the sweep. A star side absorbs — the handle
    /// writes only the fixed side; two fixed sides trade the delta under
    /// their joint minimums.
    /// </summary>
    private static void Apply(double delta, DragState state)
    {
        if (delta == 0)
        {
            return;
        }

        if (state.TopIsStar && state.BottomIsStar)
        {
            throw new InvalidOperationException(
                "RowResizeHandle between two star rows has no workspace shape.");
        }

        if (state.TopIsStar)
        {
            // The star absorbs: only the fixed bottom row carries the move.
            var bottom = Clamp(state.Bottom.ActualHeight - delta, state.BottomMin, state.BottomMax);
            state.Bottom.SetValue(RowDefinition.HeightProperty, new GridLength(bottom));
            return;
        }

        if (state.BottomIsStar)
        {
            var top = Clamp(state.Top.ActualHeight + delta, state.TopMin, state.TopMax);
            state.Top.SetValue(RowDefinition.HeightProperty, new GridLength(top));
            return;
        }

        // Both fixed: the boundary moves — top takes the delta, bottom pays
        // it, with the joint clamp so neither side crosses its minimum.
        var topNow = state.Top.ActualHeight;
        var bottomNow = state.Bottom.ActualHeight;
        var d = Math.Min(delta, state.TopMax - topNow);
        d = Math.Min(d, bottomNow - state.BottomMin);
        d = Math.Max(d, state.TopMin - topNow);
        d = Math.Max(d, bottomNow - state.BottomMax);

        state.Top.SetValue(RowDefinition.HeightProperty, new GridLength(topNow + d));
        state.Bottom.SetValue(RowDefinition.HeightProperty, new GridLength(bottomNow - d));
    }

    private static double Clamp(double value, double min, double max) =>
        Math.Min(Math.Max(value, min), max);

    /// <summary>The once-captured drag geometry: the neighbors and their size clamps.</summary>
    private sealed class DragState
    {
        public DragState(RowDefinition top, RowDefinition bottom)
        {
            Top = top;
            Bottom = bottom;
            TopIsStar = top.Height.IsStar;
            BottomIsStar = bottom.Height.IsStar;
            TopMin = top.MinHeight;
            BottomMin = bottom.MinHeight;
            TopMax = double.IsNaN(top.MaxHeight) ? double.PositiveInfinity : top.MaxHeight;
            BottomMax = double.IsNaN(bottom.MaxHeight) ? double.PositiveInfinity : bottom.MaxHeight;
        }

        public RowDefinition Top { get; }

        public RowDefinition Bottom { get; }

        public bool TopIsStar { get; }

        public bool BottomIsStar { get; }

        public double TopMin { get; }

        public double BottomMin { get; }

        public double TopMax { get; }

        public double BottomMax { get; }
    }
}
