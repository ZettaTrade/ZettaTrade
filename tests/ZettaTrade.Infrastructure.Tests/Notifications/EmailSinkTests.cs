namespace ZettaTrade.Infrastructure.Tests.Notifications;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
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
/// The email sink's best-effort facts (T-262, the T-261 sink contract on
/// the mailbox transport), exercised against the loopback SMTP stub: a
/// configured, opted-in send composes INSIDE the sink from the mapped
/// event's facts and delivers one plain-text mail exactly once — the
/// envelope riding the stored addresses, the composed trade line as the
/// subject and the body, the credentials riding the AUTH exchange only;
/// the sends are throttled (one delivered mail claims the floor, a too-
/// soon send is the quiet throttled skip, a failed send never claims the
/// floor); the quiet no-sink states (the kind's opt-in off, a missing
/// server row or credential half) dispatch nothing and log nothing loud;
/// the failure matrix (rejected credentials, an unreachable server, an
/// unparsable port) degrades into one logged warning, never a throw,
/// never a retry — and no failure report ever carries the credentials.
/// </summary>
public sealed class EmailSinkTests
{
    #region Constants

    /// <summary>The stub catalog's composed fill line (the shared composer's own golden).</summary>
    private const string ComposedFillText = "[Notif.TradeFill|[Order.Buy];0.50000000;BTC/USDT;50000.00000000;bybit;[Trading.Lane.Paper]]";

    /// <summary>The envelope sender the delivery goldens ride (sample-shaped, never a real mailbox).</summary>
    private const string StubFrom = "sender@example.org";

    /// <summary>The envelope recipient the delivery goldens ride (sample-shaped, never a real mailbox).</summary>
    private const string StubTo = "recipient@example.org";

    #endregion Constants

    #region Tests: the mailbox golden

    /// <summary>
    /// A configured, opted-in fill delivers exactly one plain-text mail:
    /// the envelope rides the stored addresses, the AUTH exchange rides
    /// the store's credentials, the composed trade line is both the
    /// subject and the body.
    /// </summary>
    [Fact]
    public async Task AConfiguredFill_DeliversThePlainTextMail()
    {
        await using var rig = await EmailRig.CreateAsync();
        var authed = rig.Server.WaitForAuthAsync();
        var delivered = rig.Server.WaitForMessageAsync(0);

        var send = BoundedSend(rig, FillEvent());

        await send.Should().NotThrowAsync();

        (await BoundedAsync(authed, rig.Server, rig.Log, "the AUTH exchange")).Should().Be(
            LoopbackSmtpServer.GoodUsername,
            "the AUTH exchange carries the store's username (the credentials' only ride)");
        var message = await BoundedAsync(delivered, rig.Server, rig.Log, "the delivery");
        message.From.Should().Contain($"<{StubFrom}>", "the envelope's sender is the stored address");
        message.To.Should().Contain($"<{StubTo}>", "the envelope's recipient is the stored address");
        message.DataPayload.Should().Contain(
            $"Subject: {ComposedFillText}",
            "the composed trade line is the mail's subject");
        message.DataPayload.Should().Contain(
            ComposedFillText,
            "the composed trade line is the mail's plain-text body");

        rig.Log.Entries.Should().NotContain(
            entry => entry.Level == LogLevel.Warning,
            "a delivered send stays quiet above the information lane");
    }

    /// <summary>Every kind composes its own line into the mail — the vocabulary's three outcomes each get their own template.</summary>
    [Fact]
    public async Task EveryKind_ComposesItsOwnLine()
    {
        // The zero floor: the shipped 60 s would silently throttle the
        // second and third back-to-back sends — the floor facts live in
        // their own tests, this one pins the three templates.
        await using var rig = await EmailRig.CreateAsync(cancelsFlag: true, rejectsFlag: true, sendFloor: TimeSpan.Zero);

        var fillDelivered = rig.Server.WaitForMessageAsync(0);
        await BoundedSend(rig, FillEvent()).Should().NotThrowAsync();
        (await BoundedAsync(fillDelivered, rig.Server, rig.Log, "the fill delivery")).DataPayload.Should().Contain("[Notif.TradeFill|");

        var cancelDelivered = rig.Server.WaitForMessageAsync(1);
        await BoundedSend(rig, TradeEvent(TradeNotificationKind.Cancel, "BTC/USDT")).Should().NotThrowAsync();
        (await BoundedAsync(cancelDelivered, rig.Server, rig.Log, "the cancel delivery")).DataPayload.Should().Contain("[Notif.TradeCancelOn|");

        var rejectDelivered = rig.Server.WaitForMessageAsync(2);
        await BoundedSend(rig, TradeEvent(TradeNotificationKind.Reject, "BTC/USDT", reason: "the venue refused")).Should().NotThrowAsync();
        (await BoundedAsync(rejectDelivered, rig.Server, rig.Log, "the reject delivery")).DataPayload.Should().Contain("[Notif.TradeReject|");
    }

