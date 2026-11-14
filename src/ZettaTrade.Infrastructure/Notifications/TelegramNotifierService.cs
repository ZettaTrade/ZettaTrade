namespace ZettaTrade.Infrastructure.Notifications;

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// The Telegram trade-notification sink (T-041): it receives one composed
/// message per journaled trade outcome and pushes it to the user's own
/// Telegram bot chat. The whole pipeline is BEST-EFFORT by contract: the
/// per-kind opt-in flag, the chat id and the bot token are read per send
/// (the flag off, an empty chat id or a missing token is the quiet no-sink
/// state — a debug line, never a warning), a failed send degrades into one
/// logged warning through this service's own lane, and nothing ever retries,
/// blocks a caller or throws outward — a notification fault stays invisible
/// to the trading flow exactly like a failed beep (the T-220 degrade shape).
/// <para>
/// The bot token rides <see cref="ISecretStore"/> only (never a command-line
/// argument, never a settings file); this service reads it per send and
/// never writes, moves or logs it — a corrupt stored value surfaces as the
/// degrade lane's warning and stays preserved on disk.
/// </para>
/// </summary>
public sealed class TelegramNotifierService
{
    #region Constants

    /// <summary>Settings section holding the notification preferences (the Notifications.* keys' home).</summary>
    public const string SettingsSection = "Notifications";

    /// <summary>Settings key of the fills opt-in (a bool, default off).</summary>
    public const string FillsEnabledKey = "TelegramFills";

    /// <summary>Settings key of the cancellations opt-in (a bool, default off).</summary>
    public const string CancelsEnabledKey = "TelegramCancels";

    /// <summary>Settings key of the rejections opt-in (a bool, default off).</summary>
    public const string RejectsEnabledKey = "TelegramRejects";

    /// <summary>Settings key of the target chat id (a string; the user's own bot chat's numeric id).</summary>
    public const string ChatIdKey = "TelegramChatId";

    /// <summary>ISecretStore key of the user's own bot token.</summary>
    public const string BotTokenSecretKey = "telegram.bot-token";

    /// <summary>The opt-in state until a stored value exists: off (nothing sends unconfigured).</summary>
    public const bool DefaultEnabled = false;

    /// <summary>The chat id until a stored value exists: empty (nothing sends unconfigured).</summary>
    public const string DefaultChatId = "";

    #endregion Constants

    #region Private Fields

    private readonly ISettingsService _settings;
    private readonly ISecretStore _secrets;
    private readonly TelegramBotClient _bot;
    private readonly ILogger<TelegramNotifierService> _logger;

    #endregion Private Fields

    #region Constructor

    /// <summary>Create the sink over the settings, the secret store and the Bot API client.</summary>
    /// <param name="settings">The reactive settings service the opt-in flags and the chat id ride.</param>
    /// <param name="secrets">The secret store the bot token reads from (never written by this service).</param>
    /// <param name="bot">The Bot API transport.</param>
    /// <param name="logger">The sink's own degrade lane.</param>
    public TelegramNotifierService(
        ISettingsService settings,
        ISecretStore secrets,
        TelegramBotClient bot,
        ILogger<TelegramNotifierService> logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        _bot = bot ?? throw new ArgumentNullException(nameof(bot));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    #endregion Constructor

    #region Send

    /// <summary>
    /// Push one composed trade message, best-effort: the kind's opt-in flag
    /// gates first (off — quiet no-sink), then the chat id and the bot token
    /// (missing — the unconfigured state, quiet no-sink), then the POST. The
    /// returned task NEVER faults: every fault of the whole chain — a read
    /// fault included — degrades into this service's own warning lane once,
    /// so a caller that dispatches the send off its critical path carries no
    /// unobserved fault and the trading flow never notices a failed push.
    /// </summary>
    /// <param name="message">The composed message text (the composer's job — this service never builds user-facing strings).</param>
    /// <param name="kind">Which trade outcome the message announces (the opt-in flag it reads).</param>
    /// <param name="cancellationToken">Cooperative cancellation on top of the transport's bounded timeout.</param>
    public async Task SendAsync(string message, TelegramTradeKind kind, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!await ReadEnabledFlagAsync(kind, cancellationToken).ConfigureAwait(false))
            {
                _logger.LogDebug("The Telegram sink is not opted in for {Kind}: no send", kind);
                return;
            }

            var chatId = await _settings
                .GetAsync<string>(SettingsSection, ChatIdKey, DefaultChatId)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(chatId))
            {
                _logger.LogDebug("The Telegram sink holds no chat id: no send for {Kind}", kind);
                return;
            }

            // The token is read per send (a rotated token applies without a
            // restart). Absence is the unconfigured state — quiet; a corrupt
            // stored value throws here and the degrade lane below warns.
            var token = await _secrets.GetAsync(BotTokenSecretKey, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(token))
            {
                _logger.LogDebug("The Telegram sink holds no bot token: no send for {Kind}", kind);
                return;
            }

            await _bot.SendMessageAsync(token, chatId, message, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("The Telegram trade notification delivered ({Kind})", kind);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "The Telegram trade notification failed and was degraded away ({Kind})", kind);
        }
    }

    /// <summary>Read the kind's opt-in flag exactly as the settings hold it right now (no cached reads).</summary>
    private async Task<bool> ReadEnabledFlagAsync(TelegramTradeKind kind, CancellationToken cancellationToken) => kind switch
    {
        TelegramTradeKind.Fill => await _settings
            .GetAsync<bool>(SettingsSection, FillsEnabledKey, DefaultEnabled)
            .ConfigureAwait(false),
        TelegramTradeKind.Cancel => await _settings
            .GetAsync<bool>(SettingsSection, CancelsEnabledKey, DefaultEnabled)
            .ConfigureAwait(false),
        TelegramTradeKind.Reject => await _settings
            .GetAsync<bool>(SettingsSection, RejectsEnabledKey, DefaultEnabled)
            .ConfigureAwait(false),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "The Telegram sink has no opt-in flag for the kind."),
    };

    #endregion Send
}
