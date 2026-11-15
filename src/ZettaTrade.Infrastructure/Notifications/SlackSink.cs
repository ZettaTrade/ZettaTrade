namespace ZettaTrade.Infrastructure.Notifications;

using System;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Livery.Localization;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// The Slack trade-notification sink (T-261): one POST per journaled
/// trade outcome to the user's own Slack incoming webhook — the body
/// carries the composed trade line as the message text. The whole
/// pipeline is BEST-EFFORT by contract: the per-kind opt-in flags and the
/// webhook URL are read per send (the flag off or a missing URL is the
/// quiet no-sink state — a debug line, never a warning), a failed send
/// degrades into one logged warning through this sink's own lane, and
/// nothing ever retries, blocks a caller or throws outward. The incoming
/// webhook URL embeds its own secret material — it rides
/// <see cref="ISecretStore"/> only and never appears in a failure report
/// (a fault names the status code only).
/// </summary>
public sealed class SlackSink : INotificationSink
{
    #region Constants

    /// <summary>Settings section holding the notification preferences (shared with the sink fleet).</summary>
    public const string SettingsSection = "Notifications";

    /// <summary>Settings key of the fills opt-in (a bool, default off).</summary>
    public const string FillsEnabledKey = "SlackFills";

    /// <summary>Settings key of the cancellations opt-in (a bool, default off).</summary>
    public const string CancelsEnabledKey = "SlackCancels";

    /// <summary>Settings key of the rejections opt-in (a bool, default off).</summary>
    public const string RejectsEnabledKey = "SlackRejects";

    /// <summary>ISecretStore key of the user's own Slack incoming webhook URL (the URL embeds the webhook's secret material).</summary>
    public const string WebhookUrlSecretKey = "slack.webhook-url";

    /// <summary>The opt-in state until a stored value exists: off (nothing sends unconfigured).</summary>
    public const bool DefaultEnabled = false;

    /// <summary>The named HttpClient the sends ride (registered by the module's composition).</summary>
    public const string HttpClientName = "ZettaTradeSlack";

    #endregion Constants

    #region Private Fields

    private readonly ISettingsService _settings;
    private readonly ISecretStore _secrets;
    private readonly HttpClient _client;
    private readonly ILocalizationManager _localization;
    private readonly ILogger<SlackSink> _logger;

    #endregion Private Fields

    #region Constructor

    /// <summary>Create the sink over the settings, the secret store, its own bounded client and the message catalog.</summary>
    /// <param name="settings">The reactive settings service the opt-in flags ride.</param>
    /// <param name="secrets">The secret store the webhook URL reads from (never written by this sink).</param>
    /// <param name="client">The bounded HttpClient the texts ride (the URL is absolute per send).</param>
    /// <param name="localization">The catalog the trade line composes from.</param>
    /// <param name="logger">The sink's own degrade lane.</param>
    public SlackSink(
        ISettingsService settings,
        ISecretStore secrets,
        HttpClient client,
        ILocalizationManager localization,
        ILogger<SlackSink> logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    #endregion Constructor

    #region Sink

    /// <summary>
    /// Handle one mapped trade event, best-effort (the sink contract): the
    /// kind's opt-in flag gates first (off — quiet no-sink), then the
    /// webhook URL (missing — the unconfigured state, quiet no-sink), then
    /// the trade line composes from the event's facts and the text rides
    /// out. The returned task NEVER faults: every fault of the chain
    /// degrades into this sink's own warning lane once.
    /// </summary>
    /// <param name="tradeEvent">The mapped trade event (the bridge's own vocabulary).</param>
    /// <param name="cancellationToken">Cooperative cancellation on top of the client's bounded timeout.</param>
    public async Task HandleAsync(TradeNotificationEvent tradeEvent, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!await ReadEnabledFlagAsync(tradeEvent.Kind, cancellationToken).ConfigureAwait(false))
            {
                _logger.LogDebug("The Slack sink is not opted in for {Kind}: no send", tradeEvent.Kind);
                return;
            }

            // The webhook URL is read per send (a rotated URL applies
            // without a restart). Absence is the unconfigured state —
            // quiet; a corrupt stored value throws here and the degrade
            // lane below warns.
            var url = await _secrets.GetAsync(WebhookUrlSecretKey, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(url))
            {
                _logger.LogDebug("The Slack sink holds no webhook URL: no send for {Kind}", tradeEvent.Kind);
                return;
            }

            var line = TradeMessageComposer.Compose(tradeEvent, _localization);

            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(url, UriKind.Absolute))
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new WebhookBody(line), JsonOptions),
                    Encoding.UTF8,
                    "application/json"),
            };

            using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // The status only: the URL embeds the webhook's secret
                // material and never rides a failure report.
                throw new InvalidOperationException(
                    $"The Slack webhook answered HTTP {(int)response.StatusCode}.");
            }

            _logger.LogInformation("The Slack trade notification delivered ({Kind})", tradeEvent.Kind);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "The Slack trade notification failed and was degraded away ({Kind})", tradeEvent.Kind);
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
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "The Slack sink has no opt-in flag for the kind."),
    };

    #endregion Sink

    /// <summary>The JSON options the bodies speak; the field names are pinned by their own attributes.</summary>
    private static readonly JsonSerializerOptions JsonOptions = new();

    /// <summary>One webhook body exactly as the Slack incoming webhook API defines it.</summary>
    private sealed record WebhookBody(
        [property: JsonPropertyName("text")] string Text);
}
