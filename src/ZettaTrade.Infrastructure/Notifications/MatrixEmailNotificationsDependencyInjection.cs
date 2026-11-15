namespace ZettaTrade.Infrastructure.Notifications;

using System;
using Livery.Localization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// DI wiring of the Matrix and email trade-notification sinks (T-262):
/// one bounded named HttpClient for the Matrix pushes (the sends PUT
/// absolute URLs, so no base address rides the client — the email sink
/// rides its own disposable SMTP session instead) and each sink
/// registered as itself AND as an <see cref="INotificationSink"/>, so
/// the App bridge's sink fleet picks the pair up beside the Telegram and
/// webhook sinks. The Matrix access token and the SMTP credentials embed
/// their own secret material and ride <see cref="ISecretStore"/>; the
/// homeserver URL, the room id and the SMTP server rows are public by
/// design and ride the settings.
/// </summary>
public static class MatrixEmailNotificationsDependencyInjection
{
    /// <summary>Register the Matrix and email sinks: the bounded Matrix client and the two sinks (concrete + contract each).</summary>
    /// <param name="services">The application's service collection.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddMatrixEmailNotificationApi(this IServiceCollection services)
    {
        services.AddHttpClient(MatrixSink.HttpClientName, client =>
        {
            // An unreachable channel never holds a send past this bound.
            client.Timeout = TimeSpan.FromSeconds(10);
        });

        return services
            .AddSingleton(sp => new MatrixSink(
                sp.GetRequiredService<ISettingsService>(),
                sp.GetRequiredService<ISecretStore>(),
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(MatrixSink.HttpClientName),
                sp.GetRequiredService<ILocalizationManager>(),
                sp.GetRequiredService<ILogger<MatrixSink>>()))
            .AddSingleton<INotificationSink>(sp => sp.GetRequiredService<MatrixSink>())
            .AddSingleton(sp => new EmailSink(
                sp.GetRequiredService<ISettingsService>(),
                sp.GetRequiredService<ISecretStore>(),
                sp.GetRequiredService<ILocalizationManager>(),
                sp.GetRequiredService<ILogger<EmailSink>>()))
            .AddSingleton<INotificationSink>(sp => sp.GetRequiredService<EmailSink>());
    }
}
