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
/// The webhook sinks' best-effort facts (T-261, the T-260 sink contract
/// generalized to the webhook transports), exercised against the loopback
/// stub: a configured, opted-in send composes INSIDE the sink from the
/// mapped event's facts and POSTs the channel's own body exactly once —
/// Discord's embeds JSON with the severity color, Slack's message-text
/// JSON, ntfy's plain text on the topic URL; the quiet no-sink states
/// (the kind's opt-in off, a missing webhook URL) dispatch nothing and log
/// nothing loud; the failure matrix (a 5xx, a hanging endpoint that rides
/// out the transport timeout, a fill without its price fact, a kind the
/// routing has no flag for) degrades into one logged warning, never a
/// throw, never a retry — and no failure report ever carries the webhook
/// URL (the Discord and Slack URLs embed their own secret material).
/// </summary>
public sealed class WebhookSinksTests
{
    #region Constants

    /// <summary>The stub catalog's composed fill line (the shared composer's own golden).</summary>
    private const string ComposedFillText = "[Notif.TradeFill|[Order.Buy];0.50000000;BTC/USDT;50000.00000000;bybit;[Trading.Lane.Paper]]";

    #endregion Constants

    #region Tests: the per-channel body goldens

    /// <summary>
    /// A configured, opted-in fill posts the channel's own body exactly
    /// once: the URL rides as given (an absolute address per send), the
    /// body shape and content type are the channel's own, the composed
    /// trade line is the payload.
    /// </summary>
    [Theory]
    [InlineData(WebhookChannel.Discord, 204)]
    [InlineData(WebhookChannel.Slack, 200)]
    [InlineData(WebhookChannel.Ntfy, 200)]
    public async Task AConfiguredFill_PostsTheChannelBody(WebhookChannel channel, int successStatus)
    {
        await using var rig = await WebhookRig.CreateAsync(channel);
        var serve = rig.Api.ServeAsync(successStatus, "{}");

        var send = () => rig.Sink.HandleAsync(FillEvent());

        await send.Should().NotThrowAsync();
        var captured = await serve;

        captured.Method.Should().Be("POST");
        captured.Path.Should().Be(rig.TopicPath, "the sink posts to the URL exactly as stored (no path rewriting)");
        switch (channel)
        {
            case WebhookChannel.Discord:
                captured.ContentType.Should().StartWith("application/json");
                using (var body = JsonDocument.Parse(captured.Body))
                {
                    var embed = body.RootElement.GetProperty("embeds").EnumerateArray().Single();
                    embed.GetProperty("description").GetString().Should().Be(ComposedFillText);
                    embed.GetProperty("color").GetInt32().Should().Be(DiscordSink.FillColor);
                }

                break;
            case WebhookChannel.Slack:
                captured.ContentType.Should().StartWith("application/json");
                using (var body = JsonDocument.Parse(captured.Body))
                {
                    body.RootElement.GetProperty("text").GetString().Should().Be(ComposedFillText);
                }

                break;
            case WebhookChannel.Ntfy:
                captured.ContentType.Should().StartWith("text/plain", "the ntfy publish contract is an honest plain-text POST");
                captured.Body.Should().Be(ComposedFillText);
                break;
        }

        rig.Log.Entries.Should().NotContain(
            entry => entry.Level == LogLevel.Warning,
            "a delivered send stays quiet above the information lane");
    }

    /// <summary>The Discord embed carries the severity color by kind (fill green, cancel blue, reject red).</summary>
    [Fact]
    public async Task TheDiscordEmbed_CarriesTheSeverityColorByKind()
    {
        await using var rig = await WebhookRig.CreateAsync(WebhookChannel.Discord, cancelsFlag: true, rejectsFlag: true);

        var fillServe = rig.Api.ServeAsync(200, "{}");
        await rig.Sink.HandleAsync(FillEvent());
        var fill = await fillServe;
        EmbedColorOf(fill).Should().Be(DiscordSink.FillColor, "a delivered fill rides the green");

        var cancelServe = rig.Api.ServeAsync(200, "{}");
        await rig.Sink.HandleAsync(TradeEvent(TradeNotificationKind.Cancel, "BTC/USDT"));
        EmbedColorOf(await cancelServe).Should().Be(DiscordSink.CancelColor, "an accepted cancellation rides the blue");

        var rejectServe = rig.Api.ServeAsync(200, "{}");
        await rig.Sink.HandleAsync(TradeEvent(TradeNotificationKind.Reject, "BTC/USDT", reason: "the venue refused"));
        EmbedColorOf(await rejectServe).Should().Be(DiscordSink.RejectColor, "a refused order rides the red");
    }

