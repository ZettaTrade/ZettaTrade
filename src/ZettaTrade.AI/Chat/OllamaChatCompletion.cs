namespace ZettaTrade.AI.Chat;

using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// <see cref="IChatCompletion"/> and <see cref="IStreamingChatCompletion"/>
/// over the Ollama HTTP server (local model first). One-shot, non-streaming
/// <c>POST /api/chat</c>: the request carries
/// model + messages ([system rider] + the conversation turns), the answer is
/// the final NDJSON line's <c>message.content</c>. The streaming half (T-205)
/// posts the same body with <c>stream: true</c> and reads the NDJSON reply
/// line by line, yielding each delta the moment its line arrives. Endpoint
/// and model are
/// read per request from the settings (section <see cref="AiSettingsKeys.Section"/>),
/// so a settings-file change switches them at runtime; an unset or blank
/// value falls back to the module's documented defaults. Failures surface
/// honestly: transport and status-code problems as <see cref="HttpRequestException"/>,
/// an unusable reply body as <see cref="InvalidOperationException"/>.
/// The streamed read carries no client-side request timeout: a token-by-token
/// generation can outlast the one-shot's cold-load budget legitimately, so
/// the Stop affordance and the caller's own cancellation token govern it.
/// </summary>
public sealed class OllamaChatCompletion : IChatCompletion, IStreamingChatCompletion
{
    #region Constants

    /// <summary>The named HttpClient this provider builds on.</summary>
    public const string HttpClientName = "ollama";

    /// <summary>Request budget: small local models on CPU answer in tens of
    /// seconds, so the standard 100 s default is too tight and one minute
    /// too strict; two minutes cover a cold model load.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(2);

    /// <summary>Ollama's chat path on the configured base URL.</summary>
    private const string ChatPath = "/api/chat";

    /// <summary>The system role's wire spelling.</summary>
    private const string SystemRole = "system";

    /// <summary>Wire JSON options: Ollama speaks camelCase (model/stream/
    /// message/done/role/content); Web defaults both directions.</summary>
    private static readonly JsonSerializerOptions WireOptions =
        new(JsonSerializerDefaults.Web);

    /// <summary>The reply JSON's content property path (message.content).</summary>
    private const string MessageContentMissingText =
        "The Ollama server answered without message content at the end of the stream.";

    #endregion

    #region Fields

    private readonly ISettingsService _settings;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<OllamaChatCompletion> _logger;

    #endregion

    #region Constructor

    /// <summary>
    /// Create a new Ollama chat completion provider. The HTTP client is
    /// resolved from the named-client factory; the endpoint and the model
    /// come from the settings file at request time.
    /// </summary>
    /// <param name="settings">The app settings service (AI section).</param>
    /// <param name="httpClientFactory">Named-client factory.</param>
    /// <param name="logger">Injected class logger.</param>
    public OllamaChatCompletion(
        ISettingsService settings,
        IHttpClientFactory httpClientFactory,
        ILogger<OllamaChatCompletion> logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    #endregion

    #region IChatCompletion

    /// <inheritdoc/>
    public async Task<string> CompleteAsync(
        string systemPrompt,
        IReadOnlyList<ChatTurn> turns,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(systemPrompt);
        ArgumentNullException.ThrowIfNull(turns);

        if (string.IsNullOrWhiteSpace(systemPrompt))
            throw new ArgumentException("The system prompt must not be empty.", nameof(systemPrompt));

        var baseUrl = await ResolveAsync(
            AiSettingsKeys.OllamaUrl,
            AiSettingsKeys.DefaultOllamaUrl,
            ct);
        var model = await ResolveAsync(
            AiSettingsKeys.Model,
            AiSettingsKeys.DefaultModel,
            ct);
        var endpoint = new Uri(new Uri(baseUrl), ChatPath);

        _logger.LogInformation(
            "AI completion: asking the local model {Model} at {BaseUrl} for a one-shot reply ({TurnCount} turns)",
            model,
            baseUrl,
            turns.Count);

        var request = BuildChatRequest(model, systemPrompt, turns, stream: false);
        var response = await _httpClientFactory.CreateClient(HttpClientName)
            .PostAsync(endpoint, request, ct);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(ct);
        var reply = ExtractReplyContent(model, body);

        _logger.LogInformation(
            "AI completion: the local model answered ({ReplyLength} chars)",
            reply.Length);

        return reply;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<string> StreamAsync(
        string systemPrompt,
        IReadOnlyList<ChatTurn> turns,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(systemPrompt);
        ArgumentNullException.ThrowIfNull(turns);

        if (string.IsNullOrWhiteSpace(systemPrompt))
            throw new ArgumentException("The system prompt must not be empty.", nameof(systemPrompt));

        var baseUrl = await ResolveAsync(
            AiSettingsKeys.OllamaUrl,
            AiSettingsKeys.DefaultOllamaUrl,
            ct);
        var model = await ResolveAsync(
            AiSettingsKeys.Model,
            AiSettingsKeys.DefaultModel,
            ct);
        var endpoint = new Uri(new Uri(baseUrl), ChatPath);

        _logger.LogInformation(
            "AI completion: asking the local model {Model} at {BaseUrl} for a streamed reply ({TurnCount} turns)",
            model,
            baseUrl,
            turns.Count);

        var request = BuildChatRequest(model, systemPrompt, turns, stream: true);

        // The streamed read carries no client-side request timeout: the Stop
        // affordance and the caller's own token govern it (the factory hands
        // a fresh instance per call, so the named client's cold-load budget
        // stays untouched for the one-shot path). The response is taken at
        // HEADERS-read: the default PostAsync completion (ResponseContentRead)
        // buffers the whole reply and the deltas would never stream.
        var client = _httpClientFactory.CreateClient(HttpClientName);
        client.Timeout = Timeout.InfiniteTimeSpan;
        using var requestMessage = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = request };
        using var response = await client.SendAsync(
            requestMessage,
            HttpCompletionOption.ResponseHeadersRead,
            ct);
        response.EnsureSuccessStatusCode();

        var content = new StringBuilder();
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(body);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            ReplyLine? reply;
            try
            {
                reply = JsonSerializer.Deserialize<ReplyLine>(line, WireOptions);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    $"The Ollama server answered an unreadable line for the {model} model: {ex.Message}",
                    ex);
            }

            var delta = reply?.Message?.Content;
            if (!string.IsNullOrEmpty(delta))
            {
                content.Append(delta);
                yield return delta;
            }

            if (reply is { Done: true })
            {
                if (content.Length == 0)
                {
                    throw new InvalidOperationException(MessageContentMissingText);
                }

                yield break;
            }
        }

