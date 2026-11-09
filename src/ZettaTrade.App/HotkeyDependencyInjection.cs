namespace ZettaTrade.App;

using Microsoft.Extensions.DependencyInjection;
using ZettaTrade.App.Services;
using ZettaTrade.App.ViewModels;

/// <summary>
/// The hotkey manager's composition (T-102): the live-map service and the
/// shared editor/cheat-sheet ViewModel, both application-lifetime
/// singletons — the main window's key route, the menu's advertised
/// gestures, the cheat-sheet overlay and the settings tab all read the
/// same instance, so advertised and real never diverge. Registered by the
/// composition root right before <see cref="MainViewModel"/>, after the
/// palette services.
/// </summary>
internal static class HotkeyDependencyInjection
{
    /// <summary>Register the hotkey manager's service and ViewModel as singletons.</summary>
    public static IServiceCollection AddHotkeyServices(this IServiceCollection services)
    {
        services.AddSingleton<HotkeyService>();
        services.AddSingleton<HotkeyViewModel>();
        return services;
    }
}
