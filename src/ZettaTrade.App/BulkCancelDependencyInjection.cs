namespace ZettaTrade.App;

using Microsoft.Extensions.DependencyInjection;
using ZettaTrade.App.Services;
using ZettaTrade.App.ViewModels;

/// <summary>
/// DI wiring for the bulk order management flow (T-076): the pass service
/// that confirms once and cancels every working order on the workspace's
/// active symbol (best effort per item), and the workspace-symbol snapshot
/// it scopes by. The snapshot subscribes the chart's symbol stream at
/// resolution — the same source the chat context rides — so the scope is
/// resolved from the standing workspace choice at click time, never from a
/// stale per-window copy. The positions panel view model (registered by the
/// position actions wiring) picks the pass service up through its
/// constructor.
/// </summary>
public static class BulkCancelDependencyInjection
{
    /// <summary>
    /// Register the workspace symbol snapshot and the bulk cancel pass
    /// service (stateless: every pass re-reads the orders from the
    /// <see cref="ZettaTrade.Core.Interfaces.ITradingAdapter"/>).
    /// </summary>
    public static IServiceCollection AddBulkCancelServices(this IServiceCollection services) => services
        .AddSingleton(sp => new WorkspaceSymbolService(
            sp.GetRequiredService<ChartViewModel>().SymbolSelections))
        .AddSingleton<BulkCancelService>();
}
