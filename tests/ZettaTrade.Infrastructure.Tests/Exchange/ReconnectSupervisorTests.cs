namespace ZettaTrade.Infrastructure.Tests.Exchange;

using System;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ZettaTrade.Infrastructure.Exchange;

/// <summary>
/// The reconnect supervisor's contract (T-105): policy registration, the
/// sequential report fold into per-adapter snapshots, the hot state stream
/// (subscribed BEFORE anything is published — the standing Rx-test hazard
/// rule) and the quiet disposal. All waits are bounded; nothing blocks.
/// </summary>
public sealed class ReconnectSupervisorTests
{
    #region Constants

    /// <summary>Bounded wait for any expected observable emission.</summary>
    private static readonly TimeSpan EventWait = TimeSpan.FromSeconds(5);

    #endregion

    #region Tests: registration

    /// <summary>
    /// For registers the policy and seeds the Disconnected state (the seed
    /// happens synchronously inside the binding, no pump involved).
    /// </summary>
    [Fact]
    public async Task For_RegistersThePolicyAndSeedsTheDisconnectedState()
    {
        await using var supervisor = CreateSupervisor();
        var policy = ReconnectPolicy.Default;

        supervisor.For("binance", policy);

        supervisor.IsRegistered("binance").Should().BeTrue();
        supervisor.IsRegistered("bybit").Should().BeFalse();

        var state = supervisor.Snapshot.Single(entry => entry.AdapterId == "binance");
        state.Policy.Should().BeSameAs(policy);
        state.Phase.Should().Be(ReconnectionPhase.Disconnected);
        state.ConsecutiveFailures.Should().Be(0);
    }

    /// <summary>A blank adapter id or a null policy is a binding error, never a tolerated default.</summary>
    [Fact]
    public async Task For_InvalidBinding_Throws()
    {
        await using var supervisor = CreateSupervisor();

        Assert.Throws<ArgumentException>(() => supervisor.For(string.Empty, ReconnectPolicy.Default));
        Assert.Throws<ArgumentException>(() => supervisor.For("   ", ReconnectPolicy.Default));
        Assert.Throws<ArgumentNullException>(() => supervisor.For("binance", null!));
    }

    #endregion

    #region Tests: report fold

    /// <summary>
    /// Reports fold into the snapshot in publish order: the failing streak
    /// counts up, a successful open resets it and the force-cycle lands its
    /// own phase. Every step is awaited through the hot stream (subscribed
    /// BEFORE any publish — the completion signal of the pump's fold).
    /// </summary>
    [Fact]
    public async Task Publish_FoldsReportsIntoTheSnapshotInThePublishedOrder()
    {
        await using var supervisor = CreateSupervisor();
        var sink = supervisor.For("binance", ReconnectPolicy.Default);
        var now = DateTimeOffset.UtcNow;

        var statesTask = CollectStatesAsync(supervisor.StateStream, 4);
        sink.Publish(new ReconnectReport(ReconnectionPhase.Connecting, now, 0, null));
        sink.Publish(new ReconnectReport(ReconnectionPhase.Reconnecting, now.AddMilliseconds(10), 1, TimeSpan.FromMilliseconds(100)));
        sink.Publish(new ReconnectReport(ReconnectionPhase.Connected, now.AddMilliseconds(20), 0, null));
        sink.Publish(new ReconnectReport(ReconnectionPhase.ForceCycling, now.AddMilliseconds(30), 0, TimeSpan.FromMilliseconds(500)));

        var states = await statesTask.WaitAsync(EventWait);

        states.Select(state => state.Phase)
            .Should()
            .Equal(
                ReconnectionPhase.Connecting,
                ReconnectionPhase.Reconnecting,
                ReconnectionPhase.Connected,
                ReconnectionPhase.ForceCycling);
        states.Select(state => state.ConsecutiveFailures)
            .Should()
            .Equal(0, 1, 0, 0);
        states.Select(state => state.UpdatedUtc)
            .Should()
            .Equal(now, now.AddMilliseconds(10), now.AddMilliseconds(20), now.AddMilliseconds(30));
    }

