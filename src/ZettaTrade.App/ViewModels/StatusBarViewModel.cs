using ReactiveUI;
using ZettaTrade.Core.Interfaces;

namespace ZettaTrade.App.ViewModels;

/// <summary>
/// ViewModel for status bar showing connection status, theme, and language.
/// </summary>
public sealed class StatusBarViewModel : ReactiveObject, IActivatableViewModel
{
    public ViewModelActivator Activator { get; } = new();
    
    private readonly ISettingsService _settings;
    private readonly ILocalizationManager _localization;

    [Reactive] public string StatusText { get; set; } = string.Empty;
    [Reactive] public string ConnectionStatus { get; set; } = "Disconnected";
    [Reactive] public string CurrentThemeName { get; set; } = "Dark";
    [Reactive] public string CurrentLanguage { get; set; } = "en";

    public StatusBarViewModel(
        ISettingsService settings,
        ILocalizationManager localization)
    {
        _settings = settings;
        _localization = localization;

        this.WhenActivated(disposables =>
        {
            // Load initial settings
            _ = LoadSettingsAsync();

            // Subscribe to language changes
            _localization.LanguageChanged
                .Subscribe(_ => UpdateLocalizedStrings())
                .DisposeWith(disposables);

            UpdateLocalizedStrings();
        });
    }

    private async Task LoadSettingsAsync()
    {
        try
        {
            CurrentThemeName = await _settings.GetAsync("UI", "Theme", "Dark");
            CurrentLanguage = await _settings.GetAsync("UI", "Language", "en");
        }
        catch (Exception)
        {
            // Use defaults if settings load fails
        }
    }

    private void UpdateLocalizedStrings()
    {
        StatusText = _localization.GetString("Status.Ready");
    }
}
