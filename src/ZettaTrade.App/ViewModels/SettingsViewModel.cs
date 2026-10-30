namespace ZettaTrade.App.ViewModels;

using System.Reactive.Linq;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// ViewModel for the settings window (theme, language, etc.).
/// Delegates available options to ThemeManager and LocalizationManager (DRY, SoC)
/// and persists every applied change through <see cref="ISettingsService"/>.
/// Selecting a theme or language applies it immediately (live preview); app
/// startup applies the stored preferences before this ViewModel is created,
/// so the selection starts in the state that is already on screen.
/// </summary>
public sealed class SettingsViewModel : ReactiveObject
{
    #region Properties

    private ThemeInfo? _selectedThemeItem;
    private LanguageInfo? _selectedLanguageItem;

    /// <summary>Selected theme (null before initialization); a new selection re-colors the app.</summary>
    public ThemeInfo? SelectedThemeItem
    {
        get => _selectedThemeItem;
        set => this.RaiseAndSetIfChanged(ref _selectedThemeItem, value);
    }

    /// <summary>Selected language (null before initialization); a new selection re-localizes the app.</summary>
    public LanguageInfo? SelectedLanguageItem
    {
        get => _selectedLanguageItem;
        set => this.RaiseAndSetIfChanged(ref _selectedLanguageItem, value);
    }

    /// <summary>Available themes from ThemeManager (no duplication).</summary>
    public IReadOnlyList<ThemeInfo> AvailableThemes => _themeManager.AvailableThemes;

    /// <summary>Available languages from LocalizationManager (no duplication).</summary>
    public IReadOnlyList<LanguageInfo> AvailableLanguages => _localization.AvailableLanguages;

    #endregion Properties

    #region Private Fields

    private readonly ISettingsService _settings;
    private readonly IThemeManager _themeManager;
    private readonly ILocalizationManager _localization;
    private readonly ILogger<SettingsViewModel> _logger;

    #endregion Private Fields

    #region Constructor

    /// <summary>
    /// Create a new SettingsViewModel instance. The managers must already be
    /// initialized (app startup applies the stored preferences before the
    /// settings window can open), so their current state defines the selection.
    /// </summary>
    public SettingsViewModel(
        ISettingsService settings,
        IThemeManager themeManager,
        ILocalizationManager localization,
        ILogger<SettingsViewModel> logger)
    {
        _settings = settings;
        _themeManager = themeManager;
        _localization = localization;
        _logger = logger;

        // Every selection change applies the picked item. Concat serializes
        // back-to-back changes; the apply methods treat an unchanged selection
        // as a no-op, so re-running the pipeline cannot rewrite settings.
        this.WhenAnyValue(x => x.SelectedThemeItem)
            .Where(static theme => theme is not null)
            .Select(theme => Observable.FromAsync(() => ApplyThemeAsync(theme!)))
            .Concat()
            .Subscribe(_ => { }, ex => _logger.LogError(ex, "Theme selection pipeline failed"));

        this.WhenAnyValue(x => x.SelectedLanguageItem)
            .Where(static language => language is not null)
            .Select(language => Observable.FromAsync(() => ApplyLanguageAsync(language!)))
            .Concat()
            .Subscribe(_ => { }, ex => _logger.LogError(ex, "Language selection pipeline failed"));

        SelectedThemeItem = _themeManager.AvailableThemes.FirstOrDefault(theme =>
            theme.DisplayName.Equals(_themeManager.CurrentTheme, StringComparison.OrdinalIgnoreCase));
        SelectedLanguageItem = _localization.AvailableLanguages.FirstOrDefault(language =>
            language.Code.Equals(_localization.CurrentLanguage, StringComparison.OrdinalIgnoreCase));
    }

    #endregion Constructor

    #region Private Methods

    private async Task ApplyThemeAsync(ThemeInfo theme)
    {
        try
        {
            // The selection is initialized from the applied theme, so an
            // identical selection is already on screen and stays a no-op.
            if (_themeManager.CurrentTheme.Equals(theme.DisplayName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            await _themeManager.ApplyThemeAsync(theme.Name);
            await _settings.SetAsync(
                UiSettingsKeys.Section,
                UiSettingsKeys.Theme,
                theme.Name);
        }
        catch (Exception ex)
        {
            // The pipeline must survive a failed apply and keep accepting changes.
            _logger.LogError(ex, "Failed to apply theme {Theme}", theme.DisplayName);
        }
    }

    private async Task ApplyLanguageAsync(LanguageInfo language)
    {
        try
        {
            if (_localization.CurrentLanguage.Equals(language.Code, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            await _localization.SetLanguageAsync(language.Code);
            await _settings.SetAsync(
                UiSettingsKeys.Section,
                UiSettingsKeys.Language,
                language.Code);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply language {Language}", language.Code);
        }
    }

    #endregion Private Methods
}
