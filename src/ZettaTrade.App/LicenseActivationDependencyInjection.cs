namespace ZettaTrade.App;

using Microsoft.Extensions.DependencyInjection;
using ZettaTrade.App.ViewModels;

/// <summary>
/// DI wiring for the license activation flow (T-132). The view model is
/// transient: every dialog open starts fresh — no plugin id, no pasted
/// key, no verdict carries over from the previous open.
/// </summary>
public static class LicenseActivationDependencyInjection
{
    /// <summary>Register the license activation view model.</summary>
    public static IServiceCollection AddLicenseActivationServices(this IServiceCollection services) => services
        .AddTransient<LicenseActivationViewModel>();
}
