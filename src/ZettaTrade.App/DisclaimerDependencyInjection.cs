namespace ZettaTrade.App;

using Microsoft.Extensions.DependencyInjection;
using ZettaTrade.App.ViewModels;

/// <summary>
/// DI wiring for the first-run disclaimer flow. The view model is transient:
/// every dialog open (the first run or the Help menu re-show) starts fresh,
/// and the stateless acknowledge write never carries a leftover from the
/// previous open.
/// </summary>
public static class DisclaimerDependencyInjection
{
    /// <summary>Register the disclaimer dialog view model.</summary>
    public static IServiceCollection AddDisclaimerServices(this IServiceCollection services) => services
        .AddTransient<DisclaimerDialogViewModel>();
}
