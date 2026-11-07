namespace ZettaTrade.App;

using Microsoft.Extensions.DependencyInjection;
using ZettaTrade.App.Services;

/// <summary>
/// DI wiring for the order ticket's position sizing helper (T-079): the
/// round-size metadata seam and the snapshot-capturing sizing service the
/// ticket view model composes its quick-size row with. The row itself is
/// ticket-VM-local — the helper only PROPOSES quantities and never places
/// or alters an order, so no extra adapter capability or pipeline exists.
/// </summary>
public static class PositionSizerDependencyInjection
{
    /// <summary>
    /// Register the sizing takes: the paper surface's declared lot-step
    /// registry and the capture service (balances + reference close) the
    /// ticket consults only at capture moments.
    /// </summary>
    public static IServiceCollection AddPositionSizerServices(this IServiceCollection services) => services
        .AddSingleton<IInstrumentMetadataProvider, PaperInstrumentMetadataProvider>()
        .AddSingleton<PositionSizingService>();
}
