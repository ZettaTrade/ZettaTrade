namespace ZettaTrade.Infrastructure.Notifications;

using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// The Telegram Bot API's <c>sendMessage</c> transport (T-041): one POST per
/// message, straight to the user's own bot on <c>api.telegram.org</c> — no
/// third-party middleman service sits in between. The token rides the
/// request path exactly as the Bot API defines it; it never appears in a
/// failure report (a fault names the status or the API's own description
/// only) and it never travels anywhere but this call.
/// <para>
/// Every fault throws to the caller's degrade lane (the notifier service owns
/// the best-effort contract): a non-success status, a <c>ok:false</c> answer,
/// a malformed body and a transport fault (the bounded timeout included) all
/// surface as exceptions carrying no secret material.
/// </para>
/// </summary>
public sealed class TelegramBotClient
{
    #region Constants

    /// <summary>The named HttpClient the sends ride (registered by the module's composition).</summary>
    public const string HttpClientName = "TelegramBot";

    /// <summary>The Telegram Bot API's base URL (the requests POST directly there — the user's own bot).</summary>
    public const string ApiBaseUrl = "https://api.telegram.org";

    /// <summary>How long one send may take: an unreachable API never holds a send past this bound (the named client's own timeout).</summary>
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The JSON options the bodies speak; the field names are pinned by their own attributes.</summary>
    private static readonly JsonSerializerOptions JsonOptions = new();

    #endregion Constants

    #region Private Fields

    /// <summary>The production client source (the named factory client); null on the pinned-test seam.</summary>
    private readonly IHttpClientFactory? _httpClientFactory;

    /// <summary>The test-pinned client aimed at the loopback stub; null in production.</summary>
    private readonly HttpClient? _pinnedClient;

    #endregion Private Fields

    #region Constructors

    /// <summary>Create the client over the named HttpClient factory (the production shape: one bounded client per send).</summary>
    /// <param name="httpClientFactory">The factory the named client resolves from.</param>
    public TelegramBotClient(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
    }

    /// <summary>
    /// The test seam: the tests pin one HttpClient aimed at the loopback stub
    /// (its base address and its own tight timeout), so the transport facts
    /// run against a scripted API and never against the real network.
    /// </summary>
    internal TelegramBotClient(HttpClient pinnedClient)
    {
        _pinnedClient = pinnedClient ?? throw new ArgumentNullException(nameof(pinnedClient));
    }

    #endregion Constructors

    #region Send

    /// <summary>
    /// POST one message to the user's bot chat. The call resolves when the
    /// API answered with a well-formed <c>ok:true</c> envelope; every other
    /// outcome (status, envelope, transport) throws — the notifier's degrade
    /// lane turns it into the one logged warning.
    /// </summary>
    /// <param name="botToken">The user's own bot token (rides the request path per the Bot API).</param>
    /// <param name="chatId">The chat the message lands in (the API accepts the numeric id as a string).</param>
    /// <param name="message">The composed message text.</param>
    /// <param name="cancellationToken">Cooperative cancellation on top of the client's bounded timeout.</param>
    /// <exception cref="InvalidOperationException">A non-success status or a negative envelope.</exception>
    public async Task SendMessageAsync(string botToken, string chatId, string message, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(botToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(chatId);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        // The token rides the path per the Bot API's own scheme. The request
        // never logs itself anywhere: the fault paths below name the status
        // or the API description only, so the token cannot leak into the
        // files-only log through a failure report.
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"bot{botToken}/sendMessage", UriKind.Relative))
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new SendMessageBody(chatId, message), JsonOptions),
                Encoding.UTF8,
                "application/json"),
        };

        var client = _pinnedClient ?? _httpClientFactory!.CreateClient(HttpClientName);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"The Telegram sendMessage call answered HTTP {(int)response.StatusCode}.");
        }

        // The envelope is the API's own truth: ok:true means delivered, a
        // false answer or unreadable bytes both count as failed sends — the
        // description (the API's own reason) rides the fault message.
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (document.RootElement.ValueKind is not JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("ok", out var ok) ||
            ok.ValueKind is not JsonValueKind.True)
        {
            var description = document.RootElement.ValueKind is JsonValueKind.Object &&
                              document.RootElement.TryGetProperty("description", out var described) &&
                              described.ValueKind is JsonValueKind.String
                ? described.GetString()
                : null;
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(description)
                    ? "The Telegram sendMessage call answered a negative or unreadable envelope."
                    : $"The Telegram sendMessage call was refused: {description}");
        }
    }

    #endregion Send

    /// <summary>One sendMessage body exactly as the Bot API defines it (the snake_case names are the wire contract).</summary>
    private sealed record SendMessageBody(
        [property: JsonPropertyName("chat_id")] string ChatId,
        [property: JsonPropertyName("text")] string Text);
}
