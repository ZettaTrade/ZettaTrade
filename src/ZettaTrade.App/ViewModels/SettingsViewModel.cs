namespace ZettaTrade.App.ViewModels;

using System.Reactive.Linq;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ReactiveUI.Primitives;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// ViewModel for settings management (theme, language, etc.).
/// Delegates available options to ThemeManager and LocalizationManager (DRY, SoC).
/// </summary>
public sealed class SettingsViewModel : ReactiveObject, IActivatableViewModel
{
    #region Properties

    public ViewModelActivator Activator { get; } = new();

    private string _selectedTheme = "Dark";
    private string _selectedLanguage = "en";

    /// <summary>Currently selected theme name.</summary>
    public string SelectedTheme
    {
        get => _selectedTheme;
        set => this.RaiseAndSetIfChanged(ref _selectedTheme, value);
    }

    /// <summary>Currently selected language code.</summary>
    public string SelectedLanguage
    {
        get => _selectedLanguage;
        set => this.RaiseAndSetIfChanged(ref _selectedLanguage, value);
    }

    /// <summary>Available themes from ThemeManager (no duplication).</summary>
    public IReadOnlyList<ThemeInfo> AvailableThemes => _themeManager.AvailableThemes;

    /// <summary>Available languages from LocalizationManager (no duplication).</summary>
    public IReadOnlyList<LanguageInfo> AvailableLanguages => _localization.AvailableLanguages;

    #endregion Properties

    #region Commands

    /// <summary>Command to apply a theme by name.</summary>
    public ReactiveCommand<string, RxVoid> ApplyThemeCommand { get; }

    /// <summary>Command to apply a language by code.</summary>
    public ReactiveCommand<string, RxVoid> ApplyLanguageCommand { get; }

    #endregion Commands

    #region Private Fields

    private readonly ISettingsService _settings;
    private readonly IThemeManager _themeManager;
    private readonly ILocalizationManager _localization;
    private readonly ILogger<SettingsViewModel> _logger;

    #endregion Private Fields

    #region Constructor

    /// <summary>
    /// Create a new SettingsViewModel instance.
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

        ApplyThemeCommand = ReactiveCommand.CreateFromTask<string>(ApplyThemeAsync);
        ApplyLanguageCommand = ReactiveCommand.CreateFromTask<string>(ApplyLanguageAsync);

        this.WhenActivated(disposables =>
        {
            Observable
                .FromAsync(() => LoadCurrentSettingsAsync())
                .Subscribe(
                    _ => { },
                    ex => _logger.LogError(ex, "Failed to load settings"))
                .DisposeWith(disposables);
        });
    }

    #endregion Constructor

    #region Private Methods

    private async Task<bool> LoadCurrentSettingsAsync()
    {
        SelectedTheme = await _settings.GetAsync("UI", "Theme", "Dark");
        SelectedLanguage = await _settings.GetAsync("UI", "Language", "en");

        // Task<bool> (not plain Task) keeps Rx pipelines on ReactiveUI.RxVoid; System.Reactive.Unit is banned.
        return true;
    }

    private async Task ApplyThemeAsync(string themeName)
    {
        SelectedTheme = themeName;
        await _themeManager.ApplyThemeAsync(themeName);
        await _settings.SetAsync("UI", "Theme", themeName);
    }

    private async Task ApplyLanguageAsync(string languageCode)
    {
        SelectedLanguage = languageCode;
        await _localization.SetLanguageAsync(languageCode);
        await _settings.SetAsync("UI", "Language", languageCode);
    }

    #endregion Private Methods
}
