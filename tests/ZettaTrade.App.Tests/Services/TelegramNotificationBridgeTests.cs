namespace ZettaTrade.App.Tests.Services;

using System;
using System.Reactive.Subjects;
using System.Text.Json;
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
/// The Telegram bridge's dispatch facts (T-041), exercised against the
/// loopback Bot API stub: only the three journaled trade outcomes ever
/// dispatch (the adapter/alert/summary kinds are the toast surfaces'
/// business), the message's lane tag reads the lane standing at event
/// time, the dispatch never blocks on the in-flight send (the tracked
/// fire-and-forget handle), one event's compose fault degrades into the
/// bridge's own lane with the stream left alive, and every trade kind
/// rides its own opt-in flag.
/// </summary>
public sealed class TelegramNotificationBridgeTests
{
    #region Constants

    private static readonly DateTimeOffset Moment = new(2026, 11, 15, 9, 0, 5, TimeSpan.Zero);

    #endregion Constants

    #region Tests: the event filter

    /// <summary>
    /// The non-trade kinds never reach the sink, and the stream stays
    /// alive: the dropped kinds dispatch nothing, the next trade outcome
    /// after them still dispatches.
    /// </summary>
    [Fact]
    public async Task TheNonTradeKinds_NeverReachTheSink_AndTheStreamStaysAlive()
    {
        await using var rig = await BridgeRig.CreateAsync();
        var trap = rig.Api.ServeAsync(200, """{"ok":true}""");

        rig.Events.OnNext(Notification.ForAdapterState("Paper", AdapterHealthState.Connected, Moment));
        rig.Events.OnNext(Notification.ForDoNotDisturbSummary(3, Moment));
        rig.Events.OnNext(Notification.ForUpdateAvailable("0.2.1", "https://example.invalid/release", Moment));
        await Task.Delay(200);

        trap.IsCompleted.Should().BeFalse("the adapter, summary and update kinds are the toast surfaces' business, not the sink's");

        rig.Events.OnNext(TradeEvent(NotificationKind.OrderFill, quantity: 0.01m, exchange: "paper"));
        var captured = await trap;

        captured.Body.Should().Contain("filled", "the stream stayed alive past the dropped kinds");
    }

    #endregion Tests: the event filter

    #region Tests: the lane tag

    /// <summary>The message's lane tag is the lane standing when the event arrived, seeded at initialization.</summary>
    [Fact]
    public async Task TheLaneTag_ReadsTheLaneStandingAtEventTime()
    {
        await using var rig = await BridgeRig.CreateAsync();

        rig.Bridge.LaneForTests.Should().Be(TradingLane.Paper, "the lane stream's standing emission seeds the tag synchronously");

        rig.Lanes.OnNext(TradingLane.Testnet);
        rig.Bridge.LaneForTests.Should().Be(TradingLane.Testnet, "the lane stream's emission is the standing tag");

        var serve = rig.Api.ServeAsync(200, """{"ok":true}""");
        rig.Events.OnNext(TradeEvent(NotificationKind.OrderFill, quantity: 0.01m, exchange: "paper"));
        var captured = await serve;

        using var body = JsonDocument.Parse(captured.Body);
        body.RootElement.GetProperty("text").GetString().Should().EndWith(
            "(Testnet)",
            "the tag names the lane standing when the fill arrived");
    }

    #endregion Tests: the lane tag

    #region Tests: the fire-and-forget dispatch

    /// <summary>
    /// The dispatch never blocks on the send: an event dispatches while the
    /// previous send is still in flight (the hanging API holds only the
    /// answer), and the tracked handle stays unfinished exactly that long.
    /// </summary>
    [Fact]
    public async Task TheDispatch_NeverBlocksOnTheSend_AndTheHandleIsTracked()
    {
        await using var rig = await BridgeRig.CreateAsync();
        var first = rig.Api.ServeHangingAsync();

        rig.Events.OnNext(TradeEvent(NotificationKind.OrderFill, quantity: 0.01m, exchange: "paper"));
        await Task.Delay(200);

        first.IsCompleted.Should().BeTrue("the request left the client (the hanging API holds only the answer)");
        rig.Bridge.LastSendForTests.IsCompleted.Should().BeFalse(
            "the dispatch returned long before the send settled — nobody blocks on it");

        var second = rig.Api.ServeHangingAsync();
        rig.Events.OnNext(TradeEvent(NotificationKind.OrderFill, quantity: 0.02m, exchange: "paper"));
        await Task.Delay(200);

        second.IsCompleted.Should().BeTrue(
            "the stream stayed alive and dispatched while the first send was still in flight");
        rig.Bridge.LastSendForTests.IsCompleted.Should().BeFalse(
            "the newest send's handle tracks the still-running push");
    }

    #endregion Tests: the fire-and-forget dispatch

    #region Tests: the compose fault's degrade lane

