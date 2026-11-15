namespace ZettaTrade.Infrastructure.Tests.Notifications;

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Reactive.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
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
/// The Matrix sink's best-effort facts (T-262, the T-261 sink contract on
/// the room-push transport), exercised against the loopback stub: a
/// configured, opted-in send composes INSIDE the sink from the mapped
/// event's facts and PUTs the client-API message exactly once — the
/// transactional path (the room escaped, a fresh txn id per send), the
/// Bearer token riding the Authorization header, the m.text body carrying
/// the composed trade line; the quiet no-sink states (the kind's opt-in
/// off, a missing homeserver URL or room id, a missing access token)
/// dispatch nothing and log nothing loud; the failure matrix (a 5xx, a
/// hanging endpoint that rides out the transport timeout) degrades into
/// one logged warning, never a throw, never a retry — and no failure
/// report ever carries the access token or the homeserver address (the
/// token is the channel's secret material).
/// </summary>
public sealed class MatrixSinkTests
{
    #region Constants

    /// <summary>The stub catalog's composed fill line (the shared composer's own golden).</summary>
    private const string ComposedFillText = "[Notif.TradeFill|[Order.Buy];0.50000000;BTC/USDT;50000.00000000;bybit;[Trading.Lane.Paper]]";

    /// <summary>The stub's stored access token (sample-shaped, never a real credential).</summary>
    private const string StubAccessToken = "syt.stub.token.0";

    /// <summary>The room the stub pushes into (the internal room-id vocabulary, sample-shaped).</summary>
    private const string StubRoomId = "!room:example.org";

    #endregion Constants

    #region Tests: the client-API golden

    /// <summary>
    /// A configured, opted-in fill PUTs the client-API message exactly
    /// once: the transactional path rides the stored homeserver with the
    /// room id escaped, the access token rides the Authorization header
    /// only, the body is the m.text JSON carrying the composed trade line.
    /// </summary>
    [Fact]
    public async Task AConfiguredFill_PutsTheClientApiBody()
    {
        await using var rig = await MatrixRig.CreateAsync();
        var serve = rig.Api.ServeAsync(200, "{}");

        var send = () => rig.Sink.HandleAsync(FillEvent());

        await send.Should().NotThrowAsync();
        var captured = await serve;

        captured.Method.Should().Be("PUT", "the Matrix client API's send contract is a transactional PUT");
        Regex.IsMatch(
                captured.Path,
                "^/_matrix/client/v3/rooms/%21room%3Aexample\\.org/send/m\\.message/[0-9a-f]{32}$")
            .Should().BeTrue(
                "the send rides the homeserver's client API with the room escaped and a fresh txn id (got {0})",
                captured.Path);
        captured.Authorization.Should().Be($"Bearer {StubAccessToken}", "the token rides the Authorization header");
        captured.ContentType.Should().StartWith("application/json");
        using (var body = JsonDocument.Parse(captured.Body))
        {
            body.RootElement.GetProperty("msgtype").GetString().Should().Be("m.text");
            body.RootElement.GetProperty("body").GetString().Should().Be(ComposedFillText);
        }

        rig.Log.Entries.Should().NotContain(
            entry => entry.Level == LogLevel.Warning,
            "a delivered send stays quiet above the information lane");
    }

    /// <summary>Every delivered send carries a fresh transaction id (the sink never retries, the txn id never repeats).</summary>
    [Fact]
    public async Task TwoSends_RideFreshTransactionIds()
    {
        await using var rig = await MatrixRig.CreateAsync(cancelsFlag: true);

        var firstServe = rig.Api.ServeAsync(200, "{}");
        await rig.Sink.HandleAsync(FillEvent());
        var first = await firstServe;

        var secondServe = rig.Api.ServeAsync(200, "{}");
        await rig.Sink.HandleAsync(TradeEvent(TradeNotificationKind.Cancel, "BTC/USDT"));
        var second = await secondServe;

        second.Path.Should().NotBe(first.Path, "each send rides its own transaction id");
    }

