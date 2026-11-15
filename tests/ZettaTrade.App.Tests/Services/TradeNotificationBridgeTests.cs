namespace ZettaTrade.App.Tests.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;
using ZettaTrade.App.Services;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Infrastructure.Notifications;

using static ZettaTrade.App.Tests.Services.TelegramSinkFixtures;

/// <summary>
/// The trade-notification bridge's dispatch facts (T-260), exercised over
/// the loopback Bot API stub and the recording sink stubs: only the three
/// journaled trade outcomes ever dispatch (the adapter/alert/summary kinds
/// are the toast surfaces' business), the mapped event carries the row's
/// own facts and the lane standing at event time, the fan-out never blocks
/// on the in-flight dispatch (the tracked fire-and-forget handle) and
/// every registered sink rides the same event, a contract-breaking sink
/// degrades into the bridge's own lane with the others still delivered,
/// and the real Telegram sink's own opt-in flag gates its wire.
/// </summary>
public sealed class TradeNotificationBridgeTests
{
    #region Constants

    private static readonly DateTimeOffset Moment = new(2026, 11, 15, 9, 0, 5, TimeSpan.Zero);

    #endregion Constants

    #region Tests: the event filter

    /// <summary>
    /// The non-trade kinds never reach the sinks, and the stream stays
    /// alive: the dropped kinds dispatch nothing, the next trade outcome
    /// after them still dispatches.
    /// </summary>
    [Fact]
    public async Task TheNonTradeKinds_NeverReachTheSinks_AndTheStreamStaysAlive()
    {
        await using var rig = await BridgeRig.CreateAsync();
        var trap = rig.Api.ServeAsync(200, """{"ok":true}""");

        rig.Events.OnNext(Notification.ForAdapterState("Paper", AdapterHealthState.Connected, Moment));
        rig.Events.OnNext(Notification.ForDoNotDisturbSummary(3, Moment));
        rig.Events.OnNext(Notification.ForUpdateAvailable("0.2.1", "https://example.invalid/release", Moment));
        await Task.Delay(200);

        trap.IsCompleted.Should().BeFalse("the adapter, summary and update kinds are the toast surfaces' business, not the sinks'");
        rig.Recorder.Events.Should().BeEmpty("the dropped kinds never mapped into the sink vocabulary");

        rig.Events.OnNext(TradeNotification(NotificationKind.OrderFill, quantity: 0.01m, exchange: "paper"));
        await rig.AwaitLastDispatchAsync();
        var captured = await trap;

        captured.Body.Should().Contain("filled", "the stream stayed alive past the dropped kinds");
        rig.Recorder.Events.Should().ContainSingle("the fill mapped and fanned out");
    }

    #endregion Tests: the event filter

    #region Tests: the mapping

    /// <summary>The mapped event carries the row's own facts, translated once into the sink vocabulary.</summary>
    [Fact]
    public async Task TheMappedEvent_CarriesTheRowFacts()
    {
        await using var rig = await BridgeRig.CreateAsync();

        rig.Events.OnNext(TradeNotification(
            NotificationKind.OrderFill,
            quantity: 0.02m,
            exchange: "bybit",
            price: 50_000m));
        await rig.AwaitLastDispatchAsync();

        var mapped = rig.Recorder.Events.Should().ContainSingle().Subject;
        mapped.Kind.Should().Be(TradeNotificationKind.Fill);
        mapped.Symbol.Should().Be("BTC/USDT");
        mapped.Side.Should().Be(OrderSide.Buy);
        mapped.Quantity.Should().Be(0.02m);
        mapped.Price.Should().Be(50_000m);
        mapped.Exchange.Should().Be("bybit");
        mapped.Reason.Should().BeEmpty("a fill carries no rejection reason");
    }

    /// <summary>Every trade kind maps into its own vocabulary member: cancel and reject with their row facts.</summary>
    [Fact]
    public async Task EveryTradeKind_MapsIntoItsOwnVocabularyMember()
    {
        await using var rig = await BridgeRig.CreateAsync();

        rig.Events.OnNext(TradeNotification(NotificationKind.OrderCancel, quantity: 0m, exchange: "paper"));
        await rig.AwaitLastDispatchAsync();
        rig.Recorder.Events.Last().Kind.Should().Be(TradeNotificationKind.Cancel);

        rig.Events.OnNext(TradeNotification(
            NotificationKind.OrderReject,
            quantity: 0.02m,
            exchange: "bybit",
            price: null,
            reason: "the venue refused the price"));
        await rig.AwaitLastDispatchAsync();

        var reject = rig.Recorder.Events.Last();
        reject.Kind.Should().Be(TradeNotificationKind.Reject);
        reject.Reason.Should().Be("the venue refused the price", "the adapter's own refusal reason rides the map");
        reject.Price.Should().BeNull("the reject published no price fact — the map carries the raw facts verbatim");
    }