    /// <summary>Every kind composes its own line into the channel body — the vocabulary's three outcomes each get their own template.</summary>
    [Theory]
    [InlineData(WebhookChannel.Slack)]
    [InlineData(WebhookChannel.Ntfy)]
    public async Task EveryKind_ComposesItsOwnLine(WebhookChannel channel)
    {
        await using var rig = await WebhookRig.CreateAsync(channel, cancelsFlag: true, rejectsFlag: true);

        var fillServe = rig.Api.ServeAsync(200, "{}");
        await rig.Sink.HandleAsync(FillEvent());
        (await fillServe).Body.Should().Contain("[Notif.TradeFill|");

        var cancelServe = rig.Api.ServeAsync(200, "{}");
        await rig.Sink.HandleAsync(TradeEvent(TradeNotificationKind.Cancel, "BTC/USDT"));
        (await cancelServe).Body.Should().Contain("[Notif.TradeCancelOn|");

        var rejectServe = rig.Api.ServeAsync(200, "{}");
        await rig.Sink.HandleAsync(TradeEvent(TradeNotificationKind.Reject, "BTC/USDT", reason: "the venue refused"));
        (await rejectServe).Body.Should().Contain("[Notif.TradeReject|");
    }

    #endregion Tests: the per-channel body goldens

    #region Tests: the quiet no-sink states (the shared matrix)

    /// <summary>The kind's opt-in off means quietly no sink: nothing hits the stub, nothing warns.</summary>
    [Theory]
    [InlineData(WebhookChannel.Discord)]
    [InlineData(WebhookChannel.Slack)]
    [InlineData(WebhookChannel.Ntfy)]
    public async Task TheFlagOff_QuietlySendsNothing(WebhookChannel channel)
    {
        await using var rig = await WebhookRig.CreateAsync(channel, fillFlag: false);

        await rig.ShouldSendNothingAsync("the fills flag is off");
    }

    /// <summary>A missing webhook URL is the unconfigured state: quietly no send (absence never masquerades as a fault).</summary>
    [Theory]
    [InlineData(WebhookChannel.Discord)]
    [InlineData(WebhookChannel.Slack)]
    [InlineData(WebhookChannel.Ntfy)]
    public async Task AMissingUrl_QuietlySendsNothing(WebhookChannel channel)
    {
        await using var rig = await WebhookRig.CreateAsync(channel, url: null);

        await rig.ShouldSendNothingAsync("no webhook URL is stored");
    }

    #endregion Tests: the quiet no-sink states (the shared matrix)

    #region Tests: the best-effort failure matrix (the shared matrix)

    /// <summary>A 5xx answer degrades into one logged warning: the send completes, no throw, no retry (exactly one POST).</summary>
    [Theory]
    [InlineData(WebhookChannel.Discord)]
    [InlineData(WebhookChannel.Slack)]
    [InlineData(WebhookChannel.Ntfy)]
    public async Task AServerError_DegradesToOneWarning_NeverThrows(WebhookChannel channel)
    {
        await using var rig = await WebhookRig.CreateAsync(channel);
        var serve = rig.Api.ServeAsync(500, "{}");

        var send = () => rig.Sink.HandleAsync(FillEvent());

        await send.Should().NotThrowAsync("a failed send is the sink's degrade lane's business, not the caller's exception");
        (await serve).Method.Should().Be("POST");
        rig.Log.Entries.Should().ContainSingle(
            entry => entry.Level == LogLevel.Warning,
            "a failed send logs its warning exactly once (no retries)");
    }

