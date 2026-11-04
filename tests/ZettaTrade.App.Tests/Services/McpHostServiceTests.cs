namespace ZettaTrade.App.Tests.Services;

using System;
using System.Reactive.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using ZettaTrade.App;
using ZettaTrade.App.Services;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// The MCP host runner: the stored enable choice starts or stops the stdio
/// server (the walk applies the stored choice, the settings save applies the
/// flip live); a start the transport refuses surfaces as the Failed state and
/// keeps the stored choice in force for the next launch — nothing throws past
/// the surfaced state, and the state stream replays its current value.
/// </summary>
public sealed class McpHostServiceTests
{
    /// <summary>
    /// The walk with the disabled default (the shipped value) leaves the
    /// server stopped without touching it.
    /// </summary>
    [Fact]
    public async Task InitializeAsync_TheDisabledDefault_KeepsTheServerStopped()
    {
        var (host, service) = CreateService(persistedEnabled: false);

        await service.InitializeAsync();

        await host.DidNotReceiveWithAnyArgs().StartAsync(Arg.Any<CancellationToken>());
        service.IsRunning.Should().BeFalse();
        (await service.State.FirstAsync()).Should().Be(McpHostState.Stopped);
    }

    /// <summary>
    /// The stored enable choice starts the server at the walk, and the state
    /// surfaces the running truth.
    /// </summary>
    [Fact]
    public async Task InitializeAsync_TheStoredEnableChoice_StartsTheServer()
    {
        // The substitute claims running once its start succeeded.
        var host = Substitute.For<IMcpServerHost>();
        host.IsRunning.Returns(true);
        var service = CreateService(SettingsWith(persistedEnabled: true), host);

        await service.InitializeAsync();

        await host.Received(1).StartAsync(Arg.Any<CancellationToken>());
        service.IsRunning.Should().BeTrue();
        (await service.State.FirstAsync()).Should().Be(McpHostState.Running);
    }

    /// <summary>
    /// A start the transport refuses never throws past the surface: the state
    /// turns Failed and the stored choice stays behind for the next launch.
    /// </summary>
    [Fact]
    public async Task InitializeAsync_ARefusedStart_SurfacesTheDeferredState()
    {
        var host = Substitute.For<IMcpServerHost>();
        host.StartAsync(Arg.Any<CancellationToken>())
            .Returns(FailedTask(new InvalidOperationException("transport busy")));
        var service = CreateService(SettingsWith(persistedEnabled: true), host);

        await service.InitializeAsync();

        service.IsRunning.Should().BeFalse();
        (await service.State.FirstAsync()).Should().Be(McpHostState.Failed);
    }

    /// <summary>
    /// The settings save's live apply starts the server even though the walk
    /// left it stopped (one start, not the sum of the walk and the apply).
    /// </summary>
    [Fact]
    public async Task ApplyAsync_AnEnableFlipOnTheStoppedServer_StartsItLive()
    {
        var (host, service) = CreateService(persistedEnabled: false);
        await service.InitializeAsync();

        await service.ApplyAsync(true);

        await host.Received(1).StartAsync(Arg.Any<CancellationToken>());
        (await service.State.FirstAsync()).Should().Be(McpHostState.Running);
    }

    /// <summary>
    /// The disable flip stops a running server live.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_TheDisableFlipOnTheRunningServer_StopsItLive()
    {
        var host = Substitute.For<IMcpServerHost>();
        host.IsRunning.Returns(true);
        var service = CreateService(SettingsWith(persistedEnabled: true), host);
        await service.InitializeAsync();

        await service.ApplyAsync(false);

        await host.Received(1).StopAsync(Arg.Any<CancellationToken>());
        (await service.State.FirstAsync()).Should().Be(McpHostState.Stopped);
    }

    /// <summary>
    /// Disabling after a failed start still aims for the stopped truth: the
    /// stop reaches the host, and the state lands on the host's own claim.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_TheDisableFlipAfterAFailedStart_LandsOnTheHostsOwnClaim()
    {
        var host = Substitute.For<IMcpServerHost>();
        host.StartAsync(Arg.Any<CancellationToken>())
            .Returns(FailedTask(new InvalidOperationException("transport busy")));
        var service = CreateService(SettingsWith(persistedEnabled: true), host);
        await service.InitializeAsync();

        await service.ApplyAsync(false);

        await host.Received(1).StopAsync(Arg.Any<CancellationToken>());
        (await service.State.FirstAsync()).Should().Be(McpHostState.Stopped);
    }

    /// <summary>
    /// A resumed session's live host surfaces its own start state: the
    /// runner's replay carries the running truth until the stored choice
    /// applies.
    /// </summary>
    [Fact]
    public async Task TheLiveHost_ReplaysItsOwnStartValue()
    {
        var host = Substitute.For<IMcpServerHost>();
        host.IsRunning.Returns(true);
        // The runner is constructed uninitialized on purpose: the replay is
        // what the settings tab reads on the first subscribe.
        var service = CreateService(SettingsWith(persistedEnabled: false), host);

        service.IsRunning.Should().BeTrue();
        (await service.State.FirstAsync()).Should().Be(McpHostState.Running);
    }

    #region Factories

    private static (IMcpServerHost Host, McpHostService Service) CreateService(
        bool persistedEnabled)
    {
        var host = Substitute.For<IMcpServerHost>();
        var service = CreateService(SettingsWith(persistedEnabled), host);
        return (host, service);
    }

    private static McpHostService CreateService(
        ISettingsService settings,
        IMcpServerHost host) =>
        new McpHostService(
            settings,
            host,
            NullLogger<McpHostService>.Instance);

    private static ISettingsService SettingsWith(bool persistedEnabled)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetAsync<bool>(
                UiSettingsKeys.McpSection,
                UiSettingsKeys.McpServerEnabled,
                Arg.Any<bool>())
            .Returns(persistedEnabled);
        return settings;
    }

    /// <summary>A task-shaped failure the host simulate refuses with.</summary>
    private static Task FailedTask(Exception error) =>
        Task.FromException<object?>(error);

    #endregion
}