    /// <summary>The mapped lane reads the lane standing when the event arrived, seeded at initialization.</summary>
    [Fact]
    public async Task TheMappedLane_ReadsTheLaneStandingAtEventTime()
    {
        await using var rig = await BridgeRig.CreateAsync();

        rig.Bridge.LaneForTests.Should().Be(TradingLane.Paper, "the lane stream's standing emission seeds the tag synchronously");

        rig.Lanes.OnNext(TradingLane.Testnet);
        rig.Bridge.LaneForTests.Should().Be(TradingLane.Testnet, "the lane stream's emission is the standing tag");

        rig.Events.OnNext(TradeNotification(NotificationKind.OrderFill, quantity: 0.01m, exchange: "paper"));
        await rig.AwaitLastDispatchAsync();

        rig.Recorder.Events.Last().Lane.Should().Be(
            TradeNotificationLane.Testnet,
            "the tag names the lane standing when the fill arrived (the sink-domain parallel)");
    }

    #endregion Tests: the mapping

    #region Tests: the fan-out

    /// <summary>Every registered sink rides the same mapped event: the fan-out delivers to the whole fleet.</summary>
    [Fact]
    public async Task TheFanOut_DeliversToEveryRegisteredSink()
    {
        await using var rig = await BridgeRig.CreateAsync(sinks: [new RecordingSink(), new RecordingSink()]);

        rig.Events.OnNext(TradeNotification(NotificationKind.OrderFill, quantity: 0.01m, exchange: "paper"));
        await rig.AwaitLastDispatchAsync();

        rig.Recorders.SelectMany(recorder => recorder.Events).Should().HaveCount(
            2,
            "both registered sinks received the same mapped event");
    }

    /// <summary>
    /// The dispatch never blocks on the sinks: an event dispatches while
    /// the previous fan-out is still in flight (a hanging sink holds only
    /// its own task), and the tracked handle stays unfinished exactly that
    /// long.
    /// </summary>
    [Fact]
    public async Task TheDispatch_NeverBlocksOnTheSinks_AndTheHandleIsTracked()
    {
        await using var rig = await BridgeRig.CreateAsync(sinks: [new HangingSink()]);
        var hangTask = rig.HangingSink!.FirstEventTask;

        rig.Events.OnNext(TradeNotification(NotificationKind.OrderFill, quantity: 0.01m, exchange: "paper"));
        await Task.Delay(200);

        hangTask.IsCompleted.Should().BeTrue("the event reached the sink (the hanging sink holds only the completion)");
        rig.Bridge.LastDispatchForTests.IsCompleted.Should().BeFalse(
            "the dispatch returned long before the sink settled — nobody blocks on it");

        rig.Events.OnNext(TradeNotification(NotificationKind.OrderCancel, quantity: 0m, exchange: "paper"));
        await Task.Delay(200);

        rig.Bridge.LastDispatchForTests.IsCompleted.Should().BeFalse(
            "the stream stayed alive and dispatched while the first fan-out was still in flight");
    }

    /// <summary>
    /// A sink that breaks the never-faults contract degrades into the
    /// bridge's own lane and the fleet stays intact: the contract-keeping
    /// sink still delivered its event and the stream never dies.
    /// </summary>
    [Fact]
    public async Task AContractBreakingSink_DegradesIntoTheBridgesLane_AndTheOthersStillDeliver()
    {
        await using var rig = await BridgeRig.CreateAsync(sinks: [new ThrowingSink(), new RecordingSink()]);
        var trap = rig.Api.ServeAsync(200, """{"ok":true}""");

        rig.Events.OnNext(TradeNotification(NotificationKind.OrderFill, quantity: 0.01m, exchange: "paper"));
        await rig.AwaitLastDispatchAsync();

        rig.BridgeLogger.Entries.Should().Contain(
            entry => entry.Level == LogLevel.Warning && entry.Exception != null,
            "a contract-breaking sink lands in the bridge's own degrade lane");
        rig.Recorder.Events.Should().ContainSingle(
            "the contract-keeping sink received the event beside the breaking one");
        rig.SinkLogger.Entries.Should().NotContain(
            entry => entry.Level >= LogLevel.Warning,
            "the real Telegram sink (also in the fleet) delivered its own push");

        var captured = await trap;
        captured.Body.Should().Contain("filled", "the real sink's push went out beside the degraded one");
    }

