namespace ZettaTrade.App;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using ZettaTrade.AI.Mcp;
using ZettaTrade.App.Services;
using ZettaTrade.App.ViewModels;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Infrastructure.Configuration;
using ZettaTrade.Infrastructure.Secrets;
using ZettaTrade.UI.Localization;
using ZettaTrade.UI.Themes;

/// <summary>
/// DI composition root. Registers all services and dependencies.
/// </summary>
internal static class CompositionRoot
{
    /// <summary>
    /// Build and configure the service provider.
    /// </summary>
    public static IServiceProvider BuildServiceProvider()
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

        services.AddSingleton<IThemeManager>(sp =>
            new ThemeManager(sp.GetRequiredService<ILogger<ThemeManager>>()));

        services.AddSingleton<ILocalizationManager>(sp =>
            new LocalizationManager(
                sp.GetRequiredService<ILogger<LocalizationManager>>(),
                ZettaTrade.UI.Resources.Strings.ResourceManager));

        services.AddSingleton<IMcpClient, McpClientService>();
        services.AddSingleton<IMcpServerHost, McpServerHost>();
        
        services.AddSingleton<IRiskManager, DefaultRiskManager>();
        services.AddSingleton<IConfirmationService, UiConfirmationService>();

        services.AddSingleton<MenuViewModel>();
        services.AddSingleton<StatusBarViewModel>(sp => 
            new StatusBarViewModel(sp.GetRequiredService<ISettingsService>()));
        services.AddSingleton<ChatViewModel>();
        services.AddSingleton<SettingsViewModel>(sp =>
            new SettingsViewModel(
                sp.GetRequiredService<ISettingsService>(),
                sp.GetRequiredService<IThemeManager>(),
                sp.GetRequiredService<ILocalizationManager>(),
                sp.GetRequiredService<ILogger<SettingsViewModel>>()));
        services.AddTransient<MainViewModel>();

        // Note: Exchange adapters should be registered as plugins
        // TradingToolsRegistrar.RegisterAll(mcpServer, exchangeAdapter, riskManager, confirmationService, logger);

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Initialize all services that implement IAsyncInitializable.
    /// Must be called after BuildServiceProvider and before using the application.
    /// </summary>
    public static async Task InitializeServicesAsync(IServiceProvider serviceProvider, CancellationToken ct = default)
    {
        var initializables = serviceProvider.GetServices<IAsyncInitializable>();
        
        foreach (var initializable in initializables)
        {
            await initializable.InitializeAsync(ct);
        }
    }
}