    /// <summary>The shipped STARTTLS default is ON: the credentials must not ride the wire in the clear unconfigured.</summary>
    [Fact]
    public void TheShippedTlsDefault_IsOn()
    {
        EmailSink.DefaultEnableSsl.Should().BeTrue(
            "the credentials must not ride the wire in the clear until the user picks otherwise");
    }

    #endregion Tests: the mailbox golden

    #region Tests: the throttle floor

    /// <summary>
    /// A send arriving inside the floor after a delivered mail is the
    /// quiet throttled skip: no second SMTP session opens, nothing warns
    /// (the mailbox lane's own rate limit — never a retry, never a fault).
    /// </summary>
    [Fact]
    public async Task ASecondSendInsideTheFloor_IsTheQuietThrottledSkip()
    {
        await using var rig = await EmailRig.CreateAsync(sendFloor: TimeSpan.FromSeconds(60));
        var firstDelivered = rig.Server.WaitForMessageAsync(0);
        await BoundedSend(rig, FillEvent()).Should().NotThrowAsync();
        await BoundedAsync(firstDelivered, rig.Server, rig.Log, "the first delivery");

        var send = BoundedSend(rig, FillEvent());

        await send.Should().NotThrowAsync();
        await Task.Delay(300);
        rig.Server.SessionCount.Should().Be(1, "the too-soon send never opened a second session");
        rig.Log.Entries.Should().NotContain(
            entry => entry.Level == LogLevel.Warning,
            "a throttled send is the quiet skip, not a fault");
    }

    /// <summary>A failed send never claims the floor: the next send delivers immediately (the floor belongs to deliveries).</summary>
    [Fact]
    public async Task AFailedSend_NeverClaimsTheFloor()
    {
        await using var rejecting = await EmailRig.CreateAsync(
            sendFloor: TimeSpan.FromSeconds(60),
            rejectCredentials: true);

        var rejectedSend = BoundedSend(rejecting, FillEvent());
        await rejectedSend.Should().NotThrowAsync();
        rejecting.Log.Entries.Should().Contain(
            entry => entry.Level == LogLevel.Warning,
            "the rejected send degraded loudly once");

        // The SAME sink instance sends again against a good server: if the
        // failed send had claimed the 60 s floor, this send would be the
        // quiet throttled skip instead of a delivery.
        await using var good = await EmailRig.CreateAsync();
        await rejecting.RepointServerAsync(good.Server);
        var redelivered = good.Server.WaitForMessageAsync(0);

        var nextSend = BoundedSend(rejecting, FillEvent());
        await nextSend.Should().NotThrowAsync();

        (await BoundedAsync(redelivered, good.Server, rejecting.Log, "the post-failure delivery")).DataPayload.Should().Contain(
            "[Notif.TradeFill|",
            "the failed send left the floor unclaimed, so the next send delivers immediately");
        rejecting.Log.Entries.Count(entry => entry.Level == LogLevel.Warning).Should().Be(1);
    }

    #endregion Tests: the throttle floor

    #region Tests: the quiet no-sink states

    /// <summary>The kind's opt-in off means quietly no sink: nothing reaches the stub, nothing warns.</summary>
    [Fact]
    public async Task TheFlagOff_QuietlySendsNothing()
    {
        await using var rig = await EmailRig.CreateAsync(fillFlag: false);

        await rig.ShouldSendNothingAsync("the fills flag is off");
    }

