namespace ZettaTrade.AI.Chat;

using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// <see cref="IChatCompletion"/> and <see cref="IStreamingChatCompletion"/>
/// over an OpenAI-compatible HTTP endpoint (T-207): a local LM Studio /
/// llama.cpp server or any cloud gateway speaking the
/// <c>POST {base}/chat/completions</c> dialect. One-shot: the request carries
/// model + messages ([system rider] + the conversation turns) with
/// <c>stream: false</c>, the answer is <c>choices[0].message.content</c>.
/// The streaming half posts the same body with <c>stream: true</c> and reads
/// the SSE reply line by line, yielding each <c>choices[0].delta.content</c>
/// the moment its <c>data:</c> line arrives; the <c>data: [DONE]</c> sentinel
/// closes the stream. Base URL and model are read per request from the
/// settings (<see cref="AiSettingsKeys.OpenAiCompatibleSection"/>), so a
/// settings-file change switches them at runtime; a blank base URL falls
/// back to the module's local default, while a blank model is an honest
/// error — a model name is the user's choice, never a guess. The API key
/// rides the secret store (<see cref="AiSettingsKeys.OpenAiCompatibleApiKeySecretKey"/>),
/// read per request: a stored key travels as the Authorization Bearer value,
/// an absent (or blank) one means the request goes unauthenticated — local
/// servers need no key, and the key itself never appears in any log.
/// Failures surface honestly: transport and status-code problems as
/// <see cref="HttpRequestException"/>, an unusable reply body as
/// <see cref="InvalidOperationException"/> naming the model. The streamed
/// read carries no client-side request timeout: a token-by-token generation
/// can outlast the one-shot's cold-load budget legitimately, so the Stop
/// affordance and the caller's own cancellation token govern it.
/// </summary>
public sealed class OpenAiCompatibleChatCompletion : IChatCompletion, IStreamingChatCompletion
{
    #region Constants

    /// <summary>The named HttpClient this provider builds on.</summary>
    public const string HttpClientName = "openai-compatible";

    /// <summary>Request budget: small local models on CPU answer in tens of
    /// seconds, so the standard 100 s default is too tight and one minute
    /// too strict; two minutes cover a cold model load (the same budget the
    /// Ollama route keeps).</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(2);

    /// <summary>The chat resource path appended to the configured base URL
    /// (the base may carry a version segment such as <c>/v1</c> — the
    /// provider appends the resource path only).</summary>
    private const string ChatPath = "chat/completions";

    /// <summary>The system role's wire spelling.</summary>
    private const string SystemRole = "system";

    /// <summary>The SSE sentinel line that closes the streamed reply.</summary>
    private const string StreamDoneSentinel = "[DONE]";

    /// <summary>The SSE data prefix every reply event carries.</summary>
    private const string StreamDataPrefix = "data:";

    /// <summary>Wire JSON options: the chat-completions dialect speaks
    /// camelCase (model/stream/messages/choices/delta/content); Web defaults
    /// both directions.</summary>
    private static readonly JsonSerializerOptions WireOptions =
        new(JsonSerializerDefaults.Web);

    /// <summary>The reply JSON's missing-content failure family (the honest
    /// no-content error; the model name completes it).</summary>
    private const string MessageContentMissingText =
        "The OpenAI-compatible server answered without message content";

    #endregion

    #region Fields

    private readonly ISettingsService _settings;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ISecretStore _secrets;
    private readonly ILogger<OpenAiCompatibleChatCompletion> _logger;

    #endregion

    #region Constructor