    /// <summary>An endpoint that holds the connection past the transport timeout degrades into one warning: the send settles bounded, the caller never hangs.</summary>
    [Theory]
    [InlineData(WebhookChannel.Discord)]
    [InlineData(WebhookChannel.Slack)]
    [InlineData(WebhookChannel.Ntfy)]
    public async Task AHangingEndpoint_RidesOutTheTimeout_IntoOneWarning(WebhookChannel channel)
    {
        await using var rig = await WebhookRig.CreateAsync(channel, timeout: TimeSpan.FromMilliseconds(300));
        var serve = rig.Api.ServeHangingAsync();

        var send = () => rig.Sink.HandleAsync(FillEvent());

        await send.Should().NotThrowAsync();
        (await serve).Method.Should().Be("POST", "the hanging request still left the client (the timeout cut it)");
        rig.Log.Entries.Should().ContainSingle(
            entry => entry.Level == LogLevel.Warning && entry.Exception != null,
            "the transport timeout lands in the degrade lane with its fault attached");
    }

    /// <summary>No failure report ever names the webhook URL: the Discord and Slack URLs embed the webhooks' own secret material, the topic URL the user's own address — a fault names the status only.</summary>
    [Theory]
    [InlineData(WebhookChannel.Discord)]
    [InlineData(WebhookChannel.Slack)]
    [InlineData(WebhookChannel.Ntfy)]
    public async Task NoFailureReport_CarriesTheUrl(WebhookChannel channel)
    {
        await using var rig = await WebhookRig.CreateAsync(channel);
        var serve = rig.Api.ServeAsync(500, "{}");

        await rig.Sink.HandleAsync(FillEvent());
        await serve;

        rig.Log.Entries.Should().NotContain(
            entry => entry.Message.Contains(rig.TopicPath, StringComparison.Ordinal),
            "the URL never rides a log line");
    }

    /// <summary>A failed send leaves the next send unharmed: the sink keeps serving (no poisoned state).</summary>
    [Theory]
    [InlineData(WebhookChannel.Discord)]
    [InlineData(WebhookChannel.Slack)]
    [InlineData(WebhookChannel.Ntfy)]
    public async Task AFailedSend_DoesNotPoisonTheNextOne(WebhookChannel channel)
    {
        await using var rig = await WebhookRig.CreateAsync(channel);

        var failed = rig.Api.ServeAsync(500, "{}");
        await rig.Sink.HandleAsync(FillEvent());
        await failed;

        var served = rig.Api.ServeAsync(200, "{}");
        await rig.Sink.HandleAsync(FillEvent());
        var captured = await served;

        captured.Body.Should().Contain("[Notif.TradeFill|");
        rig.Log.Entries.Should().NotContain(
            entry => entry.Level == LogLevel.Error,
            "a degraded send never escalates the sink's lane");
    }

    #endregion Tests: the best-effort failure matrix (the shared matrix)

    #region Tests: the composition and routing faults (the shared matrix)

