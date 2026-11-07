namespace ZettaTrade.App;

using Microsoft.Extensions.DependencyInjection;
using ZettaTrade.App.Services;
using ZettaTrade.App.ViewModels;

/// <summary>
/// DI wiring for the trading UI flows. The composition root calls the single
/// aggregate extension, so module-level registration details stay local.
/// </summary>
public static class TradingUiDependencyInjection
{
    /// <summary>Register the trading UI services and dialog view models. The positions panel rides the position actions module's registration since its actions moved there (T-077).</summary>
    public static IServiceCollection AddTradingUiServices(this IServiceCollection services) =>
        services
            .AddSingleton<IOrderJournal, OrderJournal>()
            .AddSingleton<OrderConfirmationComposer>()
            .AddSingleton<OrderConfirmPolicy>()
            .AddSingleton<SymbolListProvider>()
            .AddTransient<NewOrderDialogViewModel>();
}
