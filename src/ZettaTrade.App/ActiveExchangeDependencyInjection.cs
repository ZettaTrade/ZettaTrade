namespace ZettaTrade.App;

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZettaTrade.App.Services;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Infrastructure.Demo;
using ZettaTrade.Infrastructure.Exchange;

/// <summary>
/// DI wiring for the active-exchange facade. Concrete exchange adapters are
/// registered by type by the infrastructure aggregate; this composition
/// collects them into one <see cref="ActiveExchangeService"/>, owns the
/// single <see cref="IMarketDataAdapter"/> entry point (the facade itself)
/// and registers the paper trading adapter over it, so trading follows the
/// same context as market data. Core contracts stay untouched.
/// </summary>
public static class ActiveExchangeDependencyInjection
{
    /// <summary>
    /// The harness adapter allowlist (T-141): when this variable carries a
    /// comma-separated adapter id list, the facade's registered catalog
    /// keeps ONLY the named adapters. The soak's network-free lane needs it:
    /// the palette warm-up and the balances aggregation walk the facade's
    /// <c>Registered</c> catalog and fetch every entry's symbol catalog at
    /// startup — a demo-only catalog makes zero real-exchange calls, so the
    /// demo pin alone (the persisted choice) could not hold the acceptance.
    /// Unset, empty and whitespace-only values keep the full catalog. An
    /// unknown id is a composition refusal, never a silent substitution
    /// (trading safety).
    /// </summary>
    public const string AdapterAllowlistEnvironmentVariable = "ZETTATRADE_ADAPTERS";

    /// <summary>
    /// The allowlist rule itself, driven with the exact raw values an
    /// environment could carry: null, empty and whitespace-only keep the
    /// given catalog; a real list picks the named adapters case-insensitively
    /// and refuses unknown ids by throwing.
    /// </summary>
    internal static IReadOnlyList<TAdapter> ApplyAdapterAllowlist<TAdapter>(
        string? allowlistValue,
        IReadOnlyList<TAdapter> catalog)
        where TAdapter : ZettaTrade.Core.Interfaces.IMarketDataAdapter
    {
        if (string.IsNullOrWhiteSpace(allowlistValue))
        {
            return catalog;
        }

        var wanted = allowlistValue.Split(
            ',',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var missing = wanted
            .Where(id => !catalog.Any(adapter =>
                string.Equals(adapter.Id, id, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"{AdapterAllowlistEnvironmentVariable} names unregistered adapter(s): {string.Join(", ", missing)}");
        }

        return catalog
            .Where(adapter => wanted.Any(id =>
                string.Equals(adapter.Id, id, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    /// <summary>
    /// Compose the active-exchange stack: persisted selection + atomic
    /// stream switching through the facade, trading included. The demo lane
    /// is the FIRST registered adapter, so the shipped default (no persisted
    /// choice yet — a fresh install, i.e. exactly the first-run wizard's
    /// audience) is the canned-data demo, and nothing real is touched
    /// without an explicit pick (T-099); an existing install restores its
    /// own persisted id regardless of the order.
    /// </summary>
    public static IServiceCollection AddActiveExchange(this IServiceCollection services) => services
        .AddSingleton<ActiveExchangeService>(sp =>
        {
            IReadOnlyList<IMarketDataAdapter> catalog =
            [
                sp.GetRequiredService<DemoMarketDataAdapter>(),
                sp.GetRequiredService<BinanceAdapter>(),
                sp.GetRequiredService<BybitAdapter>(),
                sp.GetRequiredService<OkxAdapter>(),
                sp.GetRequiredService<AlpacaAdapter>(),
            ];
            return new ActiveExchangeService(
                ApplyAdapterAllowlist(
                    Environment.GetEnvironmentVariable(AdapterAllowlistEnvironmentVariable),
                    catalog),
                sp.GetRequiredService<ISettingsService>(),
                sp.GetRequiredService<ILogger<ActiveExchangeService>>());
        })
        .AddSingleton<IMarketDataAdapter>(sp => sp.GetRequiredService<ActiveExchangeService>())
        .AddPaperTrading();
}
