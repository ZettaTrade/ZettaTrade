namespace ZettaTrade.AI.Chat;

using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Composition of the AI chat module: the two named HttpClients the
/// providers build on, the providers themselves (the one-shot completion
/// and the streaming half ride the same singletons) and the provider
/// router both chat interfaces resolve to (T-207). Called from the
/// composition root — one line for the module.
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
        services.AddHttpClient(OpenAiCompatibleChatCompletion.HttpClientName, client =>
        {
            client.Timeout = OpenAiCompatibleChatCompletion.RequestTimeout;
        });

        services.AddSingleton<OllamaChatCompletion>();
        services.AddSingleton<OpenAiCompatibleChatCompletion>();
        services.AddSingleton<ChatProviderRouter>();
        services.AddSingleton<IChatCompletion>(sp => sp.GetRequiredService<ChatProviderRouter>());
        services.AddSingleton<IStreamingChatCompletion>(sp => sp.GetRequiredService<ChatProviderRouter>());

        return services;
    }
}
