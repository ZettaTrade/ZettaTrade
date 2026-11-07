namespace ZettaTrade.App;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZettaTrade.App.Services;
using ZettaTrade.App.ViewModels;

/// <summary>
/// The symbol palette's composition (T-095): the instrument source and the
/// palette ViewModel, both application-lifetime singletons — the session's
/// recents live in the ViewModel's memory and the source's snapshot serves
/// every open. Registered by the composition root right before
/// <see cref="MainViewModel"/>, after the chart ViewModel it applies into.
/// </summary>
internal static class PaletteDependencyInjection
{
    /// <summary>Register the palette source and ViewModel as singletons.</summary>
    public static IServiceCollection AddPaletteServices(this IServiceCollection services)
    {
        services.AddSingleton<SymbolPaletteSource>();
        services.AddSingleton<SymbolPaletteViewModel>();
        return services;
    }
}
