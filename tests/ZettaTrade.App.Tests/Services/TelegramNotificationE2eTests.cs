namespace ZettaTrade.App.Tests.Services;

using System;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using ZettaTrade.App.Services;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Infrastructure.Notifications;

using static ZettaTrade.App.Tests.Services.TelegramSinkFixtures;

/// <summary>
/// The Telegram trade-notification's e2e walk (T-041): a real notification
/// center publishes a journaled trade outcome, the real bridge filters and
/// composes it, and the real sink pushes it through the production-shaped
/// named HttpClient — all against the loopback Bot API stub, so the wire
/// facts (the token-bearing path, the chat id, the composed text) are the
/// pinned goldens. The degrade lane stays best-effort end to end: a failed
/// push is the sink's one logged warning, the bridge's lane stays clean and
/// the center keeps serving.
/// </summary>
public sealed class TelegramNotificationE2eTests
{
    #region Tests: the journaled pushes

    /// <summary>A filled journal row lands its push: the token rides the path, the body carries the chat id and the composed fill line.</summary>
    [Fact]
    public async Task AJournalFill_LandsItsTelegramPush()
    {
        await using var rig = await E2eRig.CreateAsync();
        var captured = await rig.PublishAndAwaitPushAsync(BuildOrder(OrderStatus.Filled, filled: 0.02m));

        captured.Method.Should().Be("POST");
        captured.Path.Should().Be($"/bot{LoopbackTelegramApi.FakeToken}/sendMessage");
        using var body = JsonDocument.Parse(captured.Body);
        body.RootElement.GetProperty("chat_id").GetString().Should().Be(ChatId);
        body.RootElement.GetProperty("text").GetString().Should().Be(
            "Buy 0.02000000 BTC/USDT filled at 50000.00000000 — paper (Paper)");
    }

    /// <summary>A cancelled journal row lands its push with the composed cancellation line.</summary>
    [Fact]
    public async Task ACancelledRow_LandsItsPush()
    {
        await using var rig = await E2eRig.CreateAsync();
        var captured = await rig.PublishAndAwaitPushAsync(BuildOrder(OrderStatus.Cancelled, filled: 0m));

        using var body = JsonDocument.Parse(captured.Body);
        body.RootElement.GetProperty("text").GetString().Should().Be(
            "BTC/USDT order cancelled — paper (Paper)");
    }

    /// <summary>A rejected journal row lands its push carrying the adapter's own reason.</summary>
    [Fact]
    public async Task ARejectedRow_LandsItsPush()
    {
        await using var rig = await E2eRig.CreateAsync();
        var captured = await rig.PublishAndAwaitPushAsync(BuildOrder(
            OrderStatus.Rejected,
            filled: 0m,
            rejectReason: "insufficient balance"));

        using var body = JsonDocument.Parse(captured.Body);
        body.RootElement.GetProperty("text").GetString().Should().Be(
            "Order rejected: BTC/USDT — insufficient balance (paper, Paper)");
    }

    #endregion Tests: the journaled pushes

    #region Tests: the quiet unconfigured state

    /// <summary>
    /// With the fills opt-in off, a journal fill dispatches nothing (the
    /// trap stays parked) and logs nothing loud — the unconfigured state is
    /// quiet on both the bridge's and the sink's own lanes.
    /// </summary>
    [Fact]
    public async Task AFlagOff_QuietlySendsNothing()
    {
        await using var rig = await E2eRig.CreateAsync(fills: false);
        var trap = rig.Api.ServeAsync(200, """{"ok":true}""");

        rig.Center.PublishTradeOutcome(BuildOrder(OrderStatus.Filled, filled: 0.02m));
        await Task.Delay(200);

        trap.IsCompleted.Should().BeFalse("the fills opt-in is off — nothing reached the stub");
        rig.SinkLogger.Entries.Should().NotContain(entry => entry.Level >= LogLevel.Warning);
        rig.BridgeLogger.Entries.Should().NotContain(entry => entry.Level >= LogLevel.Warning);
    }

    #endregion Tests: the quiet unconfigured state

    #region Tests: the best-effort degrade lane

