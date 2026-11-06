namespace ZettaTrade.App;

using Microsoft.Extensions.DependencyInjection;
using ZettaTrade.App.Services;
using ZettaTrade.App.ViewModels;

/// <summary>
/// DI wiring for the order amend flow (T-075). The composition root calls
/// the single aggregate extension, so module-level registration details
/// stay local. The amend pipeline service is an app-lifetime singleton
/// (stateless besides its dependencies); the amend dialog view model is
/// transient (one instance per dialog open).
/// </summary>
public static class OrderAmendDependencyInjection
{
    /// <summary>Register the order amend flow service and the dialog view model.</summary>
    public static IServiceCollection AddOrderAmendServices(this IServiceCollection services) => services
        .AddSingleton<OrderAmendService>()
        .AddTransient<AmendOrderDialogViewModel>();
}
