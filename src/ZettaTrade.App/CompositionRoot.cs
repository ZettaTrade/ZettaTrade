namespace ZettaTrade.App;

using Livery.Contracts;
using Livery.Localization;
using Livery.Themes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using ZettaTrade.AI.Chat;
using ZettaTrade.AI.Mcp;
using ZettaTrade.AI.Mcp.Tools;
using ZettaTrade.App.Services;
using ZettaTrade.App.ViewModels;
using ZettaTrade.Charting;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Infrastructure.Configuration;
using ZettaTrade.Infrastructure.Exchange;
using ZettaTrade.Infrastructure.Indicators;
using ZettaTrade.Infrastructure.Persistence;
using ZettaTrade.Infrastructure.Secrets;
using ZettaTrade.Infrastructure.Shell;

/// <summary>
/// DI composition root. Registers all services and dependencies.
/// </summary>
internal static class CompositionRoot
{
    /// <summary>
    /// Build and configure the service provider, initializing all services
    /// that implement <see cref="IAsyncInitializable"/>.
    /// Initialization resolves instances by their registered service types,
    /// which is how App-layer registrations declare them.
    /// </summary>
    public static async Task<IServiceProvider> BuildServiceProviderAsync(CancellationToken ct = default)
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("Avalonia", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.File(
                path: Path.Combine(ConfigPathResolver.LogDirectory, "zettatrade-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        var services = new ServiceCollection();

        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddSerilog(Log.Logger, dispose: true);
        });

        services.AddSingleton<ISecretStore>(sp =>
            SecretStoreFactory.Create(sp.GetRequiredService<ILoggerFactory>()));

        services.AddSingleton<ISettingsService>(sp =>
            new SettingsService(
                ConfigPathResolver.MainSettingsFile,
                sp.GetRequiredService<ILogger<SettingsService>>()));

        // ThemeManager is registered both by its concrete type (the read-only
        // diagnostics surface — LoadErrors/LoadErrorsChanged — lives there,
        // not on the Core interface) and as the IThemeManager service.
        services.AddSingleton<ThemeManager>(sp =>
            new ThemeManager(
                sp.GetRequiredService<ILogger<ThemeManager>>(),
                ConfigPathResolver.ThemesDirectory));
        services.AddSingleton<IThemeManager>(sp => sp.GetRequiredService<ThemeManager>());

        services.AddSingleton<IShellUtils>(new ShellUtils());

        services.AddSingleton<ILocalizationManager>(sp =>
            new LocalizationManager(
                sp.GetRequiredService<ILogger<LocalizationManager>>(),
                ZettaTrade.UI.Resources.Strings.ResourceManager));

        // Exchange adapters: transport settings live inside each adapter module (Infrastructure).
        services.AddExchangeAdapters();

        // The indicator plugin seam: one catalog scans the plugins directory
        // for IIndicator assemblies (SMA ships as the first plugin; the App
        // carries no compile-time reference to any plugin).
        services.AddIndicators();

        // Active-exchange facade over the concrete adapters: every market
        // data consumer resolves one IMarketDataAdapter that forwards to
        // the exchange picked in the toolbar selector, and trading rides the
        // same context (paper over the facade). Typed + IAsyncInitializable:
        // the startup walk restores the persisted choice here — before any
        // consumer VM is constructed.
        services.AddActiveExchange();

        services.AddBalancesServices(); // T-081: balances panel (aggregation service + panel VM).

        // AI chat: the Ollama completion service (local models first) and its
        // named HttpClient; the workspace context rides the existing seams.
        services.AddAiServices();

        services.AddSingleton<IMcpClient>(sp =>
            new McpClientService(
                sp.GetRequiredService<ILogger<McpClientService>>(),
                sp.GetRequiredService<ISecretStore>()));
        services.AddSingleton<IMcpServerHost, McpServerHost>();

        // MCP host runner: the startup walk reads the enable choice and
        // starts the stdio server per it; the settings window's save applies
        // the flip live and the state stream renders the honest rows. Registered
        // AFTER the settings service (the walk initializes that first) and
        // BEFORE every consumer VM.
        services.AddSingleton<McpHostService>();

        // External MCP client manager (T-068): the startup walk reads the
        // persisted server list and connects the enabled entries (a dead
        // server pends behind the bounded grace, never the boot). Registered
        // AFTER the settings service (the walk initializes that first) and
        // BEFORE the settings VM, which saves through it and renders its
        // per-entry states. The typed registration walks the walk; the
        // interface aliased to the same instance.
        services.AddSingleton<McpClientManager>();
        services.AddSingleton<IMcpClientManager>(sp =>
            sp.GetRequiredService<McpClientManager>());

        services.AddSingleton<IRiskManager, DefaultRiskManager>();
        services.AddSingleton<IConfirmationService>(sp => new UiConfirmationService(
            sp.GetRequiredService<ILogger<UiConfirmationService>>(),
            sp.GetRequiredService<ILocalizationManager>()));
        services.AddSingleton<TradingToolsRegistrar>();

        services.AddSingleton<MenuViewModel>();
        services.AddSingleton<StatusBarViewModel>(sp =>
            new StatusBarViewModel(
                sp.GetRequiredService<IThemeManager>(),
                sp.GetRequiredService<ILocalizationManager>(),
                sp.GetRequiredService<ActiveExchangeService>(),
                AppVersion.Current,
                logger: sp.GetRequiredService<ILogger<StatusBarViewModel>>()));
        services.AddSingleton<ChatContextService>(sp =>
            new ChatContextService(
                sp.GetRequiredService<IMarketDataAdapter>(),
                sp.GetRequiredService<ChartViewModel>().SymbolSelections,
                sp.GetRequiredService<ChartViewModel>().TimeFrameSelections,
                sp.GetRequiredService<ILogger<ChatContextService>>()));
        services.AddSingleton<WorkspacePromptComposer>();
        services.AddSingleton<ChatViewModel>(sp =>
            new ChatViewModel(
                sp.GetRequiredService<ISettingsService>(),
                sp.GetRequiredService<IChatCompletion>(),
                sp.GetRequiredService<ChatContextService>(),
                sp.GetRequiredService<WorkspacePromptComposer>(),
                sp.GetRequiredService<ILocalizationManager>(),
                sp.GetRequiredService<ILogger<ChatViewModel>>()));
        // Workspace layout: the persisted column widths of the draggable
        // splitters. Registered AFTER ChatViewModel on purpose — the startup
        // walk runs registrations in order, and the layout restore must
        // follow the chat's visibility restore so the collapsed/visible
        // assistant geometry applies before any window opens (T-038).
        services.AddSingleton<WorkspaceLayoutViewModel>(sp =>
            new WorkspaceLayoutViewModel(
                sp.GetRequiredService<ISettingsService>(),
                sp.GetRequiredService<ChatViewModel>(),
                sp.GetRequiredService<ILogger<WorkspaceLayoutViewModel>>()));
        services.AddSingleton<SettingsViewModel>(sp =>
            new SettingsViewModel(
                sp.GetRequiredService<ISettingsService>(),
                sp.GetRequiredService<IThemeManager>(),
                sp.GetRequiredService<ILocalizationManager>(),
                sp.GetRequiredService<IShellUtils>(),
                sp.GetRequiredService<McpHostService>(),
                sp.GetRequiredService<ActiveExchangeService>(),
                sp.GetRequiredService<SymbolListProvider>(),
                sp.GetRequiredService<IIndicatorCatalog>(),
                sp.GetRequiredService<IMcpClientManager>(),
                sp.GetRequiredService<ILogger<SettingsViewModel>>(),
                sp.GetRequiredService<ThemeManager>().LoadErrorsChanged));
        services.AddSingleton<ChartViewModel>(sp =>
            new ChartViewModel(
                sp.GetRequiredService<ActiveExchangeService>(),
                sp.GetRequiredService<SymbolListProvider>(),
                sp.GetRequiredService<ISettingsService>(),
                sp.GetRequiredService<IIndicatorCatalog>(),
                sp.GetRequiredService<ILogger<ChartViewModel>>()));
        services.AddSingleton<OrderBookViewModel>(sp =>
            new OrderBookViewModel(
                sp.GetRequiredService<IMarketDataAdapter>(),
                sp.GetRequiredService<ChartViewModel>().SymbolSelections,
                sp.GetRequiredService<ILogger<OrderBookViewModel>>()));
        services.AddTransient<MainViewModel>(sp =>
            new MainViewModel(
                sp.GetRequiredService<MenuViewModel>(),
                sp.GetRequiredService<StatusBarViewModel>(),
                sp.GetRequiredService<ChatViewModel>(),
                sp.GetRequiredService<SettingsViewModel>(),
                sp.GetRequiredService<ChartViewModel>(),
                sp.GetRequiredService<OrderBookViewModel>(),
                sp.GetRequiredService<WorkspaceLayoutViewModel>(),
                sp.GetRequiredService<ActiveExchangeService>(),
                sp.GetRequiredService<ILogger<MainViewModel>>()));
        services.AddTradingUiServices();
        services.AddOrderHistory();
        services.AddHistoryUiServices();

        var provider = services.BuildServiceProvider();

        await InitializeRegisteredServicesAsync(provider, services, ct);

        // MCP trading tools: registered once, against the paper trading adapter
        // (mock surface; real exchange adapters are plugged in as plugins later).
        // Registration runs AFTER the startup walk: resolving ITradingAdapter
        // constructs the whole adapter stack (PaperTradingAdapter wraps the
        // market data adapter), and the Binance adapter reads its endpoints
        // from the endpoint registry at construction time — the walk must
        // have loaded the settings file first, or every adapter would
        // silently run on the shipped production endpoints.
        provider.GetRequiredService<TradingToolsRegistrar>().RegisterAll(
            provider.GetRequiredService<IMcpServerHost>(),
            provider.GetRequiredService<ITradingAdapter>(),
            provider.GetRequiredService<IRiskManager>(),
            provider.GetRequiredService<IConfirmationService>(),
            provider.GetRequiredService<ILocalizationManager>());

        return provider;
    }

