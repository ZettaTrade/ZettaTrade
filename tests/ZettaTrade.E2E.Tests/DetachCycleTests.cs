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
    /// <summary>
    /// The detach-cycle waits' budget (T-221): sized against the observed
    /// variance, not the idle-box default. Measured on a warm box, the
    /// collapse settles under 2 s after the toggle and the attach re-widen
    /// under a quarter second, but each poll pays a cross-process UIA walk
    /// (0.25-0.8 s over the main window, ~1.6 s over the desktop) and under
    /// a full-suite gate the app's UI thread serves those walks between its
    /// own work, so every leg can lag several times the isolated figure.
    /// The legacy 6 s bound made the matter worse by sizing the whole wait
    /// around ONE poisoned iteration, so the family burned the gate's flaky
    /// retry on both 2026-11-13 review-round passes. 20 s (the T-216
    /// multiplier over the observed exceedance) keeps every wait bounded:
    /// a genuinely stuck detach still fails inside the run, never hangs.
    /// </summary>
    private static readonly TimeSpan DetachCycleWaitTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// The detach-cycle waits' poll cadence: the workspace re-render cadence
    /// the column collapse and re-widen settle on. Polling faster only
    /// multiplies the cross-process walk cost each iteration carries, so the
    /// cadence stays at one workspace beat.
    /// </summary>
    private static readonly TimeSpan DetachCycleWaitInterval = TimeSpan.FromMilliseconds(250);

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

        // The mirror's own citizen carries the verdict: the column's width
        // goes to zero with the detach (the T-173 geometry). The resize
        // handle is deliberately NOT consulted inside the wait: it hides
        // with the detach (IsVisible=!IsBookDetached), Avalonia drops the
        // hidden control from the automation tree, and the lookup would burn
        // its own full budget per iteration against a settled tree - the
        // legacy shape made one iteration outrun the whole wait (measured:
        // 6.3 s inside a 6 s budget). The handle's verdict rides the
        // failure message AFTER the wait instead.
        var lastWidth = double.NaN;
        var freed = RetryUntilTrue(() =>
        {
            var width = TryReadHostWidth();
            if (width is not { } value)
            {
                return false;
            }

            lastWidth = value;
            return value < 20;
        });
        if (freed)
        {
            freed.Should().BeTrue("the detach frees the main window's book column (the T-173 geometry)");
        }
        else
        {
            // The diagnostic lookup costs its own full budget when the
            // handle is legitimately gone, so it runs on the failing run
            // only - the happy path never pays for the message garnish.
            var handleSeen = _app.FindDescendantById(_app.MainWindow!, "BookResizeHandle") is not null;
            freed.Should().BeTrue(
                $"the detach frees the main window's book column (the T-173 geometry); last width {lastWidth:F1}, handle still present: {handleSeen}");
        }

        _app.ClickMenuItem("ViewMenu", "ViewBookMenuItem");
        _app.WaitForTopLevelWindowGone("DetachedBookWindow")
            .Should().BeTrue("the second toggle returns the panel inline");

        var restoredWidth = double.NaN;
        RetryUntilTrue(() =>
        {
            var width = TryReadHostWidth();
            if (width is not { } value)
            {
                return false;
            }

            restoredWidth = value;
            return value > 50;
        }).Should().BeTrue(
            $"the attach restores the column's live width; last width {restoredWidth:F1}");
    }

    /// <summary>
    /// Read the book host's physical width without ever throwing: a mid-read
    /// element death (the workspace re-renders exactly around the detach
    /// cycle) answers null and the wait retries, instead of FlaUI's default
    /// exception escape (ignoreException=false) failing the family on a
    /// healthy but re-rendering tree. A null host answers null the same way.
    /// </summary>
    private double? TryReadHostWidth()
    {
        var host = _app.FindDescendantById(_app.MainWindow!, "BookPanelHost");
        if (host is null)
        {
            return null;
        }

        try
        {
            return host.BoundingRectangle.Width;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>A bounded predicate retry for states the tree re-renders:
    /// the wait ends when the predicate turns true (FlaUI's WhileFalse
    /// route - WhileTrue waits the opposite way and would invert the
    /// verdict). The budget and cadence are the family's own (T-221): sized
    /// against the observed gate-mode variance, still bounded, never an
    /// unbounded wait.</summary>
    private static bool RetryUntilTrue(Func<bool> predicate) =>
        FlaUI.Core.Tools.Retry.WhileFalse(
                predicate,
                DetachCycleWaitTimeout,
                DetachCycleWaitInterval)
            .Success;
}