    #endregion Tests: the fan-out

    #region Tests: the real sink's own routing

    /// <summary>
    /// The bridge never gates — the mapped event reaches every sink and
    /// each sink routes on its own flags: with the Telegram cancels flag
    /// off, a cancellation reaches the fleet but the wire stays quiet,
    /// while a rejection (its flag on) lands its push.
    /// </summary>
    [Fact]
    public async Task TheSinksRouteOnTheirOwnFlags_TheBridgeOnlyMaps()
    {
        await using var rig = await BridgeRig.CreateAsync(fills: true, cancels: false, rejects: true);
        var trap = rig.Api.ServeAsync(200, """{"ok":true}""");

        rig.Events.OnNext(TradeNotification(NotificationKind.OrderCancel, quantity: 0m, exchange: "paper"));
        await rig.AwaitLastDispatchAsync();

        trap.IsCompleted.Should().BeFalse("the cancellation's own opt-in flag is off — the wire stays quiet");
        rig.Recorder.Events.Should().ContainSingle(
            mapped => mapped.Kind == TradeNotificationKind.Cancel,
            "the mapped event still reached the fleet — the routing is the sink's own business");

        rig.Events.OnNext(TradeNotification(
            NotificationKind.OrderReject,
            quantity: 0.02m,
            exchange: "paper",
            reason: "the venue refused the price"));
        await rig.AwaitLastDispatchAsync();
        var captured = await trap;

        using var body = JsonDocument.Parse(captured.Body);
        body.RootElement.GetProperty("text").GetString().Should().Be(
            "Order rejected: BTC/USDT — the venue refused the price (paper, Paper)",
            "the rejection's own opt-in flag is on — its push lands");
    }

    #endregion Tests: the real sink's own routing

    #region Helpers

    /// <summary>One journaled trade outcome on the raw lane (the bridge's own event surface).</summary>
    private static Notification TradeNotification(
        NotificationKind kind,
        decimal quantity,
        string exchange,
        decimal? price = 50_000m,
        string reason = "") =>
        Notification.ForJournalOutcome(kind, Moment, "BTC/USDT", OrderSide.Buy, quantity, reason, price, exchange);

    /// <summary>
    /// One wired bridge rig: the loopback stub, the named-factory transport,
    /// the opted-in settings, the stubbed secret store, the recording sink
    /// (beside whatever sinks the test passes) and the real Telegram sink +
    /// the two recording loggers. The bridge subscribes at creation; the
    /// test drives the Subjects.
    /// </summary>
    private sealed class BridgeRig : IAsyncDisposable
    {
        private BridgeRig(
            LoopbackTelegramApi api,
            ServiceProvider provider,
            Subject<Notification> events,
            BehaviorSubject<TradingLane> lanes,
            TradeNotificationBridge bridge,
            RecordingSink recorder,
            IReadOnlyList<RecordingSink> recorders,
            TelegramTestSettings settings,
            SpyLogger<TradeNotificationBridge> bridgeLogger,
            SpyLogger<TelegramNotifierService> sinkLogger,
            HangingSink? hangingSink)
        {
            Api = api;
            Provider = provider;
            Events = events;
            Lanes = lanes;
            Bridge = bridge;
            Recorder = recorder;
            Recorders = recorders;
            Settings = settings;
            BridgeLogger = bridgeLogger;
            SinkLogger = sinkLogger;
            HangingSink = hangingSink;
        }

        internal LoopbackTelegramApi Api { get; }

        internal ServiceProvider Provider { get; }

        internal Subject<Notification> Events { get; }

        internal BehaviorSubject<TradingLane> Lanes { get; }

        internal TradeNotificationBridge Bridge { get; }

        /// <summary>The first recording sink of the fleet (the mapping facts read it).</summary>
        internal RecordingSink Recorder { get; }

        /// <summary>Every recording sink the rig built (the fan-out facts read them all).</summary>
        internal IReadOnlyList<RecordingSink> Recorders { get; }

        internal TelegramTestSettings Settings { get; }

        internal SpyLogger<TradeNotificationBridge> BridgeLogger { get; }

        internal SpyLogger<TelegramNotifierService> SinkLogger { get; }

        /// <summary>The hanging sink when the rig built one (the never-blocking facts read its task).</summary>
        internal HangingSink? HangingSink { get; }

        internal async Task AwaitLastDispatchAsync()
        {
            // The dispatch is fire-and-forget by contract; a test awaits the
            // tracked handle plus a settle so the loopback's own queueing
            // lands too. A hanging sink's handle never settles — those facts
            // assert on the handle's incompleteness instead of awaiting it.
            await Bridge.LastDispatchForTests;
            await Task.Delay(100);
        }

