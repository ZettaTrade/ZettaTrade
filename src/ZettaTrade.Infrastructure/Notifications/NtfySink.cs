namespace ZettaTrade.Infrastructure.Notifications;

using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Livery.Localization;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// The ntfy trade-notification sink (T-261): one POST per journaled
/// trade outcome to the user's own ntfy topic — the phone-push lane. The
/// topic URL is PUBLIC by design (the topic name is the address, ntfy.sh
/// topics are open), so it rides the settings file rather than the secret
/// store — the honest posture the spec keeps ("a public URL by design —
/// a secret only when the channel demands one"). The whole pipeline is
/// BEST-EFFORT by contract: the per-kind opt-in flags and the topic URL
/// are read per send (the flag off or an empty URL is the quiet no-sink
/// state — a debug line, never a warning), a failed send degrades into
/// one logged warning through this sink's own lane, and nothing ever
/// retries, blocks a caller or throws outward.
/// </summary>
public sealed class NtfySink : INotificationSink
{
    #region Constants

    /// <summary>Settings section holding the notification preferences (shared with the sink fleet).</summary>
    public const string SettingsSection = "Notifications";

    /// <summary>Settings key of the fills opt-in (a bool, default off).</summary>
    public const string FillsEnabledKey = "NtfyFills";

    /// <summary>Settings key of the cancellations opt-in (a bool, default off).</summary>
    public const string CancelsEnabledKey = "NtfyCancels";

    /// <summary>Settings key of the rejections opt-in (a bool, default off).</summary>
    public const string RejectsEnabledKey = "NtfyRejects";

    /// <summary>Settings key of the topic URL (a string; the full https topic address, public by design).</summary>
    public const string TopicUrlKey = "NtfyUrl";

    /// <summary>The opt-in state until a stored value exists: off (nothing sends unconfigured).</summary>
    public const bool DefaultEnabled = false;

    /// <summary>The topic URL until a stored value exists: empty (nothing sends unconfigured).</summary>
    public const string DefaultTopicUrl = "";

    /// <summary>The named HttpClient the sends ride (registered by the module's composition).</summary>
    public const string HttpClientName = "ZettaTradeNtfy";

    #endregion Constants

    #region Private Fields

    private readonly ISettingsService _settings;
    private readonly HttpClient _client;
    private readonly ILocalizationManager _localization;
    private readonly ILogger<NtfySink> _logger;

    #endregion Private Fields

    #region Constructor

    /// <summary>Create the sink over the settings, its own bounded client and the message catalog.</summary>
    /// <param name="settings">The reactive settings service the opt-in flags and the topic URL ride.</param>
    /// <param name="client">The bounded HttpClient the pushes ride (the URL is absolute per send).</param>
    /// <param name="localization">The catalog the trade line composes from.</param>
    /// <param name="logger">The sink's own degrade lane.</param>
    public NtfySink(
        ISettingsService settings,
        HttpClient client,
        ILocalizationManager localization,
        ILogger<NtfySink> logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    #endregion Constructor

    #region Sink

    /// <summary>
    /// Handle one mapped trade event, best-effort (the sink contract): the
    /// kind's opt-in flag gates first (off — quiet no-sink), then the topic
    /// URL (missing — the unconfigured state, quiet no-sink), then the
    /// trade line composes from the event's facts and the plain-text push
    /// rides out. The returned task NEVER faults: every fault of the chain
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
                _logger.LogDebug("The ntfy sink is not opted in for {Kind}: no send", tradeEvent.Kind);
                return;
            }

            var url = await _settings
                .GetAsync<string>(SettingsSection, TopicUrlKey, DefaultTopicUrl)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(url))
            {
                _logger.LogDebug("The ntfy sink holds no topic URL: no send for {Kind}", tradeEvent.Kind);
                return;
            }

            var line = TradeMessageComposer.Compose(tradeEvent, _localization);

            // The ntfy publish contract: POST the message text to the
            // topic URL itself (the topic name is the address), the body
            // plain text, the content type honest.
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(url, UriKind.Absolute))
            {
                Content = new StringContent(line, Encoding.UTF8, "text/plain"),
            };

            using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"The ntfy push answered HTTP {(int)response.StatusCode}.");
            }

            _logger.LogInformation("The ntfy trade notification delivered ({Kind})", tradeEvent.Kind);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "The ntfy trade notification failed and was degraded away ({Kind})", tradeEvent.Kind);
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
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "The ntfy sink has no opt-in flag for the kind."),
    };

    #endregion Sink
}