    /// <summary>The final snapshot always shows the LAST applied report (a fresh copy per read, not a shared registry).</summary>
    [Fact]
    public async Task Snapshot_ReflectsTheLatestAppliedReportAsACopy()
    {
        await using var supervisor = CreateSupervisor();
        var sink = supervisor.For("bybit", ReconnectPolicy.Default);

        var connectedTask = CollectStatesAsync(supervisor.StateStream, 1);
        sink.Publish(new ReconnectReport(ReconnectionPhase.Connected, DateTimeOffset.UtcNow, 0, null));
        (await connectedTask.WaitAsync(EventWait))[0].Phase.Should().Be(ReconnectionPhase.Connected);

        supervisor.Snapshot.Single(entry => entry.AdapterId == "bybit").Phase
            .Should().Be(ReconnectionPhase.Connected);

        var reconnectingTask = CollectStatesAsync(supervisor.StateStream, 1);
        sink.Publish(new ReconnectReport(ReconnectionPhase.Reconnecting, DateTimeOffset.UtcNow, 3, TimeSpan.FromSeconds(20)));
        (await reconnectingTask.WaitAsync(EventWait))[0].ConsecutiveFailures.Should().Be(3);

        supervisor.Snapshot.Single(entry => entry.AdapterId == "bybit")
            .ConsecutiveFailures.Should().Be(3);
    }

    /// <summary>
    /// Two adapters fold independently: one transport's failure streak never
    /// bleeds into the neighbor's state — and the snapshot keeps the adapter
    /// ids in ordinal order (the stable order the health strip renders).
    /// </summary>
    [Fact]
    public async Task Publish_AdaptersFoldIndependently()
    {
        await using var supervisor = CreateSupervisor();
        var binanceSink = supervisor.For("binance", ReconnectPolicy.Default);
        var bybitSink = supervisor.For("bybit", ReconnectPolicy.Default);

        var statesTask = CollectStatesAsync(supervisor.StateStream, 2);
        binanceSink.Publish(new ReconnectReport(ReconnectionPhase.Reconnecting, DateTimeOffset.UtcNow, 4, TimeSpan.FromSeconds(30)));
        bybitSink.Publish(new ReconnectReport(ReconnectionPhase.Connected, DateTimeOffset.UtcNow, 0, null));

        var states = await statesTask.WaitAsync(EventWait);

        var binance = states.Single(entry => entry.AdapterId == "binance");
        binance.Phase.Should().Be(ReconnectionPhase.Reconnecting);
        binance.ConsecutiveFailures.Should().Be(4);

        var bybit = states.Single(entry => entry.AdapterId == "bybit");
        bybit.Phase.Should().Be(ReconnectionPhase.Connected);
        bybit.ConsecutiveFailures.Should().Be(0);

        supervisor.Snapshot.Select(entry => entry.AdapterId)
            .Should()
            .Equal("binance", "bybit");
    }

    #endregion

    #region Tests: disposal

    /// <summary>
    /// Disposal is quiet, a report after disposal is absorbed (the
    /// never-throws publish contract) and the pre-dispose state stands
    /// frozen: the pump is gone, so nothing can ever change the snapshot.
    /// </summary>
    [Fact]
    public async Task Dispose_StopsThePumpAndAbsorbsLateReportsQuietly()
    {
        var supervisor = CreateSupervisor();
        var sink = supervisor.For("binance", ReconnectPolicy.Default);

        var dispose = () => supervisor.DisposeAsync().AsTask();
        await dispose.Should().NotThrowAsync();

        Action latePublish = () => sink.Publish(
            new ReconnectReport(
                ReconnectionPhase.Reconnecting,
                DateTimeOffset.UtcNow,
                3,
                TimeSpan.FromMilliseconds(250)));
        latePublish.Should().NotThrow();

        var state = supervisor.Snapshot.Single(entry => entry.AdapterId == "binance");
        state.Phase.Should().Be(ReconnectionPhase.Disconnected);
        state.ConsecutiveFailures.Should().Be(0);
    }

    #endregion

    #region Helpers

    private static ReconnectSupervisor CreateSupervisor() =>
        new(NullLogger<ReconnectSupervisor>.Instance);

    /// <summary>
    /// Materialize the first <paramref name="count"/> stream emissions into a
    /// bounded task — subscribed BEFORE the caller triggers any publish (the
    /// hot-Subject timing hazard rule of the test suite).
    /// </summary>
    private static Task<IList<AdapterConnectionState>> CollectStatesAsync(
        IObservable<AdapterConnectionState> source,
        int count) =>
        source
            .Take(count)
            .ToList()
            .ToTask(TaskCreationOptions.RunContinuationsAsynchronously);

    #endregion
}
