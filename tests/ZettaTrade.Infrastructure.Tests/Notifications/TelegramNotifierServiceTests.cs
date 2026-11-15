namespace ZettaTrade.Infrastructure.Tests.Notifications;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Livery.Localization;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Infrastructure.Notifications;

/// <summary>
/// The Telegram sink's best-effort facts (T-041 under the T-260 sink
/// contract), exercised against the loopback Bot API stub: a configured
/// send composes INSIDE the sink from the mapped event's facts and POSTs
/// the message to the user's own bot exactly once (the token on the
/// request path, the chat id and the text in the JSON body); the quiet
/// no-sink states (the kind's opt-in off, no chat id, no token) dispatch
/// nothing and log nothing loud; the failure matrix (a 5xx, a negative
/// envelope, a malformed body, a hanging API that rides out the transport
/// timeout, a fill without its price fact, a kind the routing has no flag
/// for) degrades into one logged warning, never a throw, never a retry —
/// and no failure report ever carries the bot token.
/// </summary>
public sealed class TelegramNotifierServiceTests
{
    #region Constants

    /// <summary>The chat id one configured sink sends to.</summary>
    private const string TestChatId = "424242";

    /// <summary>The stub catalog's composed fill line: the template, the resolved side and lane words, the event's own facts.</summary>
    private const string ComposedFillText = "[Notif.TradeFill|[Order.Buy];0.50000000;BTC/USDT;50000.00000000;bybit;[Trading.Lane.Paper]]";

    #endregion Constants

    #region Tests: the configured happy path

    /// <summary>
    /// A configured, opted-in send composes the message inside the sink and
    /// POSTs it once: the token rides the path, the body carries chat_id +
    /// the composed text.
    /// </summary>
    [Fact]
    public async Task AConfiguredSend_ComposesInsideAndPostsTheMessageToTheUsersBot()
    {
        await using var rig = await SinkRig.CreateAsync(fillFlag: true);
        var serve = rig.Api.ServeAsync(200, """{"ok":true,"result":{"message_id":7}}""");

        await rig.Notifier.HandleAsync(FillEvent());

        var captured = await serve;

        captured.Method.Should().Be("POST");
        captured.Path.Should().Be($"/bot{LoopbackHttpApi.FakeToken}/sendMessage");
        captured.ContentType.Should().StartWith("application/json");
        using var body = JsonDocument.Parse(captured.Body);
        body.RootElement.GetProperty("chat_id").GetString().Should().Be(TestChatId);
        body.RootElement.GetProperty("text").GetString().Should().Be(ComposedFillText);
        rig.Logger.Entries.Should().NotContain(
            entry => entry.Level == LogLevel.Warning,
            "a delivered send stays quiet above the information lane");
    }

    /// <summary>
    /// Every kind composes its own template from the mapped event's own
    /// facts — the sink renders the vocabulary's three outcomes each with
    /// its own line.
    /// </summary>
    [Fact]
    public async Task EveryKind_ComposesItsOwnLine()
    {
        await using var rig = await SinkRig.CreateAsync(fillFlag: true, cancelsFlag: true, rejectsFlag: true);

        var fillServe = rig.Api.ServeAsync(200, """{"ok":true}""");
        await rig.Notifier.HandleAsync(FillEvent());
        (await fillServe).Body.Should().Contain("[Notif.TradeFill|");

        var cancelServe = rig.Api.ServeAsync(200, """{"ok":true}""");
        await rig.Notifier.HandleAsync(TradeEvent(TradeNotificationKind.Cancel, "BTC/USDT"));
        (await cancelServe).Body.Should().Contain("[Notif.TradeCancelOn|");

        var rejectServe = rig.Api.ServeAsync(200, """{"ok":true}""");
        await rig.Notifier.HandleAsync(TradeEvent(TradeNotificationKind.Reject, "BTC/USDT", reason: "the venue refused"));
        (await rejectServe).Body.Should().Contain("[Notif.TradeReject|");
    }

    #endregion Tests: the configured happy path

    #region Tests: the quiet no-sink states

    /// <summary>The kind's opt-in off means quietly no sink: nothing hits the stub, nothing warns.</summary>
    [Fact]
    public async Task TheFlagOff_QuietlySendsNothing()
    {
        await using var rig = await SinkRig.CreateAsync(fillFlag: false);

        await rig.ShouldSendNothingAsync("the fills flag is off");
    }

