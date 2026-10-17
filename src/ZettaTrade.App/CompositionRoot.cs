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

namespace ZettaTrade.App;

/// <summary>
/// DI composition root. Registers all services and dependencies.
/// </summary>
internal static class CompositionRoot
{
    public static IServiceProvider BuildServiceProvider()
    {
        // Configure Serilog
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("Avalonia", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.Console(
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(
                path: Path.Combine(ConfigPathResolver.LogDirectory, "zettatrade-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        var services = new ServiceCollection();

        // Logging
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddSerilog(Log.Logger, dispose: true);
        });

        // Core services
        services.AddSingleton<ISecretStore>(sp =>
            SecretStoreFactory.Create(sp.GetRequiredService<ILoggerFactory>()));

        services.AddSingleton<ISettingsService>(sp =>
            new SettingsService(
                ConfigPathResolver.MainSettingsFile,
                sp.GetRequiredService<ILogger<SettingsService>>()));

        // UI services
        services.AddSingleton<IThemeManager>(sp =>
            new ThemeManager(sp.GetRequiredService<ILogger<ThemeManager>>()));

        services.AddSingleton<ILocalizationManager>(sp =>
            new LocalizationManager(
                sp.GetRequiredService<ILogger<LocalizationManager>>(),
                ZettaTrade.UI.Resources.Strings.ResourceManager));

        // AI / MCP
        services.AddSingleton<IMcpClient, McpClientService>();
        services.AddSingleton<IMcpServerHost, McpServerHost>();
        
        // Risk management and confirmation
        services.AddSingleton<IRiskManager, DefaultRiskManager>();
        services.AddSingleton<IConfirmationService, UiConfirmationService>();

        // ViewModels
        services.AddTransient<MainViewModel>();
        
        // Register MCP tools after all services are built
        var serviceProvider = services.BuildServiceProvider();
        var mcpServer = serviceProvider.GetRequiredService<IMcpServerHost>();
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        
        // Note: Exchange adapters should be registered as plugins
        // For now, we'll register tools when an exchange is connected
        // TradingToolsRegistrar.RegisterAll(mcpServer, exchangeAdapter, riskManager, confirmationService, logger);

        // TODO: Register exchange adapters as plugins
        // services.AddSingleton<IExchangeAdapter, BinanceExchangeAdapter>();
        // services.AddSingleton<IExchangeAdapter, BybitExchangeAdapter>();

        return services.BuildServiceProvider();
    }
}
