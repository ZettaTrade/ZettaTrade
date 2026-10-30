namespace ZettaTrade.App.Tests.ViewModels;

using FluentAssertions;
using NSubstitute;
using Xunit;
using ZettaTrade.App;
using ZettaTrade.App.ViewModels;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// Tests for the settings window selection: it starts at the managers'
/// current state, applies any real change to the managers and persists it,
/// and leaves an unchanged selection a complete no-op (no settings rewrite,
/// no redundant theme/language event).
/// </summary>
public sealed class SettingsViewModelTests
{
    private static readonly ThemeInfo[] Themes =
    [
        new("dark", "Dark", true, []),
        new("light", "Light", true, []),
    ];

    private static readonly LanguageInfo[] Languages =
    [
        new("en", "English", "English", true),
        new("ru", "Russian", "Русский", true),
    ];

    [Fact]
    public void Constructor_WithInitializedManagers_SelectsTheirCurrentState()
    {
        // Arrange + Act
        var (viewModel, _, _, _) = CreateViewModel(currentTheme: "Light", currentLanguage: "ru");

        // Assert
        viewModel.SelectedThemeItem.Should().Be(Themes.Single(theme => theme.Name == "light"));
        viewModel.SelectedLanguageItem!.Code.Should().Be("ru");
    }

    [Fact]
    public async Task Constructor_WithInitializedManagers_DoesNotReapplyOrPersist()
    {
        // Arrange + Act: the pipeline runs once for the initial selection.
        var (viewModel, settings, themeManager, localization) =
            CreateViewModel(currentTheme: "Light", currentLanguage: "ru");

        // Assert: the selection already matches what is on screen.
        await themeManager.DidNotReceive().ApplyThemeAsync(Arg.Any<string>());
        await localization.DidNotReceive().SetLanguageAsync(Arg.Any<string>());
        await settings.DidNotReceiveWithAnyArgs().SetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task SelectedThemeItemChanged_AppliesNewThemeAndPersistsIt()
    {
        // Arrange
        var (viewModel, settings, themeManager, _) =
            CreateViewModel(currentTheme: "Light", currentLanguage: "en");

        // Act
        viewModel.SelectedThemeItem = Themes.Single(theme => theme.Name == "dark");

        // Assert
        await themeManager.Received().ApplyThemeAsync("dark");
        await settings.Received().SetAsync(UiSettingsKeys.Section, UiSettingsKeys.Theme, "dark");
    }

    [Fact]
    public async Task SelectedLanguageItemChanged_AppliesNewLanguageAndPersistsIt()
    {
        // Arrange
        var (viewModel, settings, themeManager, localization) =
            CreateViewModel(currentTheme: "Dark", currentLanguage: "en");

        // Act
        viewModel.SelectedLanguageItem = Languages.Single(language => language.Code == "ru");

        // Assert
        await localization.Received().SetLanguageAsync("ru");
        await settings.Received().SetAsync(UiSettingsKeys.Section, UiSettingsKeys.Language, "ru");
        await themeManager.DidNotReceive().ApplyThemeAsync(Arg.Any<string>());
    }

    private static (
        SettingsViewModel ViewModel,
        ISettingsService Settings,
        IThemeManager ThemeManager,
        ILocalizationManager Localization)
        CreateViewModel(string currentTheme, string currentLanguage)
    {
        var settings = Substitute.For<ISettingsService>();
        var themeManager = Substitute.For<IThemeManager>();
        themeManager.CurrentTheme.Returns(currentTheme);
        themeManager.AvailableThemes.Returns(Themes);
        var localization = Substitute.For<ILocalizationManager>();
        localization.CurrentLanguage.Returns(currentLanguage);
        localization.AvailableLanguages.Returns(Languages);

        var viewModel = new SettingsViewModel(
            settings,
            themeManager,
            localization,
            Substitute.For<Microsoft.Extensions.Logging.ILogger<SettingsViewModel>>());

        return (viewModel, settings, themeManager, localization);
    }
}
