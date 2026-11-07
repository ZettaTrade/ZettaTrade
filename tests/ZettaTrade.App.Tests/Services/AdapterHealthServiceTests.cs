namespace ZettaTrade.App.Tests.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using Avalonia.Threading;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using ZettaTrade.App.Services;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Infrastructure.Exchange;

/// <summary>
/// The adapter health fold's contract (T-098): supervisor transport reports
/// map into the strip's vocabulary (connected / reconnecting / down), the
/// unbound membership member renders unsupervised, the fold carries the
/// supervisor's counters and event moments, and a late subscriber
/// bootstraps with the current snapshot. The composition shape is pinned
/// too: the fold carries no polling machinery at all.
/// </summary>
/// <remarks>
/// Every stream wait subscribes BEFORE the caller triggers any publish
/// (the standing Rx-test hazard rule of the suite) and every wait is
/// bounded; nothing blocks.
/// </remarks>
public sealed class AdapterHealthServiceTests
{
    #region Constants

    /// <summary>Bounded wait for any expected observable emission.</summary>
    private static readonly TimeSpan EventWait = TimeSpan.FromSeconds(5);

    #endregion

    #region Test Data

    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly IMarketDataAdapter _first = Substitute.For<IMarketDataAdapter>();
    private readonly IMarketDataAdapter _second = Substitute.For<IMarketDataAdapter>();

    public AdapterHealthServiceTests()
    {
        _first.Id.Returns("one");
        _first.Name.Returns("One");
        _second.Id.Returns("two");
        _second.Name.Returns("Two");
    }

    /// <summary>A report stamp the facts reuse (the moment is carried verbatim into the fold).</summary>
    private static readonly DateTimeOffset ExampleMoment = new(2026, 11, 7, 12, 30, 40, TimeSpan.Zero);

    #endregion

    #region Tests: state mapping

    /// <summary>
    /// Every supervised transport phase maps into the strip vocabulary —
    /// the open session connected, every in-cycle phase (connect loop,
    /// backoff wait, watchdog force-cycle) reconnecting and the
    /// disconnected phase down — while the membership member that never
    /// bound the supervisor renders unsupervised end to end.
    /// </summary>
    [Fact]
    public async Task TransportReports_MapIntoTheStripVocabulary_AndTheUnboundMemberRendersUnsupervised()
    {
        await using var supervisor = CreateSupervisor();
        var sink = supervisor.For("one", ReconnectPolicy.Default);
        var service = CreateService(supervisor);

        // One seed emission (the subscription-time snapshot: the binding
        // pre-registered "one" as Disconnected) plus one per published
        // report, in publish order.
        var emissionsTask = service.HealthStream.Take(6).ToList().ToTask(TaskCreationOptions.RunContinuationsAsynchronously);

        sink.Publish(new ReconnectReport(ReconnectionPhase.Connected, ExampleMoment, 0, null));
        sink.Publish(new ReconnectReport(ReconnectionPhase.Connecting, ExampleMoment, 0, null));
        sink.Publish(new ReconnectReport(ReconnectionPhase.Reconnecting, ExampleMoment, 3, TimeSpan.FromMilliseconds(250)));
        sink.Publish(new ReconnectReport(ReconnectionPhase.ForceCycling, ExampleMoment, 0, null));
        sink.Publish(new ReconnectReport(ReconnectionPhase.Disconnected, ExampleMoment, 1, null));

        var emissions = await emissionsTask.WaitAsync(EventWait);

        emissions.Should().HaveCount(6, "the seed plus one emission per published report");

        var firstStates = emissions.Select(list => list.Single(health => health.AdapterId == "one")).ToList();
        firstStates[0].State.Should().Be(
            AdapterHealthState.Down,
            "the binding's pre-report Disconnected state is the seed");
        firstStates.Skip(1).Select(state => state.State)
            .Should()
            .Equal(
                AdapterHealthState.Connected,
                AdapterHealthState.Reconnecting,
                AdapterHealthState.Reconnecting,
                AdapterHealthState.Reconnecting,
                AdapterHealthState.Down);

        var unsupervised = emissions[0].Single(health => health.AdapterId == "two");
        unsupervised.State.Should().Be(AdapterHealthState.Unsupervised);
        unsupervised.LastEventUtc.Should().BeNull("an unsupervised adapter has no transport event to timestamp");
        unsupervised.ConsecutiveFailures.Should().Be(0);

        emissions.Should().OnlyContain(list => list.Single(health => health.AdapterId == "two").State == AdapterHealthState.Unsupervised);
    }