    /// <summary>
    /// A fill whose event carries no price fact is a surface-contract
    /// fault: the composition degrades into the sink's own warning lane
    /// with its fault attached, nothing hits the stub and nothing
    /// fabricated is ever sent.
    /// </summary>
    [Theory]
    [InlineData(WebhookChannel.Discord)]
    [InlineData(WebhookChannel.Slack)]
    [InlineData(WebhookChannel.Ntfy)]
    public async Task AFillWithoutPriceFact_DegradesToOneWarning_AndSendsNothing(WebhookChannel channel)
    {
        await using var rig = await WebhookRig.CreateAsync(channel);
        var trap = rig.Api.ServeAsync(200, "{}");

        var send = () => rig.Sink.HandleAsync(FillEvent(price: null));

        await send.Should().NotThrowAsync("the sink never faults — the compose fault degrades");
        await Task.Delay(200);
        trap.IsCompleted.Should().BeFalse("a fill without its price fact never fabricates a message");
        rig.Log.Entries.Should().Contain(entry =>
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
    [Theory]
    [InlineData(WebhookChannel.Discord)]
    [InlineData(WebhookChannel.Slack)]
    [InlineData(WebhookChannel.Ntfy)]
    public async Task AnUnroutableKind_DegradesToOneWarning_AndSendsNothing(WebhookChannel channel)
    {
        await using var rig = await WebhookRig.CreateAsync(channel);
        var trap = rig.Api.ServeAsync(200, "{}");

        var send = () => rig.Sink.HandleAsync(TradeEvent((TradeNotificationKind)999));

        await send.Should().NotThrowAsync("the sink never faults — the routing fault degrades");
        await Task.Delay(200);
        trap.IsCompleted.Should().BeFalse("an unknown kind never rides a substituted flag");
        rig.Log.Entries.Should().Contain(entry =>
            entry.Level == LogLevel.Warning &&
            entry.Exception is ArgumentOutOfRangeException,
            "the unknown kind lands in the degrade lane as the argument fault it is");
    }

    #endregion Tests: the composition and routing faults (the shared matrix)

    #region Helpers

    /// <summary>The Discord capture's embed color (the single-embed body the sink sends).</summary>
    private static int EmbedColorOf(LoopbackHttpApi.CapturedRequest captured)
    {
        using var body = JsonDocument.Parse(captured.Body);
        return body.RootElement.GetProperty("embeds").EnumerateArray().Single()
            .GetProperty("color").GetInt32();
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

    #endregion Helpers

    #region Rig

    /// <summary>The webhook transports the matrix walks (public: the theory method signatures carry it).</summary>
    public enum WebhookChannel
    {
        Discord = 0,
        Slack = 1,
        Ntfy = 2,
    }

    /// <summary>One wired webhook rig: the loopback stub, the pinned client, the channel's settings/secret state, the stub catalog and the logger spy.</summary>
    private sealed class WebhookRig : IAsyncDisposable
    {
        private WebhookRig(LoopbackHttpApi api, INotificationSink sink, SpyLog log, string topicPath)
        {
            Api = api;
            Sink = sink;
            Log = log;
            TopicPath = topicPath;
        }

        internal LoopbackHttpApi Api { get; }

        internal INotificationSink Sink { get; }

        internal SpyLog Log { get; }

        /// <summary>The request path one configured send lands on (also the no-leak probe — the URL never rides a log line).</summary>
        internal string TopicPath { get; }

        /// <summary>
        /// Create the rig: the flags ride an in-memory settings store, the
        /// Discord/Slack URL rides a stubbed secret store (absent when
        /// <paramref name="url"/> is null), the ntfy URL rides the settings
        /// (public by design), the catalog is the stub and the transport is
        /// a pinned HttpClient aimed at the loopback stub (its timeout the
        /// test's own, so a hanging endpoint never holds a test for the
        /// real 10 s).
        /// </summary>
        internal static async Task<WebhookRig> CreateAsync(
            WebhookChannel channel,
            bool fillFlag = true,
            bool cancelsFlag = false,
            bool rejectsFlag = false,
            string? url = "stored",
            TimeSpan? timeout = null)
        {
            var api = LoopbackHttpApi.Start();
            var settings = new TestSettings();
            var log = new SpyLog();

            // The channel's own keys and its transport address on the
            // loopback: the path double-checks the sink posts to the stored
            // URL verbatim.
            var (section, fillsKey, cancelsKey, rejectsKey) = channel switch
            {
                WebhookChannel.Discord => (
                    DiscordSink.SettingsSection,
                    DiscordSink.FillsEnabledKey,
                    DiscordSink.CancelsEnabledKey,
                    DiscordSink.RejectsEnabledKey),
                WebhookChannel.Slack => (
                    SlackSink.SettingsSection,
                    SlackSink.FillsEnabledKey,
                    SlackSink.CancelsEnabledKey,
                    SlackSink.RejectsEnabledKey),
                _ => (
                    NtfySink.SettingsSection,
                    NtfySink.FillsEnabledKey,
                    NtfySink.CancelsEnabledKey,
                    NtfySink.RejectsEnabledKey),
            };
            await settings.SetAsync(section, fillsKey, fillFlag);
            await settings.SetAsync(section, cancelsKey, cancelsFlag);
            await settings.SetAsync(section, rejectsKey, rejectsFlag);

            var path = channel switch
            {
                WebhookChannel.Discord => "/discord-hook",
                WebhookChannel.Slack => "/slack-hook",
                _ => "/ntfy-topic",
            };
            var topicUrl = new Uri(api.BaseUrl, path).ToString();

            HttpClient transport;
            INotificationSink sink;
            if (channel == WebhookChannel.Ntfy)
            {
                if (url is not null)
                {
                    await settings.SetAsync(NtfySink.SettingsSection, NtfySink.TopicUrlKey, topicUrl);
                }

                transport = new HttpClient { Timeout = timeout ?? TimeSpan.FromSeconds(5) };
                sink = new NtfySink(
                    settings,
                    transport,
                    new StubLocalization(),
                    new SpyLogger<NtfySink>(log));
            }
            else
            {
                var secretKey = channel == WebhookChannel.Discord
                    ? DiscordSink.WebhookUrlSecretKey
                    : SlackSink.WebhookUrlSecretKey;
                var secrets = Substitute.For<ISecretStore>();
                if (url is not null)
                {
                    secrets.GetAsync(secretKey, Arg.Any<CancellationToken>()).Returns(topicUrl);
                }

                transport = new HttpClient { Timeout = timeout ?? TimeSpan.FromSeconds(5) };
                sink = channel == WebhookChannel.Discord
                    ? new DiscordSink(
                        settings,
                        secrets,
                        transport,
                        new StubLocalization(),
                        new SpyLogger<DiscordSink>(log))
                    : new SlackSink(
                        settings,
                        secrets,
                        transport,
                        new StubLocalization(),
                        new SpyLogger<SlackSink>(log));
            }

            return new WebhookRig(api, sink, log, path);
        }

        /// <summary>
        /// Assert the sink stayed quiet: the awaited send already settled
        /// (the gate returned before any dispatch), a trap request is parked
        /// on the stub and never completes, and no warning was logged.
        /// </summary>
        internal async Task ShouldSendNothingAsync(string because)
        {
            var trap = Api.ServeHangingAsync();

            var send = () => Sink.HandleAsync(FillEvent());
            await send.Should().NotThrowAsync();

            // Belt-and-suspenders settle: a request would have to arrive
            // during the awaited send's own lifetime; the settle only pads
            // the trap check against the loopback's own queueing.
            await Task.Delay(200);

            trap.IsCompleted.Should().BeFalse($"no request reached the stub ({because})");
            Log.Entries.Should().NotContain(entry => entry.Level == LogLevel.Warning);
        }

        public async ValueTask DisposeAsync()
        {
            Api.Dispose();
            await Task.CompletedTask;
        }
    }

    /// <summary>The logger spy's entry store (the sinks' own degrade lanes' probe, shared by the typed loggers).</summary>
    private sealed class SpyLog
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];
    }

    /// <summary>The typed logger spy: records the level + message + exception triples into the shared store.</summary>
    private sealed class SpyLogger<T> : ILogger<T>
        where T : notnull
    {
        private readonly SpyLog _log;

        public SpyLogger(SpyLog log) => _log = log;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (formatter is null)
            {
                return;
            }

            _log.Entries.Add((logLevel, formatter(state, exception), exception));
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

    /// <summary>The in-memory settings the flags ride (the reactive stream shape the sink never subscribes).</summary>
    private sealed class TestSettings : ISettingsService
    {
        private readonly Dictionary<(string Section, string Key), object?> _values = new();

        public IObservable<SettingsChange> Changes => Observable.Never<SettingsChange>();

        public Task<T> GetAsync<T>(string section, string key, T defaultValue = default!) =>
            Task.FromResult(
                _values.TryGetValue((section, key), out var stored) && stored is T typed
                    ? typed
                    : defaultValue);

        public Task SetAsync<T>(string section, string key, T value)
        {
            _values[(section, key)] = value;
            return Task.CompletedTask;
        }

        public Task SaveAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task ReloadAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    #endregion Rig
}
