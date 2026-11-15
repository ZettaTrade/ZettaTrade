namespace ZettaTrade.Infrastructure.Notifications;

using System;
using System.Globalization;
using System.Net.Mail;
using System.Threading;
using System.Threading.Tasks;
using Livery.Localization;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// The email trade-notification sink (T-262): one plain-text mail per
/// journaled trade outcome through the user's own SMTP mailbox. The
/// posture follows the spec's split: the SMTP credentials (the username
/// and the password) are SECRET material — they ride
/// <see cref="ISecretStore"/> only and never appear in a failure report —
/// while the server host, port, envelope addresses and the TLS pick are
/// addresses and ride the settings file. Sends are THROTTLED: one
/// delivered mail claims the floor (the shipped 60 s minimum between
/// deliveries), a send arriving sooner is the quiet throttled skip —
/// the mailbox-lane's own rate limit, never a retry. The whole pipeline
/// is BEST-EFFORT by contract: the per-kind opt-in flags and every row
/// are read per send (a flag off or a missing piece is the quiet
/// no-sink state — a debug line, never a warning; an unparsable port is
/// a real degrade into one logged warning, never a substituted
/// default), a failed send degrades into one logged warning through
/// this sink's own lane, and nothing ever retries, blocks a caller or
/// throws outward.
/// </summary>
public sealed class EmailSink : INotificationSink
{
    #region Constants

    /// <summary>Settings section holding the notification preferences (shared with the sink fleet).</summary>
    public const string SettingsSection = "Notifications";

    /// <summary>Settings key of the fills opt-in (a bool, default off).</summary>
    public const string FillsEnabledKey = "EmailFills";

    /// <summary>Settings key of the cancellations opt-in (a bool, default off).</summary>
    public const string CancelsEnabledKey = "EmailCancels";

    /// <summary>Settings key of the rejections opt-in (a bool, default off).</summary>
    public const string RejectsEnabledKey = "EmailRejects";

    /// <summary>Settings key of the SMTP server's host (a string; public by design — an address).</summary>
    public const string HostKey = "SmtpHost";

    /// <summary>Settings key of the SMTP server's port (a string; parsed invariant per send — an unparsable value degrades, never a substituted default).</summary>
    public const string PortKey = "SmtpPort";

    /// <summary>Settings key of the envelope sender address (a string; public by design — an address).</summary>
    public const string FromKey = "SmtpFrom";

    /// <summary>Settings key of the recipient address the trade mail lands in (a string; public by design — an address).</summary>
    public const string ToKey = "SmtpTo";

    /// <summary>Settings key of the STARTTLS pick (a bool, default ON — credentials never ride the wire in the clear unconfigured).</summary>
    public const string EnableSslKey = "SmtpEnableSsl";

    /// <summary>ISecretStore key of the SMTP login (the credential pair's first half).</summary>
    public const string UsernameSecretKey = "smtp.username";

    /// <summary>ISecretStore key of the SMTP password (or the provider's app password; the credential pair's second half).</summary>
    public const string PasswordSecretKey = "smtp.password";

    /// <summary>The opt-in state until a stored value exists: off (nothing sends unconfigured).</summary>
    public const bool DefaultEnabled = false;

    /// <summary>The STARTTLS state until a stored value exists: on (the credentials must not ride the wire in the clear).</summary>
    public const bool DefaultEnableSsl = true;

    /// <summary>The shipped throttle floor: the minimum between two delivered mails.</summary>
    public static readonly TimeSpan DefaultSendFloor = TimeSpan.FromSeconds(60);

    /// <summary>The bounded SMTP session: an unreachable server never holds a send past this bound.</summary>
    public const int SendTimeoutMilliseconds = 10_000;

    #endregion Constants

    #region Private Fields

    private readonly ISettingsService _settings;
    private readonly ISecretStore _secrets;
    private readonly ILocalizationManager _localization;
    private readonly ILogger<EmailSink> _logger;
    private readonly TimeSpan _sendFloor;

    /// <summary>The monotonic tick of the last DELIVERED mail (a failed send never claims the floor); negative means the floor is unclaimed.</summary>
    private long _lastDeliveredTick = -1L;

    #endregion Private Fields

    #region Constructor

    /// <summary>Create the sink over the settings, the secret store and the message catalog.</summary>
    /// <param name="settings">The reactive settings service the opt-in flags and the server rows ride.</param>
    /// <param name="secrets">The secret store the credentials read from (never written by this sink).</param>
    /// <param name="localization">The catalog the trade line composes from.</param>
    /// <param name="logger">The sink's own degrade lane.</param>
    /// <param name="sendFloor">The throttle floor between two delivered mails (the shipped default rides when omitted; the seam exists for the tests' time control).</param>
    public EmailSink(
        ISettingsService settings,
        ISecretStore secrets,
        ILocalizationManager localization,
        ILogger<EmailSink> logger,
        TimeSpan? sendFloor = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _sendFloor = sendFloor ?? DefaultSendFloor;
    }

    #endregion Constructor

    #region Sink