    /// <summary>Every kind composes its own line into the message body — the vocabulary's three outcomes each get their own template.</summary>
    [Fact]
    public async Task EveryKind_ComposesItsOwnLine()
    {
        await using var rig = await MatrixRig.CreateAsync(cancelsFlag: true, rejectsFlag: true);

        var fillServe = rig.Api.ServeAsync(200, "{}");
        await rig.Sink.HandleAsync(FillEvent());
        BodyLineOf(await fillServe).Should().Contain("[Notif.TradeFill|");

        var cancelServe = rig.Api.ServeAsync(200, "{}");
        await rig.Sink.HandleAsync(TradeEvent(TradeNotificationKind.Cancel, "BTC/USDT"));
        BodyLineOf(await cancelServe).Should().Contain("[Notif.TradeCancelOn|");

        var rejectServe = rig.Api.ServeAsync(200, "{}");
        await rig.Sink.HandleAsync(TradeEvent(TradeNotificationKind.Reject, "BTC/USDT", reason: "the venue refused"));
        BodyLineOf(await rejectServe).Should().Contain("[Notif.TradeReject|");
    }

    #endregion Tests: the client-API golden

    #region Tests: the quiet no-sink states

    /// <summary>The kind's opt-in off means quietly no sink: nothing hits the stub, nothing warns.</summary>
    [Fact]
    public async Task TheFlagOff_QuietlySendsNothing()
    {
        await using var rig = await MatrixRig.CreateAsync(fillFlag: false);

        await rig.ShouldSendNothingAsync("the fills flag is off");
    }

    /// <summary>A missing address half (homeserver URL or room id) is the unconfigured state: quietly no send.</summary>
    [Theory]
    [InlineData(MatrixMissingPiece.HomeserverUrl)]
    [InlineData(MatrixMissingPiece.RoomId)]
    public async Task AMissingAddressHalf_QuietlySendsNothing(MatrixMissingPiece piece)
    {
        await using var rig = await MatrixRig.CreateAsync(missingPiece: piece);

        await rig.ShouldSendNothingAsync($"the {piece} is not stored");
    }

    /// <summary>A missing access token is the unconfigured state: quietly no send (absence never masquerades as a fault).</summary>
    [Fact]
    public async Task AMissingAccessToken_QuietlySendsNothing()
    {
        await using var rig = await MatrixRig.CreateAsync(withToken: false);

        await rig.ShouldSendNothingAsync("no access token is stored");
    }

    #endregion Tests: the quiet no-sink states

    #region Tests: the best-effort failure matrix

    /// <summary>A 5xx answer degrades into one logged warning: the send completes, no throw, no retry (exactly one PUT).</summary>
    [Fact]
    public async Task AServerError_DegradesToOneWarning_NeverThrows()
    {
        await using var rig = await MatrixRig.CreateAsync();
        var serve = rig.Api.ServeAsync(500, "{}");

        var send = () => rig.Sink.HandleAsync(FillEvent());

        await send.Should().NotThrowAsync("a failed send is the sink's degrade lane's business, not the caller's exception");
        (await serve).Method.Should().Be("PUT");
        rig.Log.Entries.Should().ContainSingle(
            entry => entry.Level == LogLevel.Warning,
            "a failed send logs its warning exactly once (no retries)");
    }

    /// <summary>An endpoint that holds the connection past the transport timeout degrades into one warning: the send settles bounded, the caller never hangs.</summary>
    [Fact]
    public async Task AHangingEndpoint_RidesOutTheTimeout_IntoOneWarning()
    {
        await using var rig = await MatrixRig.CreateAsync(timeout: TimeSpan.FromMilliseconds(300));
        var serve = rig.Api.ServeHangingAsync();

        var send = () => rig.Sink.HandleAsync(FillEvent());

        await send.Should().NotThrowAsync();
        (await serve).Method.Should().Be("PUT", "the hanging request still left the client (the timeout cut it)");
        rig.Log.Entries.Should().ContainSingle(
            entry => entry.Level == LogLevel.Warning && entry.Exception != null,
            "the transport timeout lands in the degrade lane with its fault attached");
    }

    /// <summary>
    /// No failure report ever carries the channel's secret material: the
    /// access token never rides a log line (the homeserver address is
    /// public by design, but the degrade report names the status only).
    /// </summary>
    [Fact]
    public async Task NoFailureReport_CarriesTheToken()
    {
        await using var rig = await MatrixRig.CreateAsync();
        var serve = rig.Api.ServeAsync(500, "{}");

        await rig.Sink.HandleAsync(FillEvent());
        await serve;

        rig.Log.Entries.Should().NotContain(
            entry => entry.Message.Contains(StubAccessToken, StringComparison.Ordinal),
            "the access token never rides a log line");
        rig.Log.Entries.Should().NotContain(
            entry => entry.Exception != null &&
                     entry.Exception.Message.Contains(StubAccessToken, StringComparison.Ordinal),
            "the access token never rides a fault report");
        rig.Log.Entries.Should().Contain(
            entry => entry.Level == LogLevel.Warning &&
                     entry.Exception != null &&
                     entry.Exception.Message.Contains("HTTP 500", StringComparison.Ordinal),
            "the degrade report names the answered status");
    }

