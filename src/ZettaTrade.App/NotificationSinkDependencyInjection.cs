namespace ZettaTrade.App;

using Livery.Localization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZettaTrade.App.Services;
using ZettaTrade.Infrastructure.Notifications;

/// <summary>
/// Composition of the trade-notification sink fleet (T-260; the webhook
/// batch rides since T-261): the Infrastructure transport stacks (each
/// sink's own registration — the Telegram sink first, T-041; Discord,
/// Slack and ntfy beside it) and the App-side bridge that feeds every
/// registered <see cref="INotificationSink"/> from the notification
/// center's trade events. Called from the composition root — one line —
/// AFTER the notification services (the bridge rides the center's event
/// lane) and AFTER the trading-mode registration (the bridge reads its
/// lane stream).
/// </summary>
public static class NotificationSinkDependencyInjection
{
    /// <summary>Register the trade-notification bridge over the registered sink fleet.</summary>
    /// <param name="services">The application's service collection.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddNotificationSinkServices(this IServiceCollection services) => services
        .AddTelegramNotificationApi()
        .AddWebhookNotificationApi()
        .AddSingleton(sp => new TradeNotificationBridge(
            sp.GetRequiredService<NotificationCenter>().Events,
            sp.GetRequiredService<TradingModeService>().Lane,
            sp.GetRequiredService<IEnumerable<INotificationSink>>(),
            sp.GetRequiredService<ILogger<TradeNotificationBridge>>()));
}