    /// <summary>A missing chat id is the unconfigured state: quietly no send.</summary>
    [Fact]
    public async Task AMissingChatId_QuietlySendsNothing()
    {
        await using var rig = await SinkRig.CreateAsync(fillFlag: true, chatId: "");

        await rig.ShouldSendNothingAsync("no chat id is stored");
    }

    /// <summary>A missing bot token is the unconfigured state: quietly no send (absence never masquerades as a fault).</summary>
    [Fact]
    public async Task AMissingToken_QuietlySendsNothing()
    {
        await using var rig = await SinkRig.CreateAsync(fillFlag: true, token: null);

        await rig.ShouldSendNothingAsync("no token is stored");
    }

    #endregion Tests: the quiet no-sink states

    #region Tests: the best-effort failure matrix

    /// <summary>A 5xx answer degrades into one logged warning: the send completes, no throw, no retry (exactly one POST).</summary>
    [Fact]
    public async Task AServerError_DegradesToOneWarning_NeverThrows()
    {
        await using var rig = await SinkRig.CreateAsync(fillFlag: true);
        var serve = rig.Api.ServeAsync(500, """{"ok":false,"description":"internal bot error"}""");

        var send = () => rig.Notifier.HandleAsync(FillEvent());

        await send.Should().NotThrowAsync("a failed send is the sink's degrade lane's business, not the caller's exception");
        (await serve).Method.Should().Be("POST");
        rig.Logger.Entries.Should().ContainSingle(
            entry => entry.Level == LogLevel.Warning,
            "a failed send logs its warning exactly once (no retries)");
    }

    /// <summary>An ok:false envelope is a failed send: one warning, no throw.</summary>
    [Fact]
    public async Task ANegativeEnvelope_DegradesToOneWarning()
    {
        await using var rig = await SinkRig.CreateAsync(fillFlag: true);
        var serve = rig.Api.ServeAsync(200, """{"ok":false,"description":"chat not found"}""");

        var send = () => rig.Notifier.HandleAsync(FillEvent());

        await send.Should().NotThrowAsync();
        await serve;
        rig.Logger.Entries.Should().Contain(entry =>
            entry.Level == LogLevel.Warning &&
            entry.Exception != null &&
            entry.Exception.Message.Contains("chat not found", StringComparison.Ordinal),
            "the API's own refusal reason rides the degrade lane");
    }

