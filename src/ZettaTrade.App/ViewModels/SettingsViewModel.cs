namespace ZettaTrade.App.ViewModels;

using System.Collections.ObjectModel;
using System.Reactive;
using ReactiveUI;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// ViewModel for settings management (theme, language, etc.).
/// Delegates available options to ThemeManager and LocalizationManager (DRY, SoC).
/// </summary>
public sealed class SettingsViewModel : ReactiveObject, IActivatableViewModel
{
    public ViewModelActivator Activator { get; } = new();
    
    private readonly ISettingsService _settings;
    private readonly IThemeManager _themeManager;
    private readonly ILocalizationManager _localization;

    private string _selectedTheme = "Dark";
    private string _selectedLanguage = "en";

    public string SelectedTheme
    {
        get => _selectedTheme;
        set => this.RaiseAndSetIfChanged(ref _selectedTheme, value);
    }

    public string SelectedLanguage
    {
        get => _selectedLanguage;
        set => this.RaiseAndSetIfChanged(ref _selectedLanguage, value);
    }

    // Expose available options from managers (no duplication)
    public IReadOnlyList<ThemeInfo> AvailableThemes => _themeManager.AvailableThemes;
    public IReadOnlyList<LanguageInfo> AvailableLanguages => _localization.AvailableLanguages;

    // Commands for applying theme and language
    public ReactiveCommand<string, Unit> ApplyThemeCommand { get; }
    public ReactiveCommand<string, Unit> ApplyLanguageCommand { get; }

    public SettingsViewModel(
        ISettingsService settings,
        IThemeManager themeManager,
        ILocalizationManager localization)
    {
        _settings = settings;
        _themeManager = themeManager;
        _localization = localization;

        // Initialize commands
        ApplyThemeCommand = ReactiveCommand.CreateFromTask<string>(ApplyThemeAsync);
        ApplyLanguageCommand = ReactiveCommand.CreateFromTask<string>(ApplyLanguageAsync);

        this.WhenActivated(disposables =>
        {
            // Load current settings
            _ = LoadCurrentSettingsAsync();
        });
    }

    private async Task LoadCurrentSettingsAsync()
    {
        try
        {
            SelectedTheme = await _settings.GetAsync("UI", "Theme", "Dark");
            SelectedLanguage = await _settings.GetAsync("UI", "Language", "en");
        }
        catch (Exception)
        {
            // Use defaults if settings load fails
        }
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
}
