namespace ZettaTrade.App;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZettaTrade.App.Services;
using ZettaTrade.App.ViewModels;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Infrastructure.Persistence;

/// <summary>
/// DI wiring for the position actions flows (T-077): the service that runs
/// close, partial close and reverse through the mandatory order pipeline,
/// the protective-levels pipeline that rewrites a position's SL/TP pair
/// (T-187), and the transient positions panel view model whose rows expose
/// the action surface — every panel open re-queries the adapters, so a
/// stale snapshot never leaks into a new open.
/// </summary>
public static class PositionActionsDependencyInjection
{
    /// <summary>
    /// Register the position actions service (stateless: every attempt
    /// re-reads the position from the <see cref="ITradingAdapter"/>), the
    /// protective-levels service (same stateless reading) and the panel view
    /// model that coordinates them; the protect dialog view model is
    /// transient (one instance per dialog open).
    /// </summary>
    public static IServiceCollection AddPositionActionsServices(this IServiceCollection services) => services
        .AddSingleton<PositionActionsService>()
        .AddSingleton<ProtectiveLevelsService>()
        .AddTransient<PositionsPanelViewModel>()
        .AddTransient<ProtectiveLevelsDialogViewModel>();
}