        // The stream closed without the terminal line: an early server exit.
        // With no content at all this is the one-shot's missing-content
        // family; with deltas on the table it is the truncated-stream family.
        if (content.Length == 0)
        {
            throw new InvalidOperationException(MessageContentMissingText);
        }

        throw new InvalidOperationException(
            $"The Ollama server never closed the chat stream for the {model} model.");
    }

    #endregion

    #region Internals

    /// <summary>
    /// One settings value: a blank stored value (never written or emptied)
    /// falls back to the module default — never a blank endpoint or model.
    /// </summary>
    private async Task<string> ResolveAsync(string key, string fallback, CancellationToken ct)
    {
        var stored = await _settings.GetAsync<string>(AiSettingsKeys.Section, key, string.Empty);
        return string.IsNullOrWhiteSpace(stored) ? fallback : stored.Trim();
    }

    /// <summary>The chat request body: model + messages, streaming per request.</summary>
    private static StringContent BuildChatRequest(
        string model,
        string systemPrompt,
        IReadOnlyList<ChatTurn> turns,
        bool stream)
    {
        var messages = new List<(string role, string content)> { (SystemRole, systemPrompt) };
        foreach (var turn in turns)
        {
            messages.Add((turn.Role, turn.Content));
        }

        var payloadShape = new ChatRequestPayload(
            Model: model,
            Stream: stream,
            Messages: messages
                .Select(m => new ChatMessagePayload(m.role, m.content))
                .ToArray());
        var body = JsonSerializer.Serialize(payloadShape, WireOptions);

        return new StringContent(body, Encoding.UTF8, "application/json");
    }

    /// <summary>
    /// The assistant's reply is the LAST non-empty NDJSON line's
    /// <c>message.content</c>. With <c>stream: false</c> the whole reply is a
    /// single JSON object; the same scan handles both shapes. Json throw
    /// paths surface as <see cref="InvalidOperationException"/> (an unusable
    /// reply), with the model name in the honest message.
    /// </summary>
    private static string ExtractReplyContent(string model, string body)
    {
        var lines = body
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        for (var index = lines.Length - 1; index >= 0; index--)
        {
            ReplyLine? line;
            try
            {
                line = JsonSerializer.Deserialize<ReplyLine>(lines[index], WireOptions);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    $"The Ollama server answered an unreadable line for the {model} model: {ex.Message}",
                    ex);
            }

            if (line is not { Done: true })
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(line.Message.Content))
                throw new InvalidOperationException(MessageContentMissingText);

            return line.Message.Content;
        }

        throw new InvalidOperationException(
            $"The Ollama server never closed the chat stream for the {model} model.");
    }

    /// <summary>Request wire shape (camelCase: model, stream, messages).</summary>
    private sealed record ChatRequestPayload(string Model, bool Stream, ChatMessagePayload[] Messages);

    /// <summary>One message of the request wire shape.</summary>
    private sealed record ChatMessagePayload(string Role, string Content);

    /// <summary>The NDJSON answer line wire shape (message + done).</summary>
    private sealed record ReplyLine(ReplyMessage Message, bool Done);

    /// <summary>The message object of the answer line wire shape.</summary>
    private sealed record ReplyMessage(string Role, string Content);

    #endregion
}
