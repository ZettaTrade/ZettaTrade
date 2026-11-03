namespace ZettaTrade.App.Tests.ViewModels;

using System.Reactive.Linq;
using System.Reactive.Subjects;
using FluentAssertions;
using NSubstitute;
using ReactiveUI.Primitives;
using Xunit;
using ZettaTrade.App;
using ZettaTrade.App.ViewModels;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Infrastructure.Configuration;
using ZettaTrade.Infrastructure.Shell;
using ZettaTrade.UI.Themes;

/// <summary>
/// Tests for the settings window: the selections start at the managers'
/// applied state (reset on every open, unsaved picks are discarded), and the
/// explicit save applies and persists only real differences — an unchanged
/// selection produces no manager calls and no settings rewrite. The themes
/// folder button must hand the config themes directory to the injected
/// shell abstraction, and theme load errors surface as a live warning list.
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
        var (viewModel, _, _, _, _, _) = CreateViewModel(currentTheme: "Light", currentLanguage: "ru");

        // Assert
        viewModel.SelectedThemeIndex.Should().Be(1); // "light"
        viewModel.SelectedLanguageIndex.Should().Be(1); // "ru"
    }

    /// <summary>
    /// The stored clock format drives the time format combo on open: the
    /// persisted 12-hour choice selects the second row, the default (no
    /// 12-hour shown) the first — reset on every window open.
    /// </summary>
    [Fact]
    public void Constructor_ThePersistedClockChoice_DrivesTheTimeFormatIndex()
    {
        var (viewModel, _, _, _, _, _) = CreateViewModel(
            currentTheme: "Dark",
            currentLanguage: "en",
            persistedUse12HourClock: true);

        viewModel.SelectedTimeFormatIndex.Should().Be(1); // "12-hour"

        viewModel.ResetSelections();

        viewModel.SelectedTimeFormatIndex.Should().Be(1, "an open re-sync keeps the persisted choice");
    }

    [Fact]
    public void ResetSelections_AfterUnsavedClockSelection_DiscardsTheChange()
    {
        var (viewModel, _, _, _, _, _) = CreateViewModel(currentTheme: "Dark", currentLanguage: "en");

        viewModel.SelectedTimeFormatIndex = 1; // "12-hour", unsaved
        viewModel.ResetSelections();

        viewModel.SelectedTimeFormatIndex.Should().Be(0); // applied: the 24-hour default
    }

    /// <summary>
    /// Saving a changed clock selection persists the bool key; an unchanged
    /// selection is a no-op (the same threshold the theme/language choices
    /// apply).
    /// </summary>
    [Fact]
    public async Task SaveAsync_TheClockSelection_PersistsOnlyARealDifference()
    {
        var (viewModel, settings, _, _, _, _) =
            CreateViewModel(currentTheme: "Dark", currentLanguage: "en",
                persistedUse12HourClock: false);

        viewModel.SelectedTimeFormatIndex = 0;
        await viewModel.SaveAsync();
        await settings.DidNotReceiveWithAnyArgs().SetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>());

        viewModel.SelectedTimeFormatIndex = 1; // "12-hour"
        await viewModel.SaveAsync();

        await settings.Received().SetAsync(
            UiSettingsKeys.Section,
            UiSettingsKeys.ChartUse12HourClock,
            true);
    }

    [Fact]
    public void ResetSelections_AfterUnsavedSelectionChange_DiscardsTheChange()
    {
        // Arrange
        var (viewModel, _, _, _, _, _) = CreateViewModel(currentTheme: "Dark", currentLanguage: "en");

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
        var (viewModel, settings, themeManager, localization, _, _) =
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
        var (viewModel, settings, themeManager, localization, _, _) =
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
        var (viewModel, settings, themeManager, localization, _, _) =
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
            Substitute.For<IShellUtils>(),
            Substitute.For<Microsoft.Extensions.Logging.ILogger<SettingsViewModel>>());

        // Assert
        viewModel.SelectedLanguageIndex.Should().Be(1); // "ru"
        viewModel.SelectedThemeIndex.Should().Be(0); // "Dark"
    }

    [Fact]
    public void Constructor_WithDuplicateDisplayNames_ResolvesByCanonicalName()
    {
        // Arrange: two themes share the display name "Dark"; only the
        // canonical Name ("user-dark") uniquely identifies the applied row —
        // matching by display name would always pick the first row.
        var settings = Substitute.For<ISettingsService>();
        var themeManager = Substitute.For<IThemeManager>();
        themeManager.CurrentTheme.Returns("user-dark");
        themeManager.AvailableThemes.Returns(
        [
            new("dark", "Dark", true, []),
            new("user-dark", "Dark", true, []),
        ]);
        var localization = Substitute.For<ILocalizationManager>();
        localization.CurrentLanguage.Returns("en");
        localization.AvailableLanguages.Returns(Languages);

        // Act
        var viewModel = new SettingsViewModel(
            settings,
            themeManager,
            localization,
            Substitute.For<IShellUtils>(),
            Substitute.For<Microsoft.Extensions.Logging.ILogger<SettingsViewModel>>());

        // Assert
        viewModel.SelectedThemeIndex.Should().Be(1); // the row whose Name is the applied identity
    }

    [Fact]
    public async Task OpenThemesFolderCommand_Executed_OpensConfigThemesFolder()
    {
        // Arrange
        var (viewModel, _, _, _, shellUtils, _) =
            CreateViewModel(currentTheme: "Dark", currentLanguage: "en");

        // Act
        await viewModel.OpenThemesFolderCommand.Execute(RxVoid.Default);

        // Assert
        shellUtils.Received().OpenInFileManager(ConfigPathResolver.ThemesDirectory);
    }

    [Fact]
    public void ThemeLoadErrors_EmittedByManager_UpdateWarningState()
    {
        // Arrange
        var (viewModel, _, _, _, _, themeLoadErrors) =
            CreateViewModel(currentTheme: "Dark", currentLanguage: "en");

        viewModel.HasThemeFileErrors.Should().BeFalse();

        // Act
        themeLoadErrors.OnNext(
            [new ThemeFileLoadError(@"C:\themes\broken.json", ThemeFileLoadError.InvalidJson)]);

        // Assert
        viewModel.HasThemeFileErrors.Should().BeTrue();
        viewModel.ThemeFileErrors.Should().ContainSingle().Which.FileName.Should().Be("broken.json");

        // Act: an emptied snapshot clears the warning (a fixed file reloaded).
        themeLoadErrors.OnNext(Array.Empty<ThemeFileLoadError>());

        // Assert
        viewModel.HasThemeFileErrors.Should().BeFalse();
        viewModel.ThemeFileErrors.Should().BeEmpty();
    }

    private static (
        SettingsViewModel ViewModel,
        ISettingsService Settings,
        IThemeManager ThemeManager,
        ILocalizationManager Localization,
        IShellUtils ShellUtils,
        BehaviorSubject<IReadOnlyList<ThemeFileLoadError>> ThemeLoadErrors)
        CreateViewModel(
            string currentTheme,
            string currentLanguage,
            BehaviorSubject<IReadOnlyList<ThemeFileLoadError>>? themeLoadErrors = null,
            bool persistedUse12HourClock = false)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetAsync<bool>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns(persistedUse12HourClock);
        var themeManager = Substitute.For<IThemeManager>();
        themeManager.CurrentTheme.Returns(currentTheme);
        themeManager.AvailableThemes.Returns(Themes);
        var localization = Substitute.For<ILocalizationManager>();
        localization.CurrentLanguage.Returns(currentLanguage);
        localization.AvailableLanguages.Returns(Languages);
        var shellUtils = Substitute.For<IShellUtils>();
        themeLoadErrors ??= new BehaviorSubject<IReadOnlyList<ThemeFileLoadError>>([]);

        var viewModel = new SettingsViewModel(
            settings,
            themeManager,
            localization,
            shellUtils,
            Substitute.For<Microsoft.Extensions.Logging.ILogger<SettingsViewModel>>(),
            themeLoadErrors);

        return (viewModel, settings, themeManager, localization, shellUtils, themeLoadErrors);
    }
}
