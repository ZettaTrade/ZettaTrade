namespace ZettaTrade.Infrastructure.Notifications;

using System;
using System.Threading;
using System.Threading.Tasks;
using Livery.Localization;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// The Telegram trade-notification sink (T-041, the T-260 sink contract):
/// it implements <see cref="INotificationSink"/>, so it receives one
/// mapped trade event per journaled trade outcome and composes its own
/// message from the event's facts before pushing it to the user's own
/// Telegram bot chat. The whole pipeline is BEST-EFFORT by contract: the
/// per-kind opt-in flag, the chat id and the bot token are read per send
/// (the flag off, an empty chat id or a missing token is the quiet no-sink
/// state — a debug line, never a warning), the composition and the send
/// degrade into one logged warning through this service's own lane, and
/// nothing ever retries, blocks a caller or throws outward — a
/// notification fault stays invisible to the trading flow exactly like a
/// failed beep (the T-220 degrade shape).
/// <para>
/// The bot token rides <see cref="ISecretStore"/> only (never a
/// command-line argument, never a settings file); this service reads it
/// per send and never writes, moves or logs it — a corrupt stored value
/// surfaces as the degrade lane's warning and stays preserved on disk.
/// </para>
/// </summary>
public sealed class TelegramNotifierService : INotificationSink
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
    private readonly ILocalizationManager _localization;
    private readonly ILogger<TelegramNotifierService> _logger;

    #endregion Private Fields

    #region Constructor

    /// <summary>Create the sink over the settings, the secret store, the Bot API client and the message catalog.</summary>
    /// <param name="settings">The reactive settings service the opt-in flags and the chat id ride.</param>
    /// <param name="secrets">The secret store the bot token reads from (never written by this service).</param>
    /// <param name="bot">The Bot API transport.</param>
    /// <param name="localization">The catalog the message lines compose from (the composition lives inside the sink now).</param>
    /// <param name="logger">The sink's own degrade lane.</param>
    public TelegramNotifierService(
        ISettingsService settings,
        ISecretStore secrets,
        TelegramBotClient bot,
        ILocalizationManager localization,
        ILogger<TelegramNotifierService> logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        _bot = bot ?? throw new ArgumentNullException(nameof(bot));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    #endregion Constructor

    #region Sink

    /// <summary>
    /// Handle one mapped trade event, best-effort (the sink contract): the
    /// kind's opt-in flag gates first (off — quiet no-sink), then the chat
    /// id and the bot token (missing — the unconfigured state, quiet
    /// no-sink), then the message composes from the event's facts and the
    /// POST rides out. The returned task NEVER faults: every fault of the
    /// whole chain — a read fault, a composition fault, a transport fault —
    /// degrades into this service's own warning lane once, so a caller
    /// that dispatches the sink off its critical path carries no
    /// unobserved fault and the trading flow never notices a failed push.
    /// </summary>
    /// <param name="tradeEvent">The mapped trade event (the bridge's own vocabulary).</param>
    /// <param name="cancellationToken">Cooperative cancellation on top of the transport's bounded timeout.</param>
    /// <inheritdoc cref="INotificationSink.HandleAsync"/>
    public async Task HandleAsync(TradeNotificationEvent tradeEvent, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!await ReadEnabledFlagAsync(tradeEvent.Kind, cancellationToken).ConfigureAwait(false))
            {
                _logger.LogDebug("The Telegram sink is not opted in for {Kind}: no send", tradeEvent.Kind);
                return;
            }

            var chatId = await _settings
                .GetAsync<string>(SettingsSection, ChatIdKey, DefaultChatId)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(chatId))
            {
                _logger.LogDebug("The Telegram sink holds no chat id: no send for {Kind}", tradeEvent.Kind);
                return;
            }

            // The token is read per send (a rotated token applies without a
            // restart). Absence is the unconfigured state — quiet; a corrupt
            // stored value throws here and the degrade lane below warns.
            var token = await _secrets.GetAsync(BotTokenSecretKey, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(token))
            {
                _logger.LogDebug("The Telegram sink holds no bot token: no send for {Kind}", tradeEvent.Kind);
                return;
            }

            var message = TradeMessageComposer.Compose(tradeEvent, _localization);

            await _bot.SendMessageAsync(token, chatId, message, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("The Telegram trade notification delivered ({Kind})", tradeEvent.Kind);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "The Telegram trade notification failed and was degraded away ({Kind})", tradeEvent.Kind);
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
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "The Telegram sink has no opt-in flag for the kind."),
    };

    #endregion Sink
}