    /// <summary>
    /// The fold carries the supervisor's own counters and moment: the row's
    /// consecutive-failure counter and last-event timestamp are the report's
    /// values, never recomputed in the fold.
    /// </summary>
    /// <remarks>
    /// Two emissions flow: the subscription-time snapshot seed (the
    /// binding's pre-report Disconnected state) and the published report —
    /// the assertions read the second.
    /// </remarks>
    [Fact]
    public async Task TheFold_CarriesTheCountersAndTheLastEventMoment()
    {
        await using var supervisor = CreateSupervisor();
        var sink = supervisor.For("one", ReconnectPolicy.Default);
        var service = CreateService(supervisor);

        var rowTask = service
            .HealthStream
            .Select(list => list.Single(health => health.AdapterId == "one"))
            .Take(2)
            .ToTask(TaskCreationOptions.RunContinuationsAsynchronously);

        sink.Publish(new ReconnectReport(
            ReconnectionPhase.Reconnecting,
            ExampleMoment,
            ConsecutiveFailures: 4,
            BackoffDelay: TimeSpan.FromSeconds(30)));

        var row = await rowTask.WaitAsync(EventWait);

        row.AdapterName.Should().Be("One", "the fold binds the membership's display name");
        row.State.Should().Be(AdapterHealthState.Reconnecting);
        row.ConsecutiveFailures.Should().Be(4);
        row.LastEventUtc.Should().Be(ExampleMoment);
    }

    /// <summary>
    /// A late subscriber bootstraps with the supervisor's CURRENT snapshot:
    /// the states applied before its subscription are the seed, not waited
    /// for until the next transport report.
    /// </summary>
    /// <remarks>
    /// The first subscription reads TWO emissions (the seed plus the
    /// published report) so the awaited report is the second; the second
    /// subscription — bound after everything applied — seeds with the
    /// already-applied state directly.
    /// </remarks>
    [Fact]
    public async Task LateSubscription_BootstrapsWithTheCurrentSupervisorSnapshot()
    {
        await using var supervisor = CreateSupervisor();
        var sink = supervisor.For("one", ReconnectPolicy.Default);
        var service = CreateService(supervisor);

        var firstTask = service
            .HealthStream
            .Select(list => list.Single(health => health.AdapterId == "one"))
            .Take(2)
            .ToTask(TaskCreationOptions.RunContinuationsAsynchronously);
        sink.Publish(new ReconnectReport(ReconnectionPhase.Connected, ExampleMoment, 0, null));
        var firstRow = await firstTask.WaitAsync(EventWait);
        firstRow.State.Should().Be(
            AdapterHealthState.Connected,
            "the report emission is the fold's last before completion");

        var secondTask = service
            .HealthStream
            .Select(list => list.Single(health => health.AdapterId == "one"))
            .Take(1)
            .ToTask(TaskCreationOptions.RunContinuationsAsynchronously);

        var row = await secondTask.WaitAsync(EventWait);
        row.State.Should().Be(
            AdapterHealthState.Connected,
            "the Defer seed replays the supervisor's current snapshot, no new report needed");
    }

    /// <summary>
    /// A phase outside the strip's vocabulary errors the fold — the
    /// standing rule: an unparsable state is an error, never a substituted
    /// default.
    /// </summary>
    [Fact]
    public async Task AnOutOfVocabularyPhase_ErrorsTheFold()
    {
        await using var supervisor = CreateSupervisor();
        var sink = supervisor.For("one", ReconnectPolicy.Default);
        var service = CreateService(supervisor);

        var errorTask = service
            .HealthStream
            .Materialize()
            .Where(notification => notification.Kind == System.Reactive.NotificationKind.OnError)
            .Select(notification => notification.Exception!)
            .FirstAsync()
            .ToTask(TaskCreationOptions.RunContinuationsAsynchronously);

        sink.Publish(new ReconnectReport((ReconnectionPhase)99, ExampleMoment, 0, null));

        var error = await errorTask.WaitAsync(EventWait);
        error.Should().BeOfType<ArgumentOutOfRangeException>("the fold returns the mapping error, never a default state");
    }

    #endregion

    #region Tests: shape

    /// <summary>
    /// The fold carries no polling machinery at all (the acceptance: the
    /// strip adds no polling loops). The shape pin: the service declares
    /// exactly its composition fields — no timer, thread, task or interval
    /// fiber ever exists for the strip.
    /// </summary>
    [Fact]
    public void TheFold_CarriesNoPollingMachinery()
    {
        var fields = typeof(AdapterHealthService).GetFields(
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

        fields
            .Select(field => field.Name)
            .Should()
            .BeEquivalentTo(
            [
                "<HealthStream>k__BackingField",
                "_exchanges",
                "_supervisor",
            ],
            "the fold is a pure observable composition — no accumulating state beside its inputs");

        var pollingTypes = new[]
        {
            typeof(System.Threading.Timer),
            typeof(System.Timers.Timer),
            typeof(System.Threading.PeriodicTimer),
            typeof(DispatcherTimer),
            typeof(System.Threading.Thread),
            typeof(System.Threading.Tasks.Task),
        };

        foreach (var field in fields)
        {
            pollingTypes.Should().NotContain(
                field.FieldType,
                $"the strip adds no polling loop — field '{field.Name}' must not be one");
        }
    }

    #endregion

    #region Helpers

    private static ReconnectSupervisor CreateSupervisor() =>
        new(NullLogger<ReconnectSupervisor>.Instance);

    private AdapterHealthService CreateService(ReconnectSupervisor supervisor) =>
        new(
            new ActiveExchangeService(
                [_first, _second],
                _settings,
                NullLogger<ActiveExchangeService>.Instance),
            supervisor);

    #endregion
}
