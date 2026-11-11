namespace ZettaTrade.E2E.Tests;

using System;
using FluentAssertions;
using Xunit;

/// <summary>
/// The detach cycle over the REAL process (T-176): the View menu's book
/// toggle sends the panel into its own window and the main column frees,
/// the second toggle brings it back. One control instance, two hosts -
/// the T-103 move proven on the compiled app.
/// </summary>
[Collection("E2E")]
public sealed class DetachCycleTests
{
    private readonly E2EAppFixture _app;

    public DetachCycleTests(E2EAppFixture app) => _app = app;

    [WindowsFact]
    public void TheViewMenuBookToggle_DetachesAndReattachesThePanel()
    {
        var hostBefore = _app.FindDescendantById(_app.MainWindow!, "BookPanelHost");
        hostBefore.Should().NotBeNull("the book column renders before the cycle");
        var attachedWidth = hostBefore!.BoundingRectangle.Width;
        attachedWidth.Should().BeGreaterThan(50, "the book column renders before the cycle");

        _app.ClickMenuItem("ViewMenu", "ViewBookMenuItem");
        var detached = _app.FindTopLevelWindowById("DetachedBookWindow");
        detached.Should().NotBeNull("the toggle moves the panel into its own window");

        // The mirror's own citizens carry the verdict: the handle hides
        // with the detach (IsBookDetached) and the column's width goes to
        // zero (the T-173 geometry) - the message records both if the wait
        // ever expires.
        var lastWidth = double.NaN;
        var handleSeen = true;
        var freed = RetryUntilTrue(() =>
        {
            handleSeen = _app.FindDescendantById(_app.MainWindow!, "BookResizeHandle") is not null;
            var host = _app.FindDescendantById(_app.MainWindow!, "BookPanelHost");
            if (host is null)
            {
                return false;
            }

            lastWidth = host.BoundingRectangle.Width;
            return lastWidth < 20;
        });
        freed.Should().BeTrue(
            $"the detach frees the main window's book column (the T-173 geometry); last width {lastWidth:F1}, handle still present: {handleSeen}");

        _app.ClickMenuItem("ViewMenu", "ViewBookMenuItem");
        _app.WaitForTopLevelWindowGone("DetachedBookWindow")
            .Should().BeTrue("the second toggle returns the panel inline");

        RetryUntilTrue(() =>
        {
            var host = _app.FindDescendantById(_app.MainWindow!, "BookPanelHost");
            return host is not null && host.BoundingRectangle.Width > 50;
        }).Should().BeTrue("the attach restores the column's live width");
    }

    /// <summary>A bounded predicate retry for states the tree re-renders: the
    /// wait ends when the predicate turns true (FlaUI's WhileFalse route -
    /// WhileTrue waits the opposite way and would invert the verdict).</summary>
    private static bool RetryUntilTrue(Func<bool> predicate) =>
        FlaUI.Core.Tools.Retry.WhileFalse(
                predicate,
                TimeSpan.FromSeconds(6),
                TimeSpan.FromMilliseconds(200))
            .Success;
}
