namespace ZettaTrade.App;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZettaTrade.App.Services;
using ZettaTrade.Infrastructure.SingleInstance;

/// <summary>
/// DI wiring for the single-instance handoff (T-104): the dispatch host
/// (the running instance's pipe server) and the apply worker (the
/// workspace-symbol route the handoffs land through). The host receives
/// its apply route through the composition lambda — every forwarded
/// delivery resolves the same walk-ready singleton, never a mid-walk
/// partial graph — and its pump is started by the app bootstrap after the
/// composition walk (a forwarded selection can never touch a half-built
/// dependency). The claim and the forward lane are the entry point's own
/// composition (they run before any container exists) and are not
/// registered here.
/// </summary>
public static class SingleInstanceDependencyInjection
{
    /// <summary>Register the handoff host and the apply worker.</summary>
    public static IServiceCollection AddSingleInstanceServices(this IServiceCollection services) => services
        .AddSingleton<HandoffApplyService>()
        .AddSingleton<SingleInstanceHost>(sp => new SingleInstanceHost(
            SingleInstanceNames.PipeName,
            (args, ct) => sp.GetRequiredService<HandoffApplyService>().ApplyAsync(args, ct),
            sp.GetRequiredService<ILogger<SingleInstanceHost>>()));
}
