using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using ZettaTrade.App.ViewModels;
using ZettaTrade.App.Views;
using ZettaTrade.Infrastructure.Configuration;

namespace ZettaTrade.App;

public partial class App : Application
{
    /// <summary>
    /// Application-wide service provider.
    /// </summary>
    public static IServiceProvider Services { get; private set; } = null!;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Ensure config directories exist
        ConfigPathResolver.EnsureDirectoriesExist();

        // Build DI container
        Services = CompositionRoot.BuildServiceProvider();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainVm = Services.GetRequiredService<MainViewModel>();
            desktop.MainWindow = new MainWindow
            {
                DataContext = mainVm,
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
