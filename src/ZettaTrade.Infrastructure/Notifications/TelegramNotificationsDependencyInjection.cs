namespace ZettaTrade.Infrastructure.Notifications;

using System;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// DI wiring of the Telegram trade-notification sink's transport (T-041):
/// the named HttpClient the Bot API sends ride (the bounded request timeout
/// included), the Bot API client and the best-effort notifier service.
/// The App composition calls this, then registers the event bridge that
/// feeds the sink from the notification center's trade events.
/// </summary>
public static class TelegramNotificationsDependencyInjection
{
    /// <summary>Register the Telegram transport stack: the named client, the Bot API client and the notifier.</summary>
    /// <param name="services">The application's service collection.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddTelegramNotificationApi(this IServiceCollection services)
    {
        services.AddHttpClient(TelegramBotClient.HttpClientName, client =>
        {
            // The Bot API's own base: the sends POST directly to the user's
            // bot (no middleman service); the token rides each request's path.
            client.BaseAddress = new Uri(TelegramBotClient.ApiBaseUrl);
            client.Timeout = TelegramBotClient.RequestTimeout;
        });

        return services
            .AddSingleton<TelegramBotClient>()
            .AddSingleton<TelegramNotifierService>();
    }
}
