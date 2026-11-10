using Microsoft.Extensions.DependencyInjection;
using ZettaTrade.Plugins.Abstractions;
using ZettaTrade.Plugins.Abstractions.Extensions;

namespace ZettaTrade.Plugins.Fixtures;

/// <summary>The start-order recorder the chain goldens read (T-131): each chain member appends its id at its one-time start.</summary>
public static class ChainStartOrder
{
    /// <summary>The ids in the order the loader started them.</summary>
    public static readonly List<string> Started = [];
}

/// <summary>
/// The valid sample (T-131): a complete out-of-tree plugin — manifest, a
/// panel, a toolbar item and a settings section. The loader goldens copy
/// this plugin's dll into a temp scan directory and award it a load; the
/// registry carries everything this class registered.
/// </summary>
[ZettaTradePlugin("fx.sample", "SDK Fixture Sample", "1.2.3")]
public sealed class SamplePanelPlugin : IZettaTradePlugin
{
    /// <summary>How many times the loader ran the one-time start — the replay guard.</summary>
    public static int InitializeCalls;

    /// <summary>The context the last start received — the services-window evidence.</summary>
    public static IPluginContext? LastContext;

    /// <inheritdoc/>
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton(new PluginPanelDescriptor(
            "panel-a",
            "Fx.Sample.PanelTitle",
            _ => new object()));
        services.AddSingleton(new PluginToolbarItemDescriptor("tool-a", "Fx.Sample.ToolTitle"));
        services.AddSingleton(new PluginSettingsSectionDescriptor("section-a", "Fx.Sample.SectionTitle"));
    }

    /// <inheritdoc/>
    public Task InitializeAsync(
        IPluginContext pluginContext,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref InitializeCalls);
        LastContext = pluginContext;
        return Task.CompletedTask;
    }
}

/// <summary>
/// The dependency chain head (T-131): <c>fx.alpha</c> depends on
/// <c>fx.beta</c>; the loader must start beta first regardless of the
/// scan's file order.
/// </summary>
[ZettaTradePlugin("fx.alpha", "SDK Fixture Alpha", "1.0.0", "fx.beta")]
public sealed class AlphaPlugin : IZettaTradePlugin
{
    /// <inheritdoc/>
    public void ConfigureServices(IServiceCollection services)
    {
    }

    /// <inheritdoc/>
    public Task InitializeAsync(
        IPluginContext pluginContext,
        CancellationToken cancellationToken)
    {
        ChainStartOrder.Started.Add("fx.alpha");
        return Task.CompletedTask;
    }
}

/// <summary>The chain's tail (T-131): no dependencies (alpha starts after it).</summary>
[ZettaTradePlugin("fx.beta", "SDK Fixture Beta", "1.0.0")]
public sealed class BetaPlugin : IZettaTradePlugin
{
    /// <inheritdoc/>
    public void ConfigureServices(IServiceCollection services)
    {
    }

    /// <inheritdoc/>
    public Task InitializeAsync(
        IPluginContext pluginContext,
        CancellationToken cancellationToken)
    {
        ChainStartOrder.Started.Add("fx.beta");
        return Task.CompletedTask;
    }
}