    /// <summary>A missing server row (host, port, envelope address) is the unconfigured state: quietly no send.</summary>
    [Theory]
    [InlineData(EmailMissingPiece.Host)]
    [InlineData(EmailMissingPiece.Port)]
    [InlineData(EmailMissingPiece.From)]
    [InlineData(EmailMissingPiece.To)]
    public async Task AMissingServerRow_QuietlySendsNothing(EmailMissingPiece piece)
    {
        await using var rig = await EmailRig.CreateAsync(missingPiece: piece);

        await rig.ShouldSendNothingAsync($"the {piece} row is not stored");
    }

    /// <summary>A missing credential half is the unconfigured state: quietly no send (the mail would bounce at AUTH anyway).</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AMissingCredentialHalf_QuietlySendsNothing(bool missingPassword)
    {
        await using var rig = await EmailRig.CreateAsync(
            missingPassword: missingPassword,
            withUsername: missingPassword);

        await rig.ShouldSendNothingAsync(missingPassword ? "no password is stored" : "no username is stored");
    }

    #endregion Tests: the quiet no-sink states

    #region Tests: the best-effort failure matrix

    /// <summary>Rejected credentials degrade into one logged warning: the send completes, no throw, no retry.</summary>
    [Fact]
    public async Task RejectedCredentials_DegradeToOneWarning_NeverThrows()
    {
        await using var rig = await EmailRig.CreateAsync(rejectCredentials: true);

        var send = BoundedSend(rig, FillEvent());

        await send.Should().NotThrowAsync("a failed send is the sink's degrade lane's business, not the caller's exception");
        await Task.Delay(200);
        rig.Log.Entries.Should().ContainSingle(
            entry => entry.Level == LogLevel.Warning,
            "a failed send logs its warning exactly once (no retries)");
    }

    /// <summary>No failure report ever carries the credentials: the AUTH material never rides a log line.</summary>
    [Fact]
    public async Task NoFailureReport_CarriesTheCredentials()
    {
        await using var rig = await EmailRig.CreateAsync(rejectCredentials: true);

        await BoundedSend(rig, FillEvent()).Should().NotThrowAsync();
        await Task.Delay(200);

        rig.Log.Entries.Should().NotContain(
            entry => entry.Message.Contains(LoopbackSmtpServer.GoodUsername, StringComparison.Ordinal),
            "the username never rides a log line");
        rig.Log.Entries.Should().NotContain(
            entry => entry.Message.Contains(LoopbackSmtpServer.GoodPassword, StringComparison.Ordinal),
            "the password never rides a log line");
        rig.Log.Entries.Should().NotContain(
            entry => entry.Exception != null &&
                     entry.Exception.Message.Contains(LoopbackSmtpServer.GoodPassword, StringComparison.Ordinal),
            "the password never rides a fault report");
    }

    /// <summary>An unreachable server degrades into one warning with its fault attached: the send settles bounded, the caller never hangs.</summary>
    [Fact]
    public async Task AnUnreachableServer_DegradesIntoOneWarning()
    {
        await using var rig = await EmailRig.CreateAsync(closedPort: true);

        var send = BoundedSend(rig, FillEvent());

        await send.Should().NotThrowAsync("the unreachable server is the sink's degrade lane's business");
        await Task.Delay(300);
        rig.Log.Entries.Should().ContainSingle(
            entry => entry.Level == LogLevel.Warning && entry.Exception != null,
            "the connect failure lands in the degrade lane with its fault attached");
    }

    /// <summary>
    /// A stored port that does not parse to a real port degrades loudly
    /// into the warning lane: never a substituted default — the
    /// invalid-value canon (the send would go nowhere honest).
    /// </summary>
    [Theory]
    [InlineData("not-a-port")]
    [InlineData("0")]
    [InlineData("70000")]
    public async Task AnUnparsablePort_DegradesToOneWarning_AndSendsNothing(string portText)
    {
        await using var rig = await EmailRig.CreateAsync(port: portText);

        var send = BoundedSend(rig, FillEvent());

        await send.Should().NotThrowAsync("the sink never faults — the unparsable port degrades");
        await Task.Delay(200);
        rig.Server.SessionCount.Should().Be(0, "a garbage port never opens a session");
        rig.Log.Entries.Should().Contain(entry =>
            entry.Level == LogLevel.Warning &&
            entry.Exception is InvalidOperationException &&
            entry.Exception.Message.Contains("does not parse", StringComparison.Ordinal),
            "the port guard's fault rides the degrade lane with its own type attached");
    }

    #endregion Tests: the best-effort failure matrix