    /// <summary>
    /// Create a new OpenAI-compatible chat completion provider. The HTTP
    /// client is resolved from the named-client factory; the base URL, the
    /// model and the API key come from the settings and the secret store at
    /// request time.
    /// </summary>
    /// <param name="settings">The app settings service (AI sections).</param>
    /// <param name="httpClientFactory">Named-client factory.</param>
    /// <param name="secrets">The secret store the API key resolves from.</param>
    /// <param name="logger">Injected class logger.</param>
    public OpenAiCompatibleChatCompletion(
        ISettingsService settings,
        IHttpClientFactory httpClientFactory,
        ISecretStore secrets,
        ILogger<OpenAiCompatibleChatCompletion> logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
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

        var baseUrl = await ResolveBaseUrlAsync(ct);
        var model = await ResolveModelAsync(ct);
        var endpoint = new Uri(new Uri(NormalizeBaseUrl(baseUrl)), ChatPath);

        _logger.LogInformation(
            "AI completion: asking the OpenAI-compatible model {Model} at {BaseUrl} for a one-shot reply ({TurnCount} turns)",
            model,
            baseUrl,
            turns.Count);

        var request = BuildChatRequest(model, systemPrompt, turns, stream: false);
        using var requestMessage = BuildRequestMessage(endpoint, request, await ResolveApiKeyAsync(ct));
        var response = await _httpClientFactory.CreateClient(HttpClientName)
            .SendAsync(requestMessage, ct);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(ct);
        var reply = ExtractReplyContent(model, body);

        _logger.LogInformation(
            "AI completion: the OpenAI-compatible model answered ({ReplyLength} chars)",
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

        var baseUrl = await ResolveBaseUrlAsync(ct);
        var model = await ResolveModelAsync(ct);
        var endpoint = new Uri(new Uri(NormalizeBaseUrl(baseUrl)), ChatPath);

        _logger.LogInformation(
            "AI completion: asking the OpenAI-compatible model {Model} at {BaseUrl} for a streamed reply ({TurnCount} turns)",
            model,
            baseUrl,
            turns.Count);

        var request = BuildChatRequest(model, systemPrompt, turns, stream: true);

        // The streamed read carries no client-side request timeout: the Stop
        // affordance and the caller's own token govern it (the factory hands
        // a fresh instance per call, so the named client's cold-load budget
        // stays untouched for the one-shot path). The response is taken at
        // HEADERS-read: the default completion (ResponseContentRead) buffers
        // the whole reply and the deltas would never stream.
        var apiKey = await ResolveApiKeyAsync(ct);
        var client = _httpClientFactory.CreateClient(HttpClientName);
        client.Timeout = Timeout.InfiniteTimeSpan;
        using var requestMessage = BuildRequestMessage(endpoint, request, apiKey);
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
            // SSE framing: blank lines, comment lines and the non-payload
            // field lines (event/id/retry) carry no content; every event
            // rides a `data:` line (with or without the space).
            if (string.IsNullOrWhiteSpace(line) ||
                line.StartsWith(':') ||
                line.StartsWith("event:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("id:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("retry:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!line.StartsWith(StreamDataPrefix, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"The OpenAI-compatible server answered an unreadable line for the {model} model: the line is not a data event.");
            }

            var payload = line[StreamDataPrefix.Length..].Trim();
            if (payload == StreamDoneSentinel)
            {
                if (content.Length == 0)
                {
                    throw new InvalidOperationException(
                        $"{MessageContentMissingText} for the {model} model.");
                }

                yield break;
            }

            StreamReply? reply;
            try
            {
                reply = JsonSerializer.Deserialize<StreamReply>(payload, WireOptions);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    $"The OpenAI-compatible server answered an unreadable line for the {model} model: {ex.Message}",
                    ex);
            }

            var delta = reply?.Choices is { Length: > 0 } choices ? choices[0].Delta?.Content : null;
            if (!string.IsNullOrEmpty(delta))
            {
                content.Append(delta);
                yield return delta;
            }
        }

        // The stream closed without the [DONE] sentinel: an early server
        // exit. With no content at all this is the one-shot's missing-
        // content family; with deltas on the table it is the truncated-
        // stream family.
        if (content.Length == 0)
        {
            throw new InvalidOperationException($"{MessageContentMissingText} for the {model} model.");
        }

        throw new InvalidOperationException(
            $"The OpenAI-compatible server never closed the chat stream for the {model} model.");
    }

    #endregion

    #region Internals

    /// <summary>
    /// One settings value: a blank stored base URL (never written or emptied)
    /// falls back to the module default — never a blank endpoint.
    /// </summary>
    private async Task<string> ResolveBaseUrlAsync(CancellationToken ct)
    {
        var stored = await _settings.GetAsync<string>(
            AiSettingsKeys.OpenAiCompatibleSection,
            AiSettingsKeys.OpenAiCompatibleBaseUrl,
            string.Empty);
        return string.IsNullOrWhiteSpace(stored)
            ? AiSettingsKeys.DefaultOpenAiCompatibleBaseUrl
            : stored.Trim();
    }

    /// <summary>
    /// The model has NO default: a blank stored model is an honest error —
    /// asking a model with no name would fabricate an answer to nowhere.
    /// </summary>
    private async Task<string> ResolveModelAsync(CancellationToken ct)
    {
        var stored = await _settings.GetAsync<string>(
            AiSettingsKeys.OpenAiCompatibleSection,
            AiSettingsKeys.OpenAiCompatibleModel,
            string.Empty);
        if (string.IsNullOrWhiteSpace(stored))
        {
            throw new InvalidOperationException(
                "No OpenAI-compatible model is configured: set the model in the AI chat settings (or the settings file) before sending a chat.");
        }

        return stored.Trim();
    }

    /// <summary>
    /// The API key resolves from the secret store per request: a stored
    /// (non-blank) key rides the Authorization header, an absent one leaves
    /// the request unauthenticated (local servers need no key). The key
    /// value itself never reaches a log — only its presence does.
    /// </summary>
    private async Task<string?> ResolveApiKeyAsync(CancellationToken ct)
    {
        var stored = await _secrets.GetAsync(AiSettingsKeys.OpenAiCompatibleApiKeySecretKey, ct);
        return string.IsNullOrWhiteSpace(stored) ? null : stored.Trim();
    }

    /// <summary>The base URL normalized so a relative append lands right:
    /// a trailing slash turns the stored base into a true directory URL.</summary>
    private static string NormalizeBaseUrl(string baseUrl) =>
        baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/";

    /// <summary>The POST message with its optional Bearer authorization.</summary>
    private static HttpRequestMessage BuildRequestMessage(
        Uri endpoint,
        StringContent request,
        string? apiKey)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = request };
        if (apiKey is not null)
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        return message;
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
    /// The assistant's reply is <c>choices[0].message.content</c>. Json
    /// throw paths surface as <see cref="InvalidOperationException"/> (an
    /// unusable reply), with the model name in the honest message.
    /// </summary>
    private static string ExtractReplyContent(string model, string body)
    {
        ChatReply? reply;
        try
        {
            reply = JsonSerializer.Deserialize<ChatReply>(body, WireOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"The OpenAI-compatible server answered an unreadable reply for the {model} model: {ex.Message}",
                ex);
        }

        var content = reply?.Choices is { Length: > 0 } choices
            ? choices[0].Message?.Content
            : null;
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException($"{MessageContentMissingText} for the {model} model.");
        }

        return content;
    }

    /// <summary>Request wire shape (camelCase: model, stream, messages).</summary>
    private sealed record ChatRequestPayload(string Model, bool Stream, ChatMessagePayload[] Messages);

    /// <summary>One message of the request wire shape.</summary>
    private sealed record ChatMessagePayload(string Role, string Content);

    /// <summary>The one-shot reply wire shape (choices).</summary>
    private sealed record ChatReply(ChatChoice[] Choices);

    /// <summary>One choice of the reply wire shape.</summary>
    private sealed record ChatChoice(ChatMessage Message);

    /// <summary>The message object of the reply wire shape.</summary>
    private sealed record ChatMessage(string Role, string Content);

    /// <summary>The streamed reply event wire shape (choices).</summary>
    private sealed record StreamReply(StreamChoice[] Choices);

    /// <summary>One choice of the streamed event wire shape (delta).</summary>
    private sealed record StreamChoice(StreamDelta Delta);

    /// <summary>The delta object of the streamed event wire shape.</summary>
    private sealed record StreamDelta(string Role, string Content);

    #endregion
}
