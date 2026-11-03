namespace ZettaTrade.AI.Chat;

using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Composition of the AI chat module: the named HttpClient the Ollama
/// provider builds on and the <see cref="IChatCompletion"/> service itself.
/// Called from the composition root — one line for the module.
/// </summary>
public static class AiDependencyInjection
{
    /// <summary>Register the AI chat services (local models first).</summary>
    /// <param name="services">The application's service collection.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddAiServices(this IServiceCollection services)
    {
        services.AddHttpClient(OllamaChatCompletion.HttpClientName, client =>
        {
            client.Timeout = OllamaChatCompletion.RequestTimeout;
        });

        services.AddSingleton<IChatCompletion, OllamaChatCompletion>();

        return services;
    }
}
