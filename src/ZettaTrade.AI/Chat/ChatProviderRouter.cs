namespace ZettaTrade.AI.Chat;

using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// The provider seam of the AI chat (T-207): both chat surfaces
/// (<see cref="IChatCompletion"/> and <see cref="IStreamingChatCompletion"/>) route
/// per request to the provider the settings pick — the shipped Ollama
/// (local-first default) or the OpenAI-compatible endpoint. The pick is
/// read per request, so a settings-file flip switches the provider live;
/// a blank (unset) stored pick falls back to the default, and an unknown
/// non-blank one is an honest error naming the stored value — a typo never
/// silently routes the conversation somewhere else.
/// </summary>
public sealed class ChatProviderRouter : IChatCompletion, IStreamingChatCompletion
{
    #region Constants

    /// <summary>The Ollama pick's stored spelling (case-insensitive match).</summary>
    public const string OllamaProviderName = "Ollama";

    /// <summary>The OpenAI-compatible pick's stored spelling
    /// (case-insensitive match).</summary>
    public const string OpenAiCompatibleProviderName = "OpenAiCompatible";

    #endregion

    #region Fields

    private readonly ISettingsService _settings;
    private readonly OllamaChatCompletion _ollama;
    private readonly OpenAiCompatibleChatCompletion _openAiCompatible;
    private readonly ILogger<ChatProviderRouter> _logger;

    #endregion

    #region Constructor

    /// <summary>
    /// Create a new chat provider router. Both providers ride their own
    /// singletons; the pick comes from the settings file at request time.
    /// </summary>
    /// <param name="settings">The app settings service (AI section).</param>
    /// <param name="ollama">The Ollama provider (the shipped default).</param>
    /// <param name="openAiCompatible">The OpenAI-compatible provider.</param>
    /// <param name="logger">Injected class logger.</param>
    public ChatProviderRouter(
        ISettingsService settings,
        OllamaChatCompletion ollama,
        OpenAiCompatibleChatCompletion openAiCompatible,
        ILogger<ChatProviderRouter> logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _ollama = ollama ?? throw new ArgumentNullException(nameof(ollama));
        _openAiCompatible = openAiCompatible ?? throw new ArgumentNullException(nameof(openAiCompatible));
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
        var (oneShot, _) = await ResolveAsync(ct);
        return await oneShot.CompleteAsync(systemPrompt, turns, ct);
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<string> StreamAsync(
        string systemPrompt,
        IReadOnlyList<ChatTurn> turns,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var (_, streaming) = await ResolveAsync(ct);
        await foreach (var delta in streaming.StreamAsync(systemPrompt, turns, ct))
        {
            yield return delta;
        }
    }

    #endregion

    #region Internals

    /// <summary>
    /// Resolve the provider the stored pick names: blank falls back to the
    /// shipped default, a known name (case-insensitive) picks its provider,
    /// anything else is an honest error — no default substitution for an
    /// unparsable pick. Both chat surfaces ride the same concrete (each
    /// provider implements both); the pair keeps the resolution single.
    /// </summary>
    private async Task<(IChatCompletion OneShot, IStreamingChatCompletion Streaming)> ResolveAsync(CancellationToken ct)
    {
        var stored = await _settings.GetAsync<string>(AiSettingsKeys.Section, AiSettingsKeys.Provider, string.Empty);
        if (string.IsNullOrWhiteSpace(stored))
        {
            return (_ollama, _ollama);
        }

        var pick = stored.Trim();
        if (string.Equals(pick, OllamaProviderName, StringComparison.OrdinalIgnoreCase))
        {
            return (_ollama, _ollama);
        }

        if (string.Equals(pick, OpenAiCompatibleProviderName, StringComparison.OrdinalIgnoreCase))
        {
            return (_openAiCompatible, _openAiCompatible);
        }

        _logger.LogError(
            "The stored AI chat provider '{Provider}' is not one of the known providers ('{Ollama}', '{OpenAiCompatible}')",
            pick,
            OllamaProviderName,
            OpenAiCompatibleProviderName);
        throw new InvalidOperationException(
            $"The stored AI chat provider '{pick}' is not one of the known providers " +
            $"('{OllamaProviderName}', '{OpenAiCompatibleProviderName}').");
    }

    #endregion
}
