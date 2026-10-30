namespace ZettaTrade.App.ViewModels;

using System.Globalization;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// ViewModel for the settings window (theme, language, etc.).
/// Delegates available options to ThemeManager and LocalizationManager (DRY, SoC).
/// Changes take effect only on the explicit <see cref="SaveAsync"/> call: the
/// window starts from selections synced to the applied state
/// (<see cref="ResetSelections"/>) and only a real difference is applied and
/// persisted, so closing without saving never leaves a half-applied state.
/// </summary>
public sealed class SettingsViewModel : ReactiveObject
{
    #region Constants

    /// <summary>Combo box index meaning "nothing selected".</summary>
    private const int NoSelection = -1;

    #endregion Constants

    #region Properties

    private int _selectedThemeIndex = NoSelection;
    private int _selectedLanguageIndex = NoSelection;

    /// <summary>Selected theme index (combo box); <see cref="NoSelection"/> when nothing is selected.</summary>
    public int SelectedThemeIndex
    {
        get => _selectedThemeIndex;
        set => this.RaiseAndSetIfChanged(ref _selectedThemeIndex, value);
    }

    /// <summary>Selected language index (combo box); <see cref="NoSelection"/> when nothing is selected.</summary>
    public int SelectedLanguageIndex
    {
        get => _selectedLanguageIndex;
        set => this.RaiseAndSetIfChanged(ref _selectedLanguageIndex, value);
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

        ResetSelections();
    }

    #endregion Constructor

    #region Public Methods

    /// <summary>
    /// Sync the selections with the currently applied theme and language.
    /// The ViewModel is an app-lifetime singleton, so the window refreshes the
    /// selections on every open; otherwise an abandoned (unsaved) selection
    /// would show up as already-applied state on the next open.
    /// </summary>
    public void ResetSelections()
    {
        SelectedThemeIndex = IndexOfAppliedTheme();
        SelectedLanguageIndex = IndexOfAppliedLanguage();
    }

    /// <summary>
    /// Apply and persist the selected theme and language. A selection equal to
    /// the applied state is skipped (no redundant events, no settings rewrite).
    /// </summary>
    public async Task SaveAsync()
    {
        var theme = PickAt(_themeManager.AvailableThemes, _selectedThemeIndex);
        if (theme is not null && !IsThemeApplied(theme))
        {
            await _themeManager.ApplyThemeAsync(theme.Name);
            await _settings.SetAsync(
                UiSettingsKeys.Section,
                UiSettingsKeys.Theme,
                theme.Name);
        }

        var language = PickAt(_localization.AvailableLanguages, _selectedLanguageIndex);
        if (language is not null && !IsLanguageApplied(language))
        {
            await _localization.SetLanguageAsync(language.Code);
            await _settings.SetAsync(
                UiSettingsKeys.Section,
                UiSettingsKeys.Language,
                language.Code);
        }
    }

    #endregion Public Methods

    #region Private Methods

    /// <summary>Index of the applied theme (matched by display name) in AvailableThemes.</summary>
    private int IndexOfAppliedTheme()
    {
        var themes = _themeManager.AvailableThemes;
        for (var i = 0; i < themes.Count; i++)
        {
            if (IsThemeApplied(themes[i]))
            {
                return i;
            }
        }

        _logger.LogWarning("Applied theme {Theme} is not among available themes", _themeManager.CurrentTheme);
        return NoSelection;
    }

    /// <summary>Index of the applied language in AvailableLanguages: exact code, then two-letter fallback ("ru-RU" -&gt; "ru").</summary>
    private int IndexOfAppliedLanguage()
    {
        var languages = _localization.AvailableLanguages;
        var current = _localization.CurrentLanguage;
        for (var i = 0; i < languages.Count; i++)
        {
            if (IsLanguageApplied(languages[i]))
            {
                return i;
            }
        }

        try
        {
            var shortCode = new CultureInfo(current).TwoLetterISOLanguageName;
            for (var i = 0; i < languages.Count; i++)
            {
                if (languages[i].Code.Equals(shortCode, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
        }
        catch (CultureNotFoundException ex)
        {
            _logger.LogWarning(ex, "Unknown current culture {Language}", current);
        }

        _logger.LogWarning("Applied language {Language} is not among available languages", current);
        return NoSelection;
    }

    private bool IsThemeApplied(ThemeInfo theme) =>
        _themeManager.CurrentTheme.Equals(theme.DisplayName, StringComparison.OrdinalIgnoreCase);

    private bool IsLanguageApplied(LanguageInfo language) =>
        _localization.CurrentLanguage.Equals(language.Code, StringComparison.OrdinalIgnoreCase);

    private static T? PickAt<T>(IReadOnlyList<T> items, int index)
        where T : class
    {
        return index >= 0 && index < items.Count ? items[index] : null;
    }

    #endregion Private Methods
}