    /// <summary>
    /// One event's compose fault degrades into the bridge's own lane
    /// (nothing dispatches for it) and the event stream never dies: the
    /// next well-formed event still dispatches.
    /// </summary>
    [Fact]
    public async Task AComposeFault_DegradesAndNeverKillsTheStream()
    {
        await using var rig = await BridgeRig.CreateAsync();
        var trap = rig.Api.ServeAsync(200, """{"ok":true}""");

        rig.Events.OnNext(TradeEvent(NotificationKind.OrderFill, quantity: 0.5m, exchange: "bybit", price: null));
        await Task.Delay(200);

        trap.IsCompleted.Should().BeFalse("the faulted dispatch sent nothing");
        rig.BridgeLogger.Entries.Should().Contain(
            entry => entry.Level == LogLevel.Error && entry.Exception != null,
            "the compose fault rides the bridge's own degrade lane");

        rig.Events.OnNext(TradeEvent(NotificationKind.OrderCancel, quantity: 0m, exchange: "paper"));
        var captured = await trap;

        captured.Body.Should().Contain("cancelled", "the stream stayed alive past the faulted event");
    }

    #endregion Tests: the compose fault's degrade lane

    #region Tests: the per-kind opt-in

    /// <summary>
    /// Every trade kind rides its own opt-in flag: a cancellation under a
    /// rig whose cancels flag is off dispatches nothing while the fills and
    /// the rejections (their flags on) dispatch — the kind mapping reaches
    /// exactly the sink's own flag vocabulary.
    /// </summary>
    [Fact]
    public async Task EachTradeKind_RidesItsOwnOptInFlag()
    {
        await using var rig = await BridgeRig.CreateAsync(fills: true, cancels: false, rejects: true);
        var trap = rig.Api.ServeAsync(200, """{"ok":true}""");

        rig.Events.OnNext(TradeEvent(NotificationKind.OrderCancel, quantity: 0m, exchange: "paper"));
        await Task.Delay(200);

        trap.IsCompleted.Should().BeFalse("the cancellation's kind maps to its own opt-in flag, and that flag is off");

        rig.Events.OnNext(TradeEvent(
            NotificationKind.OrderReject,
            quantity: 0.02m,
            exchange: "paper",
            reason: "the venue refused the price"));
        var captured = await trap;

        using var body = JsonDocument.Parse(captured.Body);
        body.RootElement.GetProperty("text").GetString().Should().Be(
            "Order rejected: BTC/USDT — the venue refused the price (paper, Paper)",
            "the rejection's kind maps to its own opt-in flag, and that flag is on");
    }

    #endregion Tests: the per-kind opt-in

    #region Helpers

    /// <summary>One journaled trade outcome on the raw lane (the bridge's own event surface).</summary>
    private static Notification TradeEvent(
        NotificationKind kind,
        decimal quantity,
        string exchange,
        decimal? price = 50_000m,
        string reason = "") =>
        Notification.ForJournalOutcome(kind, Moment, "BTC/USDT", OrderSide.Buy, quantity, reason, price, exchange);

    /// <summary>
    /// One wired bridge rig: the loopback stub, the named-factory transport,
    /// the opted-in settings, the stubbed secret store, the real catalog
    /// (English) and the two recording loggers. The bridge subscribes at
    /// creation; the test drives the Subjects.
    /// </summary>
    private sealed class BridgeRig : IAsyncDisposable
    {
        private BridgeRig(
            LoopbackTelegramApi api,
            ServiceProvider provider,
            Subject<Notification> events,
            BehaviorSubject<TradingLane> lanes,
            TelegramNotificationBridge bridge,
            TelegramTestSettings settings,
            SpyLogger<TelegramNotificationBridge> bridgeLogger,
            SpyLogger<TelegramNotifierService> sinkLogger)
        {
            Api = api;
            Provider = provider;
            Events = events;
            Lanes = lanes;
            Bridge = bridge;
            Settings = settings;
            BridgeLogger = bridgeLogger;
            SinkLogger = sinkLogger;
        }

        internal LoopbackTelegramApi Api { get; }

        internal ServiceProvider Provider { get; }

        internal Subject<Notification> Events { get; }

        internal BehaviorSubject<TradingLane> Lanes { get; }

        internal TelegramNotificationBridge Bridge { get; }

        internal TelegramTestSettings Settings { get; }

        internal SpyLogger<TelegramNotificationBridge> BridgeLogger { get; }

        internal SpyLogger<TelegramNotifierService> SinkLogger { get; }

        internal static async Task<BridgeRig> CreateAsync(
            bool fills = true,
            bool cancels = true,
            bool rejects = true)
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
                sinkLogger);
            var bridgeLogger = new SpyLogger<TelegramNotificationBridge>();
            var events = new Subject<Notification>();
            var lanes = new BehaviorSubject<TradingLane>(TradingLane.Paper);
            var bridge = new TelegramNotificationBridge(
                events,
                lanes,
                notifier,
                new ResxCatalogLocalization("en"),
                bridgeLogger);
            await bridge.InitializeAsync();

            return new BridgeRig(api, provider, events, lanes, bridge, settings, bridgeLogger, sinkLogger);
        }

        public async ValueTask DisposeAsync()
        {
            Bridge.Dispose();
            Provider.Dispose();
            Api.Dispose();
            await Task.CompletedTask;
        }
    }

    #endregion Helpers
}
