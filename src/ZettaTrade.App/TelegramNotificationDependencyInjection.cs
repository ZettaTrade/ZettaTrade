namespace ZettaTrade.App;

using Livery.Localization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZettaTrade.App.Services;
using ZettaTrade.Infrastructure.Notifications;

/// <summary>
/// Composition of the Telegram trade-notification sink (T-041): the
/// Infrastructure transport stack (the named Bot API client + the
/// best-effort notifier) and the App-side bridge that feeds it from the
/// notification center's trade events. Called from the composition root —
/// one line — AFTER the notification services (the bridge rides the center's
/// event lane) and AFTER the trading-mode registration (the bridge reads its
/// lane stream).
/// </summary>
public static class TelegramNotificationDependencyInjection
{
    /// <summary>Register the Telegram sink: the transport, the notifier and the trade-event bridge.</summary>
    /// <param name="services">The application's service collection.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddTelegramNotificationServices(this IServiceCollection services) => services
        .AddTelegramNotificationApi()
        .AddSingleton(sp => new TelegramNotificationBridge(
            sp.GetRequiredService<NotificationCenter>().Events,
            sp.GetRequiredService<TradingModeService>().Lane,
            sp.GetRequiredService<TelegramNotifierService>(),
            sp.GetRequiredService<ILocalizationManager>(),
            sp.GetRequiredService<ILogger<TelegramNotificationBridge>>()));
}