    /// <summary>
    /// Initialize every registered service instance implementing
    /// <see cref="IAsyncInitializable"/>, in registration order, once per instance.
    /// </summary>
    private static async Task InitializeRegisteredServicesAsync(
        IServiceProvider provider,
        IServiceCollection registrations,
        CancellationToken ct)
    {
        var initialized = new HashSet<object>(ReferenceEqualityComparer.Instance);

        foreach (var descriptor in registrations)
        {
            if (!IsStartupWalkCandidate(descriptor))
            {
                continue;
            }

            if (provider.GetService(descriptor.ServiceType) is not IAsyncInitializable service)
            {
                continue;
            }

            if (!initialized.Add(service))
            {
                continue;
            }

            await service.InitializeAsync(ct);
        }
    }

    /// <summary>
    /// Static (resolution-free) screen for the startup walk: decides whether
    /// a registration descriptor can host an <see cref="IAsyncInitializable"/>
    /// service before anything is resolved.
    /// </summary>
    /// <remarks>
    /// Typed registrations are inspected through static interface
    /// assignability: a <see cref="System.Type"/> object never pattern-matches
    /// a service interface, so a naive `type is IAsyncInitializable` check
    /// silently skipped every typed registration (the endpoint registry could
    /// never initialize). Factory registrations carry no implementation hint
    /// and are inspected dynamically by the walk; instance registrations
    /// carry the instance itself. Open-generic registrations (e.g. Options)
    /// cannot be resolved directly and are never candidates.
    /// </remarks>
    internal static bool IsStartupWalkCandidate(ServiceDescriptor descriptor)
    {
        if (descriptor.ImplementationType is { } typed &&
            !typeof(IAsyncInitializable).IsAssignableFrom(typed))
        {
            return false;
        }

        if (descriptor.ImplementationInstance is { } instance &&
            instance is not IAsyncInitializable)
        {
            return false;
        }

        if (descriptor.ServiceType.IsGenericType && !descriptor.ServiceType.IsConstructedGenericType)
        {
            return false;
        }

        return true;
    }
}

