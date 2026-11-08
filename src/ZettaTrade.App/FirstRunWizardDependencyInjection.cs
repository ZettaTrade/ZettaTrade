namespace ZettaTrade.App;

using Microsoft.Extensions.DependencyInjection;
using ZettaTrade.App.ViewModels;

/// <summary>
/// DI wiring for the first-run wizard flow (T-099). The view model is
/// transient: every wizard open (the first run or the settings-window re-run)
/// seeds fresh from the current applied state, and a leftover draft from a
/// previous open never survives.
/// </summary>
public static class FirstRunWizardDependencyInjection
{
    /// <summary>Register the first-run wizard view model.</summary>
    public static IServiceCollection AddFirstRunWizardServices(this IServiceCollection services) => services
        .AddTransient<FirstRunWizardViewModel>();
}
