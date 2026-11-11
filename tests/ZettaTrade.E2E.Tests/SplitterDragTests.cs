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

    /// <summary>Drag an element by a screen delta with the real mouse. The
    /// delta rides small steps with a settle beat after the press: the Thumb
    /// must capture before the first move lands, and a sweep of small moves
    /// never loses the gesture to input coalescing under suite load.</summary>
    private static void DragBy(AutomationElement handle, int dx, int dy)
    {
        var rect = handle.BoundingRectangle;
        Mouse.Position = new System.Drawing.Point(
            rect.Left + (rect.Width / 2),
            rect.Top + (rect.Height / 2));
        Mouse.Down(MouseButton.Left);
        System.Threading.Thread.Sleep(60);

        var steps = 8;
        var movedX = 0;
        var movedY = 0;
        for (var step = 1; step <= steps; step++)
        {
            var targetX = dx * step / steps;
            var targetY = dy * step / steps;
            Mouse.MoveBy(targetX - movedX, targetY - movedY);
            movedX = targetX;
            movedY = targetY;
        }

        System.Threading.Thread.Sleep(60);
        Mouse.Up(MouseButton.Left);
        System.Threading.Thread.Sleep(60);
    }
}
