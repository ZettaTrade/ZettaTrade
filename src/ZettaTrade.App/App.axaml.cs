using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Livery.Localization;
using Livery.Themes;
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
        try
        {
            ConfigPathResolver.EnsureDirectoriesExist();

            Services = await CompositionRoot.BuildServiceProviderAsync();

            await ApplyStoredUiSettingsAsync();

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
        catch (Exception ex)
        {
            // BUG-019: OnFrameworkInitializationCompleted is async void, so an
            // unhandled exception here crashes the process with nothing in
            // the Serilog file log. The failure is logged through the Serilog
            // static logger (configured as the composition root's first step)
            // and the rethrow IS the fail-fast path: the process must not
            // limp on with a half-built service tree and no main window.
            LogStartupFailure(ex);
            throw;
        }
    }

    /// <summary>
    /// Write the startup failure into the file log. Existence of the entry
    /// is guaranteed for every failure AFTER the composition root has
    /// configured <see cref="Serilog.Log.Logger"/> (the store/theme corrupt
    /// cases resolve in its initialization walk); only the directory-setup
    /// step before them is unloggable by that design, because the log
    /// directory itself may be the failed resource and the fail-fast exit
    /// already surfaces it as the process error.
    /// </summary>
    internal static void LogStartupFailure(Exception exception) =>
        Serilog.Log.Logger
            .ForContext<App>()
            .Error(
                exception,
                "Application startup failed; the process exits before the main window exists");

    /// <summary>
    /// Apply the stored UI preferences (language, theme) before the main window
    /// is created, so the {loc:Localized} bindings emit their initial value in
    /// the chosen culture and the window is colored by the chosen theme from its
    /// first frame; this also makes both survive an application restart.
    /// The theme manager must already be initialized (its custom themes loaded),
    /// which the composition root does while building the service provider.
    /// On the first run (no stored language) the OS culture is kept.
    /// </summary>
    private static async Task ApplyStoredUiSettingsAsync()
    {
        var settings = Services.GetRequiredService<ISettingsService>();

        await ApplyStoredLanguageAsync(settings);
        await ApplyStoredThemeAsync(settings);
    }

    /// <summary>
    /// Apply the language stored in the UI settings section.
    /// On the first run (no stored value) the OS culture is kept.
    /// </summary>
    private static async Task ApplyStoredLanguageAsync(ISettingsService settings)
    {
        var language = await settings.GetAsync(
            UiSettingsKeys.Section,
            UiSettingsKeys.Language,
            string.Empty);

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

    /// <summary>
    /// Apply the theme stored in the UI settings section, falling back to the
    /// canonical default theme when no value is stored. Stored values are
    /// resolved to the canonical theme Name here: Name is the single
    /// identifier, but builds before canonical identity stored display names
    /// ("Dark"), so a Name match is tried first and a display-name match is
    /// the upgrade fallback. An unknown value is logged and leaves the
    /// startup theme unchanged.
    /// </summary>
    private static async Task ApplyStoredThemeAsync(ISettingsService settings)
    {
        var stored = await settings.GetAsync(
            UiSettingsKeys.Section,
            UiSettingsKeys.Theme,
            UiSettingsKeys.DefaultTheme);

        if (string.IsNullOrWhiteSpace(stored))
        {
            return;
        }

        var themeManager = Services.GetRequiredService<IThemeManager>();
        var themes = themeManager.AvailableThemes;
        var match = themes.FirstOrDefault(t =>
                        t.Name.Equals(stored, StringComparison.OrdinalIgnoreCase)) ??
                    themes.FirstOrDefault(t =>
                        t.DisplayName.Equals(stored, StringComparison.OrdinalIgnoreCase));

        if (match is null)
        {
            Services.GetRequiredService<ILogger<App>>().LogWarning(
                "Unknown stored theme '{Theme}'; keeping the startup theme",
                stored);
            return;
        }

        // The theme manager matches canonical Names only.
        await themeManager.ApplyThemeAsync(match.Name);
    }
}