    /// <summary>
    /// A stub outage degrades into exactly one warning on the sink's own
    /// lane — the push never faults, the bridge's lane stays clean and the
    /// center keeps serving the next outcome.
    /// </summary>
    [Fact]
    public async Task AStubOutage_DegradesIntoOneWarning_AndTheCenterKeepsServing()
    {
        await using var rig = await E2eRig.CreateAsync();
        var failed = rig.Api.ServeAsync(500, """{"ok":false,"description":"internal bot error"}""");

        rig.Center.PublishTradeOutcome(BuildOrder(OrderStatus.Filled, filled: 0.02m));
        await rig.Bridge.LastSendForTests;
        await failed;

        rig.SinkLogger.Entries.Should().ContainSingle(
            entry => entry.Level == LogLevel.Warning,
            "a failed push is the sink's one logged warning (no retries, no escalation)");
        rig.BridgeLogger.Entries.Should().NotContain(
            entry => entry.Level >= LogLevel.Warning,
            "the send's fault stayed inside the sink's own degrade lane");

        var served = rig.Api.ServeAsync(200, """{"ok":true}""");
        rig.Center.PublishTradeOutcome(BuildOrder(OrderStatus.Filled, filled: 0.01m));
        await rig.Bridge.LastSendForTests;
        var captured = await served;

        captured.Body.Should().Contain("0.01000000", "the center keeps serving past the failed push");
    }

    #endregion Tests: the best-effort degrade lane

    #region Rig

    /// <summary>
    /// One wired e2e rig: the loopback stub, the production-shaped named
    /// HttpClient factory transport, the opted-in settings, the stubbed
    /// secret store, the real notification center and the real bridge.
    /// </summary>
    private sealed class E2eRig : IAsyncDisposable
    {
        private E2eRig(
            LoopbackTelegramApi api,
            ServiceProvider provider,
            NotificationCenter center,
            TelegramNotificationBridge bridge,
            SpyLogger<TelegramNotificationBridge> bridgeLogger,
            SpyLogger<TelegramNotifierService> sinkLogger)
        {
            Api = api;
            Provider = provider;
            Center = center;
            Bridge = bridge;
            BridgeLogger = bridgeLogger;
            SinkLogger = sinkLogger;
        }

        internal LoopbackTelegramApi Api { get; }

        internal ServiceProvider Provider { get; }

        internal NotificationCenter Center { get; }

        internal TelegramNotificationBridge Bridge { get; }

        internal SpyLogger<TelegramNotificationBridge> BridgeLogger { get; }

        internal SpyLogger<TelegramNotifierService> SinkLogger { get; }

        internal static async Task<E2eRig> CreateAsync(bool fills = true, bool cancels = true, bool rejects = true)
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
            var transport = new TelegramBotClient(provider.GetRequiredService<IHttpClientFactory>());

            var sinkLogger = new SpyLogger<TelegramNotifierService>();
            var notifier = new TelegramNotifierService(settings, secrets, transport, sinkLogger);

            var center = new NotificationCenter(
                settings,
                NullLogger<NotificationCenter>.Instance,
                TimeSpan.FromMilliseconds(100));

            var bridgeLogger = new SpyLogger<TelegramNotificationBridge>();
            var bridge = new TelegramNotificationBridge(
                center.Events,
                new BehaviorSubject<TradingLane>(TradingLane.Paper),
                notifier,
                new ResxCatalogLocalization("en"),
                bridgeLogger);
            await bridge.InitializeAsync();

            return new E2eRig(api, provider, center, bridge, bridgeLogger, sinkLogger);
        }

        /// <summary>Publish one journaled outcome and await its push deterministically: the tracked send handle rides the stub's capture.</summary>
        internal async Task<LoopbackTelegramApi.CapturedTelegramRequest> PublishAndAwaitPushAsync(Order order)
        {
            var serve = Api.ServeAsync(200, """{"ok":true}""");
            Center.PublishTradeOutcome(order);
            await Bridge.LastSendForTests;
            return await serve;
        }

        public async ValueTask DisposeAsync()
        {
            Bridge.Dispose();
            Provider.Dispose();
            Api.Dispose();
            await Task.CompletedTask;
        }
    }

    #endregion Rig
}
