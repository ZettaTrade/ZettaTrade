namespace ZettaTrade.App;

using Microsoft.Extensions.DependencyInjection;
using ZettaTrade.App.ViewModels;

/// <summary>
/// DI wiring for the order history UI flow. The composition root calls the
/// single aggregate extension, so module-level registration details stay
/// local. The store itself is registered by Infrastructure's
/// <c>AddOrderHistory</c>; the view model is transient (every open reads
/// the database state).
/// </summary>
public static class HistoryUiDependencyInjection
{
    /// <summary>Register the order history view model.</summary>
    public static IServiceCollection AddHistoryUiServices(this IServiceCollection services) => services
        .AddTransient<OrderHistoryViewModel>();
}
