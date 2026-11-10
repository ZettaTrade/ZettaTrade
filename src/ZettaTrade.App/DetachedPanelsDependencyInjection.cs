namespace ZettaTrade.App.Services;

using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using ZettaTrade.App.Views;

/// <summary>
/// The DI entry the app uses to summon the detached-panel runtime (T-103):
/// the coordinator singleton plus its per-panel window factories — the
/// trivial chrome shells the coordinator fills with the panels' one control
/// instances.
/// </summary>
public static class DetachedPanelsDependencyInjection
{
    /// <summary>
    /// Registers the detached-panel coordinator (a singleton: the one
    /// authority of the detached state across the app lifetime) with the
    /// per-panel detached window factories. The coordinator is NOT an
    /// <c>IAsyncInitializable</c>: the main window's open drives the
    /// restore (the view lane — the panel slots — exists only there).
    /// </summary>
    /// <param name="services">The host service collection.</param>
    /// <returns>The same collection for chaining.</returns>
    public static IServiceCollection AddDetachedPanelsServices(
        this IServiceCollection services)
    {
        services.AddSingleton(new DetachedPanelHosts(
            new Dictionary<PanelDetachId, Func<Window>>
            {
                [PanelDetachId.Chart] = () => new DetachedChartWindow(),
                [PanelDetachId.Book] = () => new DetachedBookWindow(),
                [PanelDetachId.Assistant] = () => new DetachedAssistantWindow(),
            }));
        services.AddSingleton<IDetachedPanelCoordinator, DetachedPanelCoordinator>();
        return services;
    }
}