    #region Tests: the composition and routing faults

    /// <summary>
    /// A fill whose event carries no price fact is a surface-contract
    /// fault: the composition degrades into the sink's own warning lane
    /// with its fault attached, nothing reaches the stub and nothing
    /// fabricated is ever sent.
    /// </summary>
    [Fact]
    public async Task AFillWithoutPriceFact_DegradesToOneWarning_AndSendsNothing()
    {
        await using var rig = await EmailRig.CreateAsync();

        var send = BoundedSend(rig, FillEvent(price: null));

        await send.Should().NotThrowAsync("the sink never faults — the compose fault degrades");
        await Task.Delay(200);
        rig.Server.SessionCount.Should().Be(0, "a fill without its price fact never fabricates a message");
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
        await using var rig = await EmailRig.CreateAsync();

        var send = BoundedSend(rig, TradeEvent((TradeNotificationKind)999));

        await send.Should().NotThrowAsync("the sink never faults — the routing fault degrades");
        await Task.Delay(200);
        rig.Server.SessionCount.Should().Be(0, "an unknown kind never rides a substituted flag");
        rig.Log.Entries.Should().Contain(entry =>
            entry.Level == LogLevel.Warning &&
            entry.Exception is ArgumentOutOfRangeException,
            "the unknown kind lands in the degrade lane as the argument fault it is");
    }

    #endregion Tests: the composition and routing faults

    #region Helpers

    /// <summary>
    /// Bound one test wait so a stalled SMTP session FAILS the fact —
    /// with the stub's transcript and the sink's own degrade log as the
    /// evidence — instead of wedging the whole run (the SMTP loopback
    /// family's recorded flake shape; two hung runs named the harness).
    /// </summary>
    private static async Task<T> BoundedAsync<T>(Task<T> wait, LoopbackSmtpServer server, SpyLog log, string what)
    {
        try
        {
            return await wait.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException(
                $"{what} never settled within 10 s.\nSMTP transcript:\n{await server.DumpTranscriptAsync()}\nSink log:\n{log.Dump()}",
                ex);
        }
    }

    /// <summary>One bounded send: the sink's HandleAsync rides a 15 s bound (a stalled session fails the fact, never wedges the run).</summary>
    private static Func<Task> BoundedSend(EmailRig rig, TradeNotificationEvent tradeEvent) =>
        () => rig.Sink.HandleAsync(tradeEvent).WaitAsync(TimeSpan.FromSeconds(15));

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

    /// <summary>The email server row a missing-piece fact withholds (public: the theory method signatures carry it).</summary>
    public enum EmailMissingPiece
    {
        Host = 0,
        Port = 1,
        From = 2,
        To = 3,
    }

    /// <summary>One wired email rig: the loopback SMTP stub, the flags/server rows, the credential store and the logger spy.</summary>
    private sealed class EmailRig : IAsyncDisposable
    {
        private EmailRig(LoopbackSmtpServer server, EmailSink sink, SpyLog log, TestSettings settings)
        {
            Server = server;
            Sink = sink;
            Log = log;
            Settings = settings;
        }

        internal LoopbackSmtpServer Server { get; }

        internal EmailSink Sink { get; }

        internal SpyLog Log { get; }

        private TestSettings Settings { get; }

