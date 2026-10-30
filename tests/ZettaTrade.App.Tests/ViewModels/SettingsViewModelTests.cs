namespace ZettaTrade.App.Tests.ViewModels;

using FluentAssertions;
using NSubstitute;
using Xunit;
using ZettaTrade.App;
using ZettaTrade.App.ViewModels;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// Tests for the settings window: the selections start at the managers'
/// applied state (reset on every open, unsaved picks are discarded), and the
/// explicit save applies and persists only real differences — an unchanged
/// selection produces no manager calls and no settings rewrite.
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
    public void Constructor_WithInitializedManagers_SelectsAppliedState()
    {
        // Arrange + Act
        var (viewModel, _, _, _) = CreateViewModel(currentTheme: "Light", currentLanguage: "ru");

        // Assert
        viewModel.SelectedThemeIndex.Should().Be(1); // "light"
        viewModel.SelectedLanguageIndex.Should().Be(1); // "ru"
    }

    [Fact]
    public void ResetSelections_AfterUnsavedSelectionChange_DiscardsTheChange()
    {
        // Arrange
        var (viewModel, _, _, _) = CreateViewModel(currentTheme: "Dark", currentLanguage: "en");

        // Act
        viewModel.SelectedThemeIndex = 1;
        viewModel.SelectedLanguageIndex = 1;
        viewModel.ResetSelections();

        // Assert
        viewModel.SelectedThemeIndex.Should().Be(0); // applied: "dark"
        viewModel.SelectedLanguageIndex.Should().Be(0); // applied: "en"
    }

    [Fact]
    public async Task SaveAsync_WithChangedSelections_AppliesAndPersistsBoth()
    {
        // Arrange
        var (viewModel, settings, themeManager, localization) =
            CreateViewModel(currentTheme: "Dark", currentLanguage: "en");

        // Act
        viewModel.SelectedThemeIndex = 1; // "light"
        viewModel.SelectedLanguageIndex = 1; // "ru"
        await viewModel.SaveAsync();

        // Assert
        await themeManager.Received().ApplyThemeAsync("light");
        await localization.Received().SetLanguageAsync("ru");
        await settings.Received().SetAsync(UiSettingsKeys.Section, UiSettingsKeys.Theme, "light");
        await settings.Received().SetAsync(UiSettingsKeys.Section, UiSettingsKeys.Language, "ru");
    }

    [Fact]
    public async Task SaveAsync_WithUnchangedSelections_IsCompleteNoOp()
    {
        // Arrange: selections already match the applied state (fresh reset).
        var (viewModel, settings, themeManager, localization) =
            CreateViewModel(currentTheme: "Dark", currentLanguage: "en");
        viewModel.SelectedThemeIndex = 0;
        viewModel.SelectedLanguageIndex = 0;

        // Act
        await viewModel.SaveAsync();

        // Assert
        await themeManager.DidNotReceive().ApplyThemeAsync(Arg.Any<string>());
        await localization.DidNotReceive().SetLanguageAsync(Arg.Any<string>());
        await settings.DidNotReceiveWithAnyArgs().SetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task SaveAsync_WithOnlyLanguageChanged_DoesNotTouchTheme()
    {
        // Arrange
        var (viewModel, settings, themeManager, localization) =
            CreateViewModel(currentTheme: "Dark", currentLanguage: "en");

        // Act
        viewModel.SelectedLanguageIndex = 1; // "ru"
        await viewModel.SaveAsync();

        // Assert
        await localization.Received().SetLanguageAsync("ru");
        await settings.Received().SetAsync(UiSettingsKeys.Section, UiSettingsKeys.Language, "ru");
        await themeManager.DidNotReceive().ApplyThemeAsync(Arg.Any<string>());
    }

    [Fact]
    public void IndexOfAppliedLanguage_FallsBackToTwoLetterCode()
    {
        // Arrange: the OS-reported culture is region-specific ("ru-RU"), but the
        // language list only has plain codes ("ru"): the selection must still resolve.
        var settings = Substitute.For<ISettingsService>();
        var themeManager = Substitute.For<IThemeManager>();
        themeManager.CurrentTheme.Returns("Dark");
        themeManager.AvailableThemes.Returns(Themes);
        var localization = Substitute.For<ILocalizationManager>();
        localization.CurrentLanguage.Returns("ru-RU");
        localization.AvailableLanguages.Returns(Languages);

        // Act
        var viewModel = new SettingsViewModel(
            settings,
            themeManager,
            localization,
            Substitute.For<Microsoft.Extensions.Logging.ILogger<SettingsViewModel>>());

        // Assert
        viewModel.SelectedLanguageIndex.Should().Be(1); // "ru"
        viewModel.SelectedThemeIndex.Should().Be(0); // "Dark"
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
