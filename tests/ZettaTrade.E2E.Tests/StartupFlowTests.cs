namespace ZettaTrade.E2E.Tests;

using FluentAssertions;
using Xunit;

/// <summary>
/// The startup flow over the REAL process (T-176): the compiled app
/// launches, the main window renders the workspace's fixed citizens (the
/// menu bar, the three panel hosts, the resize handle) and stays alive -
/// the whole app lifecycle in the shape a user experiences it.
/// </summary>
[Collection("E2E")]
public sealed class StartupFlowTests
{
    private readonly E2EAppFixture _app;

    public StartupFlowTests(E2EAppFixture app) => _app = app;

    [WindowsFact]
    public void TheRealLaunch_ShowsTheWorkspaceAndStaysAlive()
    {
        var window = _app.MainWindow!;
        window.Title.Should().Be("ZettaTrade");

        _app.FindDescendantById(window, "FileMenu")
            .Should().NotBeNull("the menu bar is the workspace's fixed header row");
        _app.FindDescendantById(window, "ChartPanelHost")
            .Should().NotBeNull("the chart column renders at startup");
        _app.FindDescendantById(window, "BookPanelHost")
            .Should().NotBeNull("the book column renders at startup");
        _app.FindDescendantById(window, "AssistantPanelHost")
            .Should().NotBeNull("the assistant column renders at startup");
        _app.FindDescendantById(window, "BookResizeHandle")
            .Should().NotBeNull("the workspace boundary drag affordance renders with its column");

        window.IsOffscreen.Should().BeFalse("the launched window shows, not parks off-screen");
    }
}
