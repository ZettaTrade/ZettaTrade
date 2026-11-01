namespace ZettaTrade.App.Tests.ViewModels;

using System.Reactive.Subjects;
using FluentAssertions;
using NSubstitute;
using Xunit;
using ZettaTrade.App;
using ZettaTrade.App.ViewModels;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// Tests for the status bar theme and language chips: they must show the
/// managers' current value on construction and track later changes live.
/// The theme stream carries the canonical Name (the single identifier), so
/// the chip resolves the user-facing DisplayName from the themed list and
/// falls back to the raw name while the list is (not yet) populated.
/// </summary>
public sealed class StatusBarViewModelTests
{
    private static readonly ThemeInfo[] Themes =
    [
        new("dark", "Dark", true, []),
        new("light", "Light", true, []),
    ];

    [Fact]
    public void Constructor_WithManagersHoldingCurrentValue_ShowsDisplayNamesImmediately()
    {
        // Arrange: the theme manager broadcasts its current value on subscribe.
        var themeManager = Substitute.For<IThemeManager>();
        themeManager.ThemeChanged.Returns(new BehaviorSubject<string>("dark"));
        themeManager.AvailableThemes.Returns(Themes);
        var localizationManager = Substitute.For<ILocalizationManager>();
        localizationManager.LanguageChanged.Returns(new BehaviorSubject<string>("en"));

        // Act
        var viewModel = new StatusBarViewModel(
            themeManager,
            localizationManager,
            "0.1.0-test");

        // Assert
        viewModel.CurrentThemeName.Should().Be("Dark"); // DisplayName resolved from the canonical name
        viewModel.CurrentLanguage.Should().Be("en");
    }

    [Fact]
    public void ManagerStreamChange_OnThemeOrLanguageEmitted_UpdatesChips()
    {
        // Arrange
        var themeChanged = new BehaviorSubject<string>("dark");
        var themeManager = Substitute.For<IThemeManager>();
        themeManager.ThemeChanged.Returns(themeChanged);
        themeManager.AvailableThemes.Returns(Themes);
        var languageChanged = new BehaviorSubject<string>("en");
        var localizationManager = Substitute.For<ILocalizationManager>();
        localizationManager.LanguageChanged.Returns(languageChanged);
        var viewModel = new StatusBarViewModel(
            themeManager,
            localizationManager,
            "0.1.0-test");

        // Act
        themeChanged.OnNext("light");
        languageChanged.OnNext("ru");

        // Assert
        viewModel.CurrentThemeName.Should().Be("Light"); // DisplayName resolved from the canonical name
        viewModel.CurrentLanguage.Should().Be("ru");
    }

    [Fact]
    public void ThemeStream_WithUnregisteredTheme_KeepsRawName()
    {
        // Arrange: the chip must not lose information when the theme is not
        // (yet) registered — the raw value is the defensive fallback.
        var themeManager = Substitute.For<IThemeManager>();
        themeManager.ThemeChanged.Returns(new BehaviorSubject<string>("dark"));
        themeManager.AvailableThemes.Returns([]);
        var localizationManager = Substitute.For<ILocalizationManager>();
        localizationManager.LanguageChanged.Returns(new BehaviorSubject<string>("en"));

        // Act
        var viewModel = new StatusBarViewModel(
            themeManager,
            localizationManager,
            "0.1.0-test");

        // Assert
        viewModel.CurrentThemeName.Should().Be("dark");
    }

    [Fact]
    public void Ctor_WithAppVersion_ShowsTheVersionChip()
    {
        var themeManager = Substitute.For<IThemeManager>();
        themeManager.ThemeChanged.Returns(new BehaviorSubject<string>("dark"));
        var localizationManager = Substitute.For<ILocalizationManager>();
        localizationManager.LanguageChanged.Returns(new BehaviorSubject<string>("en"));

        var viewModel = new StatusBarViewModel(
            themeManager,
            localizationManager,
            "0.1.0-alpha.12");

        viewModel.AppVersion.Should().Be("0.1.0-alpha.12");
    }

    [Fact]
    public void ToDisplayVersion_TrimsTheGitSuffixTheSdkBakes()
    {
        AppVersion.ToDisplayVersion("0.1.0-alpha.12+de116cb").Should().Be("0.1.0-alpha.12");
        AppVersion.ToDisplayVersion("0.1.0-alpha.12").Should().Be("0.1.0-alpha.12");
    }

    [Fact]
    public void ToDisplayVersion_WithoutAttributeValue_NeverBlank()
    {
        AppVersion.ToDisplayVersion(null).Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void UpdateConnectionStatus_ReportsLocalizedStatePerValue()
    {
        var themeManager = Substitute.For<IThemeManager>();
        themeManager.ThemeChanged.Returns(new BehaviorSubject<string>("dark"));
        var localizationManager = Substitute.For<ILocalizationManager>();
        localizationManager.LanguageChanged.Returns(new BehaviorSubject<string>("en"));
        localizationManager.GetString("Status.Connected").Returns("Connected");
        localizationManager.GetString("Status.Disconnected").Returns("Disconnected");

        var viewModel = new StatusBarViewModel(
            themeManager,
            localizationManager,
            "0.1.0-test");

        viewModel.UpdateConnectionStatus(true);
        viewModel.ConnectionStatus.Should().Be("Connected", "the connected key is localized");

        viewModel.UpdateConnectionStatus(false);
        viewModel.ConnectionStatus.Should().Be("Disconnected", "the disconnected key is localized");
    }

    [Fact]
    public void LanguageSwitch_KeepsTheConnectionChipLocalized()
    {
        var languageChanged = new BehaviorSubject<string>("en");
        var themeManager = Substitute.For<IThemeManager>();
        themeManager.ThemeChanged.Returns(new BehaviorSubject<string>("dark"));
        var localizationManager = Substitute.For<ILocalizationManager>();
        localizationManager.LanguageChanged.Returns(languageChanged);
        localizationManager.GetString("Status.Connected").Returns("Connected");

        var viewModel = new StatusBarViewModel(
            themeManager,
            localizationManager,
            "0.1.0-test");
        viewModel.UpdateConnectionStatus(true);

        languageChanged.OnNext("ru");

        viewModel.ConnectionStatus.Should().Be("Connected", "the chip re-derives from the stored state, not from a stale text");
    }

    [Fact]
    public void BeforeTheFirstConnectionReport_TheChipStaysEmpty()
    {
        var themeManager = Substitute.For<IThemeManager>();
        themeManager.ThemeChanged.Returns(new BehaviorSubject<string>("dark"));
        var localizationManager = Substitute.For<ILocalizationManager>();
        localizationManager.LanguageChanged.Returns(new BehaviorSubject<string>("en"));

        var viewModel = new StatusBarViewModel(
            themeManager,
            localizationManager,
            "0.1.0-test");

        viewModel.ConnectionStatus.Should().BeEmpty("no state report means no chip text");
    }
}
