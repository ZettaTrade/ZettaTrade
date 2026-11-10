using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Livery.Localization;
using Livery.Themes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZettaTrade.App.Services;
using ZettaTrade.App.ViewModels;
using ZettaTrade.App.Views;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Infrastructure.Configuration;
using ZettaTrade.Infrastructure.SingleInstance;

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

            // The global crash guard (T-101): both process hooks (the
            // runtime's terminating leg and the UI dispatcher's fatal leg)
            // attach right after the container builds, so every later
            // failure lands as a logged, dialog-informed crash — never a
            // silent death. The guard is LAST-INFORM: it logs + composes +
            // presents the dialog once and returns; it never suppresses,
            // never marks anything handled, never writes state — the
            // platform's own exit follows it exactly as before.
            Services.GetRequiredService<CrashGuard>().Attach();

            await ApplyStoredUiSettingsAsync();

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                // The multi-window workspace (T-103): the main window closing
                // IS the app shutdown (the detached panel windows follow it),
                // and the shutdown arm makes the closing panel windows quiet —
                // they return their content but never rewrite the stored
                // record, so the detached layout stands exactly as the user
                // left it for the next session.
                desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
                desktop.ShutdownRequested += (_, _) => Services
                    .GetRequiredService<IDetachedPanelCoordinator>()
                    .MarkShutdownRequested();

                // The single-instance handoff (T-104): the dispatch pipe
                // opens only now — after the composition walk — so a
                // forwarded selection can never touch a half-initialized
                // dependency, and the app's own launch arguments ride the
                // same validated route a forwarded start does (the grammar
                // refusal, the semantic refusal and the applied shapes all
                // settle into file-log receipts before the first frame).
                await RunSingleInstanceHandoffAsync(desktop);

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

                // The first-run disclaimer: a fresh (or externally reset)
                // acknowledgment shows the dialog once per wording version.
                // An explicit acknowledge persists the flag, a plain
                // dismissal leaves it unset (the next launch re-shows), and
                // the flag itself never gates any trading action — it only
                // re-shows the wording (the risk and confirmation flows stay
                // untouched).
                await ShowFirstRunDisclaimerAsync(mainWindow);

                // The first-run wizard (T-099): after the disclaimer wording
                // showed (both first-run surfaces ride one launch path), the
                // wizard shows ONCE — the persisted flag decides, any exit
                // marks it, and the re-run entry lives in the settings window.
                await ShowFirstRunWizardAsync(mainWindow);
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

    /// <summary>
    /// The single-instance handoff step: the dispatch host starts serving
    /// its pipe (after the composition walk, so a forwarded selection can
    /// never touch a half-built dependency) and the app's own launch
    /// arguments ride the same validated route a forwarded start does —
    /// the grammar refusal, the semantic refusal and the applied shapes all
    /// settle into file-log receipts before the window opens.
    /// </summary>
    private static async Task RunSingleInstanceHandoffAsync(
        IClassicDesktopStyleApplicationLifetime desktop)
    {
        var host = Services.GetRequiredService<SingleInstanceHost>();
        host.Start();
        await host.ProcessAsync(desktop.Args ?? [], CancellationToken.None);
    }

    /// <summary>
    /// Show the first-run disclaimer dialog when the persisted acknowledgment
    /// does not cover the shipped wording. A plain dismissal leaves the flag
    /// unset (the next launch re-shows); the flag itself never gates any
    /// trading action — it only re-shows the wording (the risk and
    /// confirmation flows stay untouched).
    /// A failure here is logged and non-fatal: the wording shows again on
    /// the next launch instead of failing the startup.
    /// </summary>
    private static async Task ShowFirstRunDisclaimerAsync(MainWindow owner)
    {
        try
        {
            var settings = Services.GetRequiredService<ISettingsService>();
            var acknowledged = await DisclaimerAcknowledgment.ReadAcknowledgedVersionAsync(settings);
            if (DisclaimerAcknowledgment.CoversCurrent(acknowledged))
            {
                return;
            }

            var dialog = new DisclaimerDialogWindow
            {
                DataContext = Services.GetRequiredService<DisclaimerDialogViewModel>(),
            };
            await dialog.ShowDialog(owner);
        }
        catch (Exception ex)
        {
            Services.GetRequiredService<ILogger<App>>().LogError(
                ex,
                "Failed to show the first-run disclaimer dialog; the wording shows again on the next launch");
        }
    }

    /// <summary>
    /// Show the first-run wizard when the persisted once-flag does not
    /// exist: a fresh install officially announces the setup. Every exit —
    /// finish, skip or a plain window close — marks the flag, because the
    /// wizard announced itself exactly once and must never re-announce
    /// afterwards (skipping leaves the demo-lane defaults standing).
    /// A failure here is logged and non-fatal: a launch that failed to show
    /// the wizard never marks the flag, so the next launch shows it instead.
    /// </summary>
    private static async Task ShowFirstRunWizardAsync(MainWindow owner)
    {
        try
        {
            var settings = Services.GetRequiredService<ISettingsService>();
            if (!await FirstRunWizardState.ShouldBeShownAsync(settings))
            {
                return;
            }

            // The window shows after the startup walk and the disclaimer:
            // the main window is already up, nothing service construction
            // pends behind the dialog.
            var dialog = new FirstRunWizardWindow
            {
                DataContext = Services.GetRequiredService<FirstRunWizardViewModel>(),
            };
            await dialog.ShowDialog(owner);

            // ANY exit lands the once-flag exactly here: the wizard showed
            // and every path out of it (the finish, the skip affordance and
            // the system close included) is an announcement consumed.
            await FirstRunWizardState.MarkShownAsync(settings);
        }
        catch (Exception ex)
        {
            Services.GetRequiredService<ILogger<App>>().LogError(
                ex,
                "Failed to show the first-run wizard; it shows again on the next launch");
        }
    }
}

