namespace ZettaTrade.E2E.Tests;

using System;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FluentAssertions;
using Xunit;

/// <summary>
/// The workspace boundary drag over the REAL process (T-176): the
/// chart/book resize handle moves the persisted book column with the real
/// mouse - the T-172 discipline proven on the compiled app, not a
/// simulated pointer. The drag round-trips so the lane leaves the
/// workspace as it found it.
/// </summary>
[Collection("E2E")]
public sealed class SplitterDragTests
{
    private readonly E2EAppFixture _app;

    public SplitterDragTests(E2EAppFixture app) => _app = app;

    [WindowsFact]
    public void TheHandleDrag_MovesTheBookColumnAndBack()
    {
        var host = _app.FindDescendantById(_app.MainWindow!, "BookPanelHost");
        host.Should().NotBeNull("the book column renders");
        var before = host!.BoundingRectangle.Width;
        before.Should().BeGreaterThan(50, "the book column renders at its persisted width");

        var handle = _app.FindDescendantById(_app.MainWindow!, "BookResizeHandle");
        handle.Should().NotBeNull("the resize handle renders");
        // The handle's contract trades the two fixed columns: a rightward
        // drag grows the chart side, so the book column is paid by a
        // leftward drag.
        DragBy(handle!, -60, 0);

        var grown = _app.FindDescendantById(_app.MainWindow!, "BookPanelHost")!
            .BoundingRectangle.Width;
        grown.Should().BeGreaterThan(before + 30, "a leftward 60 px drag pays the book column's width");

        var handleAfter = _app.FindDescendantById(_app.MainWindow!, "BookResizeHandle");
        handleAfter.Should().NotBeNull("the handle survives the drag");
        DragBy(handleAfter!, 60, 0);

        var restored = _app.FindDescendantById(_app.MainWindow!, "BookPanelHost")!
            .BoundingRectangle.Width;
        Math.Abs(restored - before).Should().BeLessThan(
            15, "the drag round-trip restores the column within a pixel's slop");
    }

    /// <summary>Drag an element by a screen delta with the real mouse.</summary>
    private static void DragBy(AutomationElement handle, int dx, int dy)
    {
        var rect = handle.BoundingRectangle;
        Mouse.Position = new System.Drawing.Point(
            rect.Left + (rect.Width / 2),
            rect.Top + (rect.Height / 2));
        Mouse.Down(MouseButton.Left);
        Mouse.MoveBy(dx / 2, dy / 2);
        Mouse.MoveBy(dx - (dx / 2), dy - (dy / 2));
        Mouse.Up(MouseButton.Left);
    }
}