    /// <summary>A failed send leaves the next send unharmed: the sink keeps serving (no poisoned state).</summary>
    [Fact]
    public async Task AFailedSend_DoesNotPoisonTheNextOne()
    {
        await using var rig = await MatrixRig.CreateAsync();

        var failed = rig.Api.ServeAsync(500, "{}");
        await rig.Sink.HandleAsync(FillEvent());
        await failed;

        var served = rig.Api.ServeAsync(200, "{}");
        await rig.Sink.HandleAsync(FillEvent());
        var captured = await served;

        BodyLineOf(captured).Should().Contain("[Notif.TradeFill|");
        rig.Log.Entries.Should().NotContain(
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
        await using var rig = await MatrixRig.CreateAsync();
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
    [Fact]
    public async Task AnUnroutableKind_DegradesToOneWarning_AndSendsNothing()
    {
        await using var rig = await MatrixRig.CreateAsync();
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

    #endregion Tests: the composition and routing faults

    #region Helpers

    /// <summary>The captured m.text body's composed line.</summary>
    private static string BodyLineOf(LoopbackHttpApi.CapturedRequest captured)
    {
        using var body = JsonDocument.Parse(captured.Body);
        return body.RootElement.GetProperty("body").GetString() ?? string.Empty;
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

    /// <summary>The Matrix address half a missing-piece fact withholds (public: the theory method signatures carry it).</summary>
    public enum MatrixMissingPiece
    {
        HomeserverUrl = 0,
        RoomId = 1,
    }

    /// <summary>One wired Matrix rig: the loopback stub, the pinned client, the flags/addresses state, the token store and the logger spy.</summary>
    private sealed class MatrixRig : IAsyncDisposable
    {
        private MatrixRig(LoopbackHttpApi api, INotificationSink sink, SpyLog log)
        {
            Api = api;
            Sink = sink;
            Log = log;
        }

        internal LoopbackHttpApi Api { get; }

        internal INotificationSink Sink { get; }

        internal SpyLog Log { get; }

        /// <summary>
        /// Create the rig: the flags and the two address rows ride an
        /// in-memory settings store (the homeserver points at the loopback
        /// stub, the room id rides the stub's own), the access token rides
        /// a stubbed secret store (absent when <paramref name="withToken"/>
        /// is false) and the transport is a pinned HttpClient whose timeout
        /// is the test's own, so a hanging endpoint never holds a test for
        /// the real 10 s.
        /// </summary>
        internal static async Task<MatrixRig> CreateAsync(
            bool fillFlag = true,
            bool cancelsFlag = false,
            bool rejectsFlag = false,
            bool withToken = true,
            MatrixMissingPiece missingPiece = (MatrixMissingPiece)(-1),
            TimeSpan? timeout = null)
        {
            var api = LoopbackHttpApi.Start();
            var settings = new TestSettings();
            var log = new SpyLog();

            await settings.SetAsync(MatrixSink.SettingsSection, MatrixSink.FillsEnabledKey, fillFlag);
            await settings.SetAsync(MatrixSink.SettingsSection, MatrixSink.CancelsEnabledKey, cancelsFlag);
            await settings.SetAsync(MatrixSink.SettingsSection, MatrixSink.RejectsEnabledKey, rejectsFlag);
            if (missingPiece != MatrixMissingPiece.HomeserverUrl)
            {
                await settings.SetAsync(
                    MatrixSink.SettingsSection,
                    MatrixSink.HomeserverUrlKey,
                    api.BaseUrl.ToString());
            }

            if (missingPiece != MatrixMissingPiece.RoomId)
            {
                await settings.SetAsync(MatrixSink.SettingsSection, MatrixSink.RoomIdKey, StubRoomId);
            }

            var secrets = Substitute.For<ISecretStore>();
            if (withToken)
            {
                secrets
                    .GetAsync(MatrixSink.AccessTokenSecretKey, Arg.Any<CancellationToken>())
                    .Returns(StubAccessToken);
            }

            var transport = new HttpClient { Timeout = timeout ?? TimeSpan.FromSeconds(5) };
            var sink = new MatrixSink(
                settings,
                secrets,
                transport,
                new StubLocalization(),
                new SpyLogger<MatrixSink>(log));

            return new MatrixRig(api, sink, log);
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

    /// <summary>The logger spy's entry store (the sink's own degrade lane's probe).</summary>
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