        /// <summary>
        /// Create the rig: the flags and the server rows ride an in-memory
        /// settings store (STARTTLS pinned off — the plain stub never
        /// upgrades), the credentials ride a stubbed secret store, the
        /// throttle floor is the test's own (the shipped 60 s would starve
        /// the multi-send facts) and the SMTP session bound stays the
        /// shipped one. <paramref name="closedPort"/> re-points the rows at
        /// a freshly closed port for the unreachable-server fact.
        /// </summary>
        internal static async Task<EmailRig> CreateAsync(
            bool fillFlag = true,
            bool cancelsFlag = false,
            bool rejectsFlag = false,
            bool rejectCredentials = false,
            bool missingPassword = false,
            bool withUsername = true,
            EmailMissingPiece missingPiece = (EmailMissingPiece)(-1),
            string? port = null,
            bool closedPort = false,
            TimeSpan? sendFloor = null)
        {
            var server = LoopbackSmtpServer.Start(rejectCredentials);
            var settings = new TestSettings();
            var log = new SpyLog();

            await settings.SetAsync(EmailSink.SettingsSection, EmailSink.FillsEnabledKey, fillFlag);
            await settings.SetAsync(EmailSink.SettingsSection, EmailSink.CancelsEnabledKey, cancelsFlag);
            await settings.SetAsync(EmailSink.SettingsSection, EmailSink.RejectsEnabledKey, rejectsFlag);
            await settings.SetAsync(EmailSink.SettingsSection, EmailSink.EnableSslKey, false);
            if (missingPiece != EmailMissingPiece.Host)
            {
                await settings.SetAsync(EmailSink.SettingsSection, EmailSink.HostKey, server.Host);
            }

            if (missingPiece != EmailMissingPiece.Port)
            {
                await settings.SetAsync(
                    EmailSink.SettingsSection,
                    EmailSink.PortKey,
                    port ?? server.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            if (missingPiece != EmailMissingPiece.From)
            {
                await settings.SetAsync(EmailSink.SettingsSection, EmailSink.FromKey, StubFrom);
            }

            if (missingPiece != EmailMissingPiece.To)
            {
                await settings.SetAsync(EmailSink.SettingsSection, EmailSink.ToKey, StubTo);
            }

            var secrets = Substitute.For<ISecretStore>();
            if (withUsername)
            {
                secrets
                    .GetAsync(EmailSink.UsernameSecretKey, Arg.Any<CancellationToken>())
                    .Returns(LoopbackSmtpServer.GoodUsername);
            }

            if (!missingPassword)
            {
                secrets
                    .GetAsync(EmailSink.PasswordSecretKey, Arg.Any<CancellationToken>())
                    .Returns(LoopbackSmtpServer.GoodPassword);
            }

            var sink = new EmailSink(
                settings,
                secrets,
                new StubLocalization(),
                new SpyLogger<EmailSink>(log),
                sendFloor);

            if (closedPort)
            {
                await settings.SetAsync(EmailSink.SettingsSection, EmailSink.HostKey, "127.0.0.1");
                await settings.SetAsync(
                    EmailSink.SettingsSection,
                    EmailSink.PortKey,
                    FindClosedPort().ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            return new EmailRig(server, sink, log, settings);
        }

        /// <summary>
        /// A port nothing listens on: bind an ephemeral listener, record its
        /// port, free it — the recorded port answers nothing (the tiny
        /// reclaim race is acceptable for a test; a stray listener surfaces
        /// immediately as a delivered send instead of the fact's warning).
        /// </summary>
        private static int FindClosedPort()
        {
            var finder = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            finder.Start();
            var port = ((System.Net.IPEndPoint)finder.LocalEndpoint).Port;
            finder.Stop();
            return port;
        }

        /// <summary>Re-point the stored server rows at another stub (the rows are read per send, so the next send rides them).</summary>
        internal async Task RepointServerAsync(LoopbackSmtpServer server)
        {
            await Settings.SetAsync(EmailSink.SettingsSection, EmailSink.HostKey, server.Host);
            await Settings.SetAsync(
                EmailSink.SettingsSection,
                EmailSink.PortKey,
                server.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// Assert the sink stayed quiet: the awaited send already settled
        /// (the gate returned before any dispatch), no session opened and
        /// no warning was logged. The send rides its bound (a stalled
        /// session fails the fact, never wedges the run).
        /// </summary>
        internal async Task ShouldSendNothingAsync(string because)
        {
            var send = BoundedSend(this, FillEvent());
            await send.Should().NotThrowAsync();
            await Task.Delay(300);

            Server.SessionCount.Should().Be(0, $"no session reached the stub ({because})");
            Log.Entries.Should().NotContain(entry => entry.Level == LogLevel.Warning);
        }

        public async ValueTask DisposeAsync()
        {
            Server.Dispose();
            await Task.CompletedTask;
        }
    }

    /// <summary>The logger spy's entry store (the sink's own degrade lane's probe).</summary>
    private sealed class SpyLog
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        /// <summary>The entries as one readable block (the bounded waits' evidence).</summary>
        public string Dump() => string.Join(
            Environment.NewLine,
            Entries.Select(entry =>
                $"{entry.Level}: {entry.Message}"
                + (entry.Exception is null ? string.Empty : $" :: {entry.Exception.GetType().Name} {entry.Exception.Message}")));
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
