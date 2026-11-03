namespace ZettaTrade.AI.Chat;

/// <summary>
/// Settings contract of the AI chat module: the Ollama endpoint the local
/// model is served from (local models first — cloud is an enhancement) and
/// the model to answer with. Values are read per request, so switching the
/// model or the endpoint in the settings file takes effect at runtime.
/// </summary>
public static class AiSettingsKeys
{
    /// <summary>Settings section owned by the AI module.</summary>
    public const string Section = "AI";

    /// <summary>Base URL of the Ollama HTTP server.</summary>
    public const string OllamaUrl = "OllamaUrl";

    /// <summary>Model tag served by Ollama (e.g. "llama3.2").</summary>
    public const string Model = "Model";

    /// <summary>Default Ollama endpoint: the local server on its standard port.</summary>
    public const string DefaultOllamaUrl = "http://localhost:11434";

    /// <summary>Default model tag: a small local model, quick on CPU.</summary>
    public const string DefaultModel = "llama3.2";
}
