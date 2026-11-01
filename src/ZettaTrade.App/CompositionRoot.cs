namespace ZettaTrade.App;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using ZettaTrade.AI.Mcp;
using ZettaTrade.AI.Mcp.Tools;
using ZettaTrade.App.Services;
using ZettaTrade.App.ViewModels;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Infrastructure.Configuration;
using ZettaTrade.Infrastructure.Exchange;
using ZettaTrade.Infrastructure.Persistence;
using ZettaTrade.Infrastructure.Secrets;
using ZettaTrade.Infrastructure.Shell;
using ZettaTrade.UI.Localization;
using ZettaTrade.UI.Themes;

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
            new ThemeManager(sp.GetRequiredService<ILogger<ThemeManager>>()));
        services.AddSingleton<IThemeManager>(sp => sp.GetRequiredService<ThemeManager>());

        services.AddSingleton<IShellUtils>(new ShellUtils());

        services.AddSingleton<ILocalizationManager>(sp =>
            new LocalizationManager(
                sp.GetRequiredService<ILogger<LocalizationManager>>(),
                ZettaTrade.UI.Resources.Strings.ResourceManager));

        // Exchange adapters: transport settings live inside each adapter module (Infrastructure).
        services.AddExchangeAdapters();

        // Active-exchange facade over the concrete adapters: every market
        // data consumer resolves one IMarketDataAdapter that forwards to
        // the exchange picked in the toolbar selector, and trading rides the
        // same context (paper over the facade). Typed + IAsyncInitializable:
        // the startup walk restores the persisted choice here — before any
        // consumer VM is constructed.
        services.AddActiveExchange();

        services.AddSingleton<IMcpClient, McpClientService>();
        services.AddSingleton<IMcpServerHost, McpServerHost>();

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
        services.AddSingleton<ChatViewModel>();
        services.AddSingleton<SettingsViewModel>(sp =>
            new SettingsViewModel(
                sp.GetRequiredService<ISettingsService>(),
                sp.GetRequiredService<IThemeManager>(),
                sp.GetRequiredService<ILocalizationManager>(),
                sp.GetRequiredService<IShellUtils>(),
                sp.GetRequiredService<ILogger<SettingsViewModel>>(),
                sp.GetRequiredService<ThemeManager>().LoadErrorsChanged));
        services.AddSingleton<ChartViewModel>(sp =>
            new ChartViewModel(
                sp.GetRequiredService<ActiveExchangeService>(),
                sp.GetRequiredService<SymbolListProvider>(),
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
