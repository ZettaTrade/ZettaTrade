namespace ZettaTrade.AI.Chat;

/// <summary>
/// Settings contract of the AI chat module: the provider the chat answers
/// with (local Ollama first — cloud is an enhancement), the Ollama endpoint
/// and model, and the OpenAI-compatible endpoint pieces. Values are read per
/// request, so switching the provider, the model or the endpoint in the
/// settings file takes effect at runtime. The OpenAI-compatible API key is
/// NOT here: it rides the secret store (the T-046 canon) — config carries
/// non-credential pieces only.
/// </summary>
public static class AiSettingsKeys
{
    /// <summary>Settings section owned by the AI module.</summary>
    public const string Section = "AI";

    /// <summary>Base URL of the Ollama HTTP server.</summary>
    public const string OllamaUrl = "OllamaUrl";

    /// <summary>Model tag served by Ollama (e.g. "llama3.2").</summary>
    public const string Model = "Model";

    /// <summary>The chat provider pick: "Ollama" or "OpenAiCompatible"
    /// (T-207). Blank falls back to the default; anything else is an honest
    /// error at send time — a typo never silently routes the conversation
    /// somewhere else.</summary>
    public const string Provider = "Provider";

    /// <summary>Default provider: the shipped local-first posture.</summary>
    public const string DefaultProvider = "Ollama";

    /// <summary>The OpenAI-compatible provider's own settings subsection
    /// (T-207): its base URL and model ride a section of their own so the
    /// two providers' pieces never collide.</summary>
    public const string OpenAiCompatibleSection = "AI.OpenAiCompatible";

    /// <summary>Base URL of the OpenAI-compatible HTTP server (e.g. a local
    /// LM Studio or llama.cpp server, or a cloud gateway). The provider
    /// appends <c>chat/completions</c> to it.</summary>
    public const string OpenAiCompatibleBaseUrl = "BaseUrl";

    /// <summary>Model name served by the OpenAI-compatible endpoint
    /// (e.g. "gpt-4o-mini" or the local server's model id).</summary>
    public const string OpenAiCompatibleModel = "Model";

    /// <summary>Default OpenAI-compatible endpoint: the local server on
    /// LM Studio's standard port — the same local-first posture, a different
    /// local server. There is NO default model: an unset one is an honest
    /// send-time error (a model name is a user's choice, not a guess).</summary>
    public const string DefaultOpenAiCompatibleBaseUrl = "http://localhost:1234/v1";

    /// <summary>The secret store key of the OpenAI-compatible API key
    /// (T-207). Cloud endpoints need it; local servers run keyless — a blank
    /// (absent) secret means the requests carry no Authorization header.</summary>
    public const string OpenAiCompatibleApiKeySecretKey = "ai.openai-compatible.api-key";

    /// <summary>Default Ollama endpoint: the local server on its standard port.</summary>
    public const string DefaultOllamaUrl = "http://localhost:11434";

    /// <summary>Default model tag: a small local model, quick on CPU.</summary>
    public const string DefaultModel = "llama3.2";

    /// <summary>Whether the chat streams the reply token by token (T-205).
    /// Endpoints that cannot stream set it false; the chat panel then rides
    /// the one-shot completion verbatim. Read at issue time, like the rest
    /// of the section — a settings-file flip between sends changes exactly
    /// what the next send does.</summary>
    public const string Streaming = "Streaming";

    /// <summary>Default streaming posture: on — the shipped Ollama route
    /// streams natively.</summary>
    public const bool DefaultStreaming = true;
}
