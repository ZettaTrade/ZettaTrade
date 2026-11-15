namespace ZettaTrade.Infrastructure.Notifications;

using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Livery.Localization;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// The Matrix trade-notification sink (T-262): one transactional PUT per
/// journaled trade outcome to the user's own Matrix room through their
/// homeserver's client API — the room-push lane. The posture follows the
/// spec's split: the bot's access token is SECRET material (it rides
/// <see cref="ISecretStore"/> only and never appears in a failure report),
/// while the homeserver base URL and the room id are addresses (public by
/// design — they ride the settings file like the ntfy topic URL). The
/// whole pipeline is BEST-EFFORT by contract: the per-kind opt-in flags
/// and every address are read per send (a flag off or a missing piece is
/// the quiet no-sink state — a debug line, never a warning), a failed
/// send degrades into one logged warning through this sink's own lane,
/// and nothing ever retries, blocks a caller or throws outward.
/// </summary>
public sealed class MatrixSink : INotificationSink
{
    #region Constants

    /// <summary>Settings section holding the notification preferences (shared with the sink fleet).</summary>
    public const string SettingsSection = "Notifications";

    /// <summary>Settings key of the fills opt-in (a bool, default off).</summary>
    public const string FillsEnabledKey = "MatrixFills";

    /// <summary>Settings key of the cancellations opt-in (a bool, default off).</summary>
    public const string CancelsEnabledKey = "MatrixCancels";

    /// <summary>Settings key of the rejections opt-in (a bool, default off).</summary>
    public const string RejectsEnabledKey = "MatrixRejects";

    /// <summary>Settings key of the homeserver's client API base URL (a string; public by design — an address).</summary>
    public const string HomeserverUrlKey = "MatrixHomeserverUrl";

    /// <summary>Settings key of the room the bot pushes into (a string; the internal room id, public by design).</summary>
    public const string RoomIdKey = "MatrixRoomId";

    /// <summary>ISecretStore key of the bot's access token (the token is the channel's secret material).</summary>
    public const string AccessTokenSecretKey = "matrix.access-token";

    /// <summary>The opt-in state until a stored value exists: off (nothing sends unconfigured).</summary>
    public const bool DefaultEnabled = false;

    /// <summary>The homeserver URL until a stored value exists: empty (nothing sends unconfigured).</summary>
    public const string DefaultHomeserverUrl = "";

    /// <summary>The room id until a stored value exists: empty (nothing sends unconfigured).</summary>
    public const string DefaultRoomId = "";

    /// <summary>The named HttpClient the sends ride (registered by the module's composition).</summary>
    public const string HttpClientName = "ZettaTradeMatrix";

    #endregion Constants

    #region Private Fields

    private readonly ISettingsService _settings;
    private readonly ISecretStore _secrets;
    private readonly HttpClient _client;
    private readonly ILocalizationManager _localization;
    private readonly ILogger<MatrixSink> _logger;

    #endregion Private Fields

    #region Constructor

    /// <summary>Create the sink over the settings, the secret store, its own bounded client and the message catalog.</summary>
    /// <param name="settings">The reactive settings service the opt-in flags and the addresses ride.</param>
    /// <param name="secrets">The secret store the access token reads from (never written by this sink).</param>
    /// <param name="client">The bounded HttpClient the pushes ride (the URL is absolute per send).</param>
    /// <param name="localization">The catalog the trade line composes from.</param>
    /// <param name="logger">The sink's own degrade lane.</param>
    public MatrixSink(
        ISettingsService settings,
        ISecretStore secrets,
        HttpClient client,
        ILocalizationManager localization,
        ILogger<MatrixSink> logger)
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
    /// addresses (homeserver URL and room id — a missing piece is the
    /// unconfigured state, quiet no-sink), then the access token (missing
    /// — the unconfigured state, quiet no-sink), then the trade line
    /// composes from the event's facts and the transactional PUT rides
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
                _logger.LogDebug("The Matrix sink is not opted in for {Kind}: no send", tradeEvent.Kind);
                return;
            }

            // The addresses are read per send (a corrected homeserver or
            // room applies without a restart). Absence is the unconfigured
            // state — quiet; an unusable stored value throws here and the
            // degrade lane below warns.
            var homeserverUrl = await _settings
                .GetAsync<string>(SettingsSection, HomeserverUrlKey, DefaultHomeserverUrl)
                .ConfigureAwait(false);
            var roomId = await _settings
                .GetAsync<string>(SettingsSection, RoomIdKey, DefaultRoomId)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(homeserverUrl) || string.IsNullOrWhiteSpace(roomId))
            {
                _logger.LogDebug("The Matrix sink holds no homeserver URL or room id: no send for {Kind}", tradeEvent.Kind);
                return;
            }

            // The access token is read per send (a rotated token applies
            // without a restart) and rides the Authorization header only.
            var accessToken = await _secrets.GetAsync(AccessTokenSecretKey, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                _logger.LogDebug("The Matrix sink holds no access token: no send for {Kind}", tradeEvent.Kind);
                return;
            }

            var line = TradeMessageComposer.Compose(tradeEvent, _localization);

            // The Matrix client API's send contract: one transactional PUT
            // per message — the transaction id is the idempotency key, a
            // fresh one per send (the sink never retries, so the txn id
            // never repeats). The room id is URL-escaped (its "!"/":"
            // vocabulary is not URL-native).
            var requestUrl = homeserverUrl.TrimEnd('/')
                + "/_matrix/client/v3/rooms/"
                + Uri.EscapeDataString(roomId)
                + "/send/m.message/"
                + Guid.NewGuid().ToString("N");

            using var request = new HttpRequestMessage(HttpMethod.Put, new Uri(requestUrl, UriKind.Absolute))
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new ClientApiBody(line), JsonOptions),
                    Encoding.UTF8,
                    "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // The status only: the access token is the channel's secret
                // material and never rides a failure report.
                throw new InvalidOperationException(
                    $"The Matrix client API answered HTTP {(int)response.StatusCode}.");
            }

            _logger.LogInformation("The Matrix trade notification delivered ({Kind})", tradeEvent.Kind);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "The Matrix trade notification failed and was degraded away ({Kind})", tradeEvent.Kind);
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
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "The Matrix sink has no opt-in flag for the kind."),
    };

    #endregion Sink

    /// <summary>The JSON options the bodies speak; the field names are pinned by their own attributes.</summary>
    private static readonly JsonSerializerOptions JsonOptions = new();

    /// <summary>One client-API send body exactly as the m.message event defines it.</summary>
    private sealed record ClientApiBody(
        [property: JsonPropertyName("msgtype")] string MessageType,
        [property: JsonPropertyName("body")] string Body)
    {
        internal ClientApiBody(string body)
            : this("m.text", body)
        {
        }
    }
}
