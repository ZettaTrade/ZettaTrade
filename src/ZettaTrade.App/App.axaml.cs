using System.Globalization;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZettaTrade.App.ViewModels;
using ZettaTrade.App.Views;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Infrastructure.Configuration;

namespace ZettaTrade.App;

/// <summary>
/// Main application class.
/// </summary>
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

    /// <inheritdoc/>
    public override async void OnFrameworkInitializationCompleted()
    {
        ConfigPathResolver.EnsureDirectoriesExist();

        Services = await CompositionRoot.BuildServiceProviderAsync();

        await ApplyStoredLanguageAsync();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainVm = Services.GetRequiredService<MainViewModel>();
            var mainWindow = new MainWindow
            {
                DataContext = mainVm,
            };
            desktop.MainWindow = mainWindow;

            // StartCore() shows MainWindow synchronously right after this callback returns.
            // With a settings file present, our async initialization suspends on the first
            // await, so the lifetime finds MainWindow still null and nothing shows the window
            // later. Window.Show() is idempotent for non-modal windows (Window.ShowCore),
            // making an explicit call safe in both sync and async cases.
            mainWindow.Show();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Apply the stored language before the main window is created, so the
    /// {loc:Localized} bindings emit their initial value in the chosen culture
    /// and the runtime switch survives an application restart.
    /// On the first run (no stored value) the OS culture is kept.
    /// </summary>
    private static async Task ApplyStoredLanguageAsync()
    {
        var language = await Services
            .GetRequiredService<ISettingsService>()
            .GetAsync(UiSettingsKeys.Section, UiSettingsKeys.Language, string.Empty);

        if (string.IsNullOrWhiteSpace(language))
        {
            return;
        }

        try
        {
            await Services.GetRequiredService<ILocalizationManager>().SetLanguageAsync(language);
        }
        catch (CultureNotFoundException ex)
        {
            // Invalid stored value: keep the OS culture instead of failing startup.
            Services.GetRequiredService<ILogger<App>>().LogWarning(
                ex,
                "Invalid stored language '{Language}'; keeping the OS culture",
                language);
        }
    }
}