    /// <summary>A malformed body is a failed send: one warning, no throw.</summary>
    [Fact]
    public async Task AMalformedBody_DegradesToOneWarning()
    {
        await using var rig = await SinkRig.CreateAsync(fillFlag: true);
        var serve = rig.Api.ServeAsync(200, "<not-json>");

        var send = () => rig.Notifier.HandleAsync(FillEvent());

        await send.Should().NotThrowAsync();
        await serve;
        rig.Logger.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Warning);
    }

    /// <summary>An API that holds the connection past the transport timeout degrades into one warning: the send settles bounded, the caller never hangs.</summary>
    [Fact]
    public async Task AHangingApi_RidesOutTheTimeout_IntoOneWarning()
    {
        await using var rig = await SinkRig.CreateAsync(fillFlag: true, timeout: TimeSpan.FromMilliseconds(300));
        var serve = rig.Api.ServeHangingAsync();

        var send = () => rig.Notifier.HandleAsync(FillEvent());

        await send.Should().NotThrowAsync();
        (await serve).Method.Should().Be("POST", "the hanging request still left the client (the timeout cut it)");
        rig.Logger.Entries.Should().ContainSingle(
            entry => entry.Level == LogLevel.Warning && entry.Exception != null,
            "the transport timeout lands in the degrade lane with its fault attached");
    }

    /// <summary>No failure report ever names the bot token (the fault paths report the status or the API's own description only).</summary>
    [Fact]
    public async Task NoFailureReport_CarriesTheToken()
    {
        await using var rig = await SinkRig.CreateAsync(fillFlag: true);
        var serve = rig.Api.ServeAsync(500, """{"ok":false,"description":"internal bot error"}""");

        await rig.Notifier.HandleAsync(FillEvent());
        await serve;

        rig.Logger.Entries.Should().NotContain(
            entry => entry.Message.Contains(LoopbackHttpApi.FakeToken, StringComparison.Ordinal),
            "the token never rides a log line");
    }

    /// <summary>A failed send leaves the next send unharmed: the sink keeps serving (no poisoned state).</summary>
    [Fact]
    public async Task AFailedSend_DoesNotPoisonTheNextOne()
    {
        await using var rig = await SinkRig.CreateAsync(fillFlag: true);

        var failed = rig.Api.ServeAsync(500, """{"ok":false}""");
        await rig.Notifier.HandleAsync(FillEvent());
        await failed;

        var served = rig.Api.ServeAsync(200, """{"ok":true}""");
        await rig.Notifier.HandleAsync(FillEvent());
        var captured = await served;

        captured.Body.Should().Contain("[Notif.TradeFill|");
        rig.Logger.Entries.Should().NotContain(
            entry => entry.Level == LogLevel.Error,
            "a degraded send never escalates the sink's lane");
    }

    #endregion Tests: the best-effort failure matrix

    #region Tests: the composition and routing faults

    /// <summary>
    /// A fill whose event carries no price fact is a surface-contract
    /// fault: the composition degrades into the sink's own warning lane
    /// with its fault attached, nothing hits the stub and nothing
    /// fabricated is ever sent.
    /// </summary>
    [Fact]
    public async Task AFillWithoutPriceFact_DegradesToOneWarning_AndSendsNothing()
    {
        await using var rig = await SinkRig.CreateAsync(fillFlag: true);
        var trap = rig.Api.ServeAsync(200, """{"ok":true}""");

        var send = () => rig.Notifier.HandleAsync(FillEvent(price: null));

        await send.Should().NotThrowAsync("the sink never faults — the compose fault degrades");
        await Task.Delay(200);
        trap.IsCompleted.Should().BeFalse("a fill without its price fact never fabricates a message");
        rig.Logger.Entries.Should().Contain(entry =>
            entry.Level == LogLevel.Warning &&
            entry.Exception is InvalidOperationException &&
            entry.Exception.Message.Contains("no price fact", StringComparison.Ordinal),
            "the price guard's fault rides the degrade lane with its own type attached");
    }

    /// <summary>
    /// A kind the routing has no opt-in flag for is refused loudly into the
    /// degrade lane: one warning, nothing sent, never a substituted
    /// default — the invalid-value canon.
    /// </summary>
    [Fact]
    public async Task AnUnroutableKind_DegradesToOneWarning_AndSendsNothing()
    {
        await using var rig = await SinkRig.CreateAsync(fillFlag: true);
        var trap = rig.Api.ServeAsync(200, """{"ok":true}""");

        var send = () => rig.Notifier.HandleAsync(TradeEvent((TradeNotificationKind)999));

        await send.Should().NotThrowAsync("the sink never faults — the routing fault degrades");
        await Task.Delay(200);
        trap.IsCompleted.Should().BeFalse("an unknown kind never rides a substituted flag");
        rig.Logger.Entries.Should().Contain(entry =>
            entry.Level == LogLevel.Warning &&
            entry.Exception is ArgumentOutOfRangeException,
            "the unknown kind lands in the degrade lane as the argument fault it is");
    }

    #endregion Tests: the composition and routing faults

    #region Rig

    /// <summary>One wired sink rig: the loopback stub, the pinned client, the settings, the secret store, the stub catalog and the logger spy.</summary>
    private sealed class SinkRig : IAsyncDisposable
    {
        private SinkRig(LoopbackHttpApi api, TelegramNotifierService notifier, SpyLogger logger)
        {
            Api = api;
            Notifier = notifier;
            Logger = logger;
        }

        internal LoopbackHttpApi Api { get; }

        internal TelegramNotifierService Notifier { get; }

        internal SpyLogger Logger { get; }

        /// <summary>
        /// Create the rig: the flags ride an in-memory settings store, the
        /// token rides a stubbed secret store (absent when
        /// <paramref name="token"/> is null), the catalog is the stub and
        /// the transport is a pinned HttpClient aimed at the loopback stub
        /// (its timeout the test's own, so a hanging API never holds a test
        /// for the real 10 s).
        /// </summary>
        internal static async Task<SinkRig> CreateAsync(
            bool fillFlag,
            bool cancelsFlag = false,
            bool rejectsFlag = false,
            string chatId = TestChatId,
            string? token = LoopbackHttpApi.FakeToken,
            TimeSpan? timeout = null)
        {
            var api = LoopbackHttpApi.Start();
            var settings = new TestSettings();
            await settings.SetAsync(TelegramNotifierService.SettingsSection, TelegramNotifierService.FillsEnabledKey, fillFlag);
            await settings.SetAsync(TelegramNotifierService.SettingsSection, TelegramNotifierService.CancelsEnabledKey, cancelsFlag);
            await settings.SetAsync(TelegramNotifierService.SettingsSection, TelegramNotifierService.RejectsEnabledKey, rejectsFlag);
            await settings.SetAsync(TelegramNotifierService.SettingsSection, TelegramNotifierService.ChatIdKey, chatId);

            var secrets = Substitute.For<ISecretStore>();
            if (token is not null)
            {
                secrets.GetAsync(TelegramNotifierService.BotTokenSecretKey, Arg.Any<CancellationToken>())
                    .Returns(token);
            }

            var transport = new HttpClient { BaseAddress = api.BaseUrl, Timeout = timeout ?? TimeSpan.FromSeconds(5) };
            var logger = new SpyLogger();
            var notifier = new TelegramNotifierService(
                settings,
                secrets,
                new TelegramBotClient(transport),
                new StubLocalization(),
                logger);

            return new SinkRig(api, notifier, logger);
        }

        /// <summary>
        /// Assert the sink stayed quiet: the awaited send already settled
        /// (the gate returned before any dispatch), a trap request is parked
        /// on the stub and never completes, and no warning was logged.
        /// </summary>
        internal async Task ShouldSendNothingAsync(string because)
        {
            var trap = Api.ServeHangingAsync();

            var send = () => Notifier.HandleAsync(FillEvent());
            await send.Should().NotThrowAsync();

            // Belt-and-suspenders settle: a request would have to arrive
            // during the awaited send's own lifetime; the settle only pads
            // the trap check against the loopback's own queueing.
            await Task.Delay(200);

            trap.IsCompleted.Should().BeFalse($"no request reached the stub ({because})");
            Logger.Entries.Should().NotContain(entry => entry.Level == LogLevel.Warning);
        }

        public async ValueTask DisposeAsync()
        {
            Api.Dispose();
            await Task.CompletedTask;
        }
    }

    /// <summary>
    /// The sink tests' localization stub: a deterministic line per catalog
    /// request — "[key]" for a word, "[key|arg;arg;…]" for a template — so
    /// a send's wire text pins the template the sink composed with and the
    /// exact facts it fed it (the real-catalog wordings stay the App tests'
    /// goldens, which the shipped resx backs).
    /// </summary>
    private sealed class StubLocalization : ILocalizationManager
    {
        public string CurrentLanguage => "en";

        public IReadOnlyList<LanguageInfo> AvailableLanguages { get; } = [];

        public Task SetLanguageAsync(string cultureCode, CancellationToken ct = default) => Task.CompletedTask;

        public string GetString(string key) => $"[{key}]";

        public string GetString(string key, params object[] args) =>
            $"[{key}|{string.Join(";", args)}]";

        public IObservable<string> LanguageChanged { get; } = Observable.Never<string>();

        public IObservable<string> GetObservable(string key) => Observable.Never<string>();
    }

    /// <summary>One mapped trade event with a fill's canonical facts (the price overridable for the contract-fault facts).</summary>
    private static TradeNotificationEvent FillEvent(decimal? price = 50_000m) => new(
        TradeNotificationKind.Fill,
        "BTC/USDT",
        OrderSide.Buy,
        0.5m,
        price,
        "bybit",
        string.Empty,
        TradeNotificationLane.Paper);

    /// <summary>One mapped trade event with the kind's shape (the price off — only fills carry one).</summary>
    private static TradeNotificationEvent TradeEvent(
        TradeNotificationKind kind,
        string symbol = "BTC/USDT",
        string reason = "") => new(
        kind,
        symbol,
        OrderSide.Buy,
        0.5m,
        null,
        "bybit",
        reason,
        TradeNotificationLane.Paper);

    /// <summary>The in-memory settings the flags ride (the reactive stream shape the sink never subscribes).</summary>
    private sealed class TestSettings : ISettingsService
    {
        private readonly Dictionary<(string Section, string Key), object?> _values = new();

        public IObservable<SettingsChange> Changes => Observable.Never<SettingsChange>();

        public Task<T> GetAsync<T>(string section, string key, T defaultValue = default!)
        {
            _values.TryGetValue((section, key), out var stored);
            return Task.FromResult(stored is T typed ? typed : defaultValue);
        }

        public Task SetAsync<T>(string section, string key, T value)
        {
            _values[(section, key)] = value;
            return Task.CompletedTask;
        }

        public Task SaveAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task ReloadAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>The logger spy: records the level + message + exception triples (the sink's own degrade lane's probe).</summary>
    private sealed class SpyLogger : ILogger<TelegramNotifierService>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception), exception));
    }

    #endregion Rig
}
