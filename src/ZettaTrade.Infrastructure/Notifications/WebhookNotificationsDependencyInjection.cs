namespace ZettaTrade.Infrastructure.Notifications;

using System;
using Livery.Localization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// DI wiring of the webhook trade-notification sinks (T-261): one bounded
/// named HttpClient per channel (Discord, Slack, ntfy — the sends POST
/// absolute URLs, so no base address rides the clients) and each sink
/// registered as itself AND as an <see cref="INotificationSink"/>, so the
/// App bridge's sink fleet picks the whole batch up beside the Telegram
/// sink. The Discord and Slack webhook URLs embed their own secret
/// material and ride <see cref="ISecretStore"/>; the ntfy topic URL is
/// public by design and rides the settings.
/// </summary>
public static class WebhookNotificationsDependencyInjection
{
    /// <summary>Register the webhook sink batch: the bounded clients and the three sinks (concrete + contract each).</summary>
    /// <param name="services">The application's service collection.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddWebhookNotificationApi(this IServiceCollection services)
    {
        services.AddHttpClient(DiscordSink.HttpClientName, client =>
        {
            // An unreachable channel never holds a send past this bound.
            client.Timeout = TimeSpan.FromSeconds(10);
        });
        services.AddHttpClient(SlackSink.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
        });
        services.AddHttpClient(NtfySink.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
        });

        return services
            .AddSingleton(sp => new DiscordSink(
                sp.GetRequiredService<ISettingsService>(),
                sp.GetRequiredService<ISecretStore>(),
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(DiscordSink.HttpClientName),
                sp.GetRequiredService<ILocalizationManager>(),
                sp.GetRequiredService<ILogger<DiscordSink>>()))
            .AddSingleton<INotificationSink>(sp => sp.GetRequiredService<DiscordSink>())
            .AddSingleton(sp => new SlackSink(
                sp.GetRequiredService<ISettingsService>(),
                sp.GetRequiredService<ISecretStore>(),
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(SlackSink.HttpClientName),
                sp.GetRequiredService<ILocalizationManager>(),
                sp.GetRequiredService<ILogger<SlackSink>>()))
            .AddSingleton<INotificationSink>(sp => sp.GetRequiredService<SlackSink>())
            .AddSingleton(sp => new NtfySink(
                sp.GetRequiredService<ISettingsService>(),
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(NtfySink.HttpClientName),
                sp.GetRequiredService<ILocalizationManager>(),
                sp.GetRequiredService<ILogger<NtfySink>>()))
            .AddSingleton<INotificationSink>(sp => sp.GetRequiredService<NtfySink>());
    }
}