        internal static async Task<BridgeRig> CreateAsync(
            bool fills = true,
            bool cancels = true,
            bool rejects = true,
            IReadOnlyList<INotificationSink>? sinks = null)
        {
            var api = LoopbackTelegramApi.Start();
            var settings = new TelegramTestSettings();
            await settings.SetAsync(TelegramNotifierService.SettingsSection, TelegramNotifierService.FillsEnabledKey, fills);
            await settings.SetAsync(TelegramNotifierService.SettingsSection, TelegramNotifierService.CancelsEnabledKey, cancels);
            await settings.SetAsync(TelegramNotifierService.SettingsSection, TelegramNotifierService.RejectsEnabledKey, rejects);
            await settings.SetAsync(TelegramNotifierService.SettingsSection, TelegramNotifierService.ChatIdKey, ChatId);

            var secrets = Substitute.For<ISecretStore>();
            secrets.GetAsync(TelegramNotifierService.BotTokenSecretKey, Arg.Any<CancellationToken>())
                .Returns(LoopbackTelegramApi.FakeToken);

            var services = new ServiceCollection();
            services.AddHttpClient(TelegramBotClient.HttpClientName, client =>
            {
                client.BaseAddress = api.BaseUrl;
                client.Timeout = TimeSpan.FromSeconds(5);
            });
            var provider = services.BuildServiceProvider();
            var transport = new TelegramBotClient(
                provider.GetRequiredService<IHttpClientFactory>());

            var sinkLogger = new SpyLogger<TelegramNotifierService>();
            var notifier = new TelegramNotifierService(
                settings,
                secrets,
                transport,
                new ResxCatalogLocalization("en"),
                sinkLogger);

            // The fleet: the test's own sinks first, the real Telegram sink
            // last; a rig without its own sinks rides the default recorder +
            // notifier pair.
            var fleet = new List<INotificationSink>();
            if (sinks is not null)
            {
                fleet.AddRange(sinks);
            }

            var recorders = fleet.OfType<RecordingSink>().ToList();
            var recorder = recorders.FirstOrDefault() ?? new RecordingSink();
            if (recorders.Count == 0)
            {
                fleet.Add(recorder);
            }

            var hangingSink = fleet.OfType<HangingSink>().FirstOrDefault();
            fleet.Add(notifier);

            var bridgeLogger = new SpyLogger<TradeNotificationBridge>();
            var events = new Subject<Notification>();
            var lanes = new BehaviorSubject<TradingLane>(TradingLane.Paper);
            var bridge = new TradeNotificationBridge(
                events,
                lanes,
                fleet,
                bridgeLogger);
            await bridge.InitializeAsync();

            return new BridgeRig(api, provider, events, lanes, bridge, recorder, recorders, settings, bridgeLogger, sinkLogger, hangingSink);
        }

        public async ValueTask DisposeAsync()
        {
            Bridge.Dispose();
            Provider.Dispose();
            Api.Dispose();
            await Task.CompletedTask;
        }
    }

    /// <summary>
    /// A recording sink: it never faults, it records every event the bridge
    /// hands it, and the first arrival completes a task a fact can await.
    /// </summary>
    internal sealed class RecordingSink : INotificationSink
    {
        internal List<TradeNotificationEvent> Events { get; } = [];

        internal Task FirstEventTask => FirstEvent.Task;

        private TaskCompletionSource FirstEvent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task HandleAsync(TradeNotificationEvent tradeEvent, CancellationToken cancellationToken = default)
        {
            Events.Add(tradeEvent);
            FirstEvent.TrySetResult();
            return Task.CompletedTask;
        }
    }

    /// <summary>A hanging sink: it never faults and never completes (the dispatch's fire-and-forget shape reads it).</summary>
    internal sealed class HangingSink : INotificationSink
    {
        internal TaskCompletionSource FirstEvent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task FirstEventTask => FirstEvent.Task;

        public Task HandleAsync(TradeNotificationEvent tradeEvent, CancellationToken cancellationToken = default)
        {
            FirstEvent.TrySetResult();
            return new TaskCompletionSource().Task;
        }
    }

    /// <summary>A contract-breaking sink: it throws synchronously (the bridge's degrade lane reads it).</summary>
    internal sealed class ThrowingSink : INotificationSink
    {
        public Task HandleAsync(TradeNotificationEvent tradeEvent, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("this sink breaks the never-faults contract");
    }

    #endregion Helpers
}