    /// <summary>
    /// Handle one mapped trade event, best-effort (the sink contract): the
    /// kind's opt-in flag gates first (off — quiet no-sink), then the
    /// server rows (host, port, from, to — a missing piece is the
    /// unconfigured state, quiet no-sink; an unparsable port is a real
    /// degrade into the warning lane, never a substituted default), then
    /// the credentials (username and password — a missing half is the
    /// unconfigured state, quiet no-sink), then the throttle floor (a too-
    /// soon send is the quiet throttled skip), and only then the trade
    /// line composes from the event's facts and the plain-text mail rides
    /// out. The returned task NEVER faults: every fault of the chain
    /// degrades into this sink's own warning lane once.
    /// </summary>
    /// <param name="tradeEvent">The mapped trade event (the bridge's own vocabulary).</param>
    /// <param name="cancellationToken">Cooperative cancellation on top of the bounded SMTP session.</param>
    public async Task HandleAsync(TradeNotificationEvent tradeEvent, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!await ReadEnabledFlagAsync(tradeEvent.Kind, cancellationToken).ConfigureAwait(false))
            {
                _logger.LogDebug("The email sink is not opted in for {Kind}: no send", tradeEvent.Kind);
                return;
            }

            // The server rows are read per send (a corrected server or
            // address applies without a restart). Absence is the
            // unconfigured state — quiet; a stored garbage port is a real
            // degrade (the send would go nowhere honest).
            var host = await _settings.GetAsync<string>(SettingsSection, HostKey, string.Empty).ConfigureAwait(false);
            var portText = await _settings.GetAsync<string>(SettingsSection, PortKey, string.Empty).ConfigureAwait(false);
            var from = await _settings.GetAsync<string>(SettingsSection, FromKey, string.Empty).ConfigureAwait(false);
            var to = await _settings.GetAsync<string>(SettingsSection, ToKey, string.Empty).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(host)
                || string.IsNullOrWhiteSpace(portText)
                || string.IsNullOrWhiteSpace(from)
                || string.IsNullOrWhiteSpace(to))
            {
                _logger.LogDebug(
                    "The email sink holds no complete SMTP row (host, port, from, to): no send for {Kind}", tradeEvent.Kind);
                return;
            }

            if (!int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)
                || port < 1
                || port > 65_535)
            {
                throw new InvalidOperationException("The stored SMTP port does not parse to a real port.");
            }

            // The credentials are read per send (a rotated password applies
            // without a restart). A missing half is the unconfigured state
            // — the mail would bounce at the AUTH step anyway.
            var username = await _secrets.GetAsync(UsernameSecretKey, cancellationToken).ConfigureAwait(false);
            var password = await _secrets.GetAsync(PasswordSecretKey, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            {
                _logger.LogDebug("The email sink holds no credentials: no send for {Kind}", tradeEvent.Kind);
                return;
            }

            // The throttle floor: one delivered mail claims it, a send
            // arriving sooner is the quiet skip (the mailbox lane's own
            // rate limit — never a warning, never a retry). The floor is
            // claimed by deliveries only: a failed send leaves the budget
            // intact.
            var now = Environment.TickCount64;
            if (_lastDeliveredTick >= 0 && now - _lastDeliveredTick < (long)_sendFloor.TotalMilliseconds)
            {
                _logger.LogDebug("The email sink is throttled: the floor between two mails is not spent yet ({Kind})", tradeEvent.Kind);
                return;
            }

            var line = TradeMessageComposer.Compose(tradeEvent, _localization);

            // The plain-text transport: one SMTP session per send (the
            // client is disposable and never shared), STARTTLS per the
            // stored pick, the credentials riding the AUTH step only.
            var enableSsl = await _settings
                .GetAsync<bool>(SettingsSection, EnableSslKey, DefaultEnableSsl)
                .ConfigureAwait(false);
            using var client = new SmtpClient(host, port)
            {
                EnableSsl = enableSsl,
                Credentials = new System.Net.NetworkCredential(username, password),
                Timeout = SendTimeoutMilliseconds,
            };
            using var message = new MailMessage(from, to)
            {
                Subject = line,
                Body = line,
                BodyEncoding = System.Text.Encoding.UTF8,
            };

            await client.SendMailAsync(message, cancellationToken).ConfigureAwait(false);

            _lastDeliveredTick = Environment.TickCount64;
            _logger.LogInformation("The email trade notification delivered ({Kind})", tradeEvent.Kind);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "The email trade notification failed and was degraded away ({Kind})", tradeEvent.Kind);
        }
    }

    /// <summary>Read the kind's opt-in flag exactly as the settings hold it right now (no cached reads).</summary>
    private async Task<bool> ReadEnabledFlagAsync(TradeNotificationKind kind, CancellationToken cancellationToken) => kind switch
    {
        TradeNotificationKind.Fill => await _settings
            .GetAsync<bool>(SettingsSection, FillsEnabledKey, DefaultEnabled)
            .ConfigureAwait(false),
        TradeNotificationKind.Cancel => await _settings
            .GetAsync<bool>(SettingsSection, CancelsEnabledKey, DefaultEnabled)
            .ConfigureAwait(false),
        TradeNotificationKind.Reject => await _settings
            .GetAsync<bool>(SettingsSection, RejectsEnabledKey, DefaultEnabled)
            .ConfigureAwait(false),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "The email sink has no opt-in flag for the kind."),
    };

    #endregion Sink
}
