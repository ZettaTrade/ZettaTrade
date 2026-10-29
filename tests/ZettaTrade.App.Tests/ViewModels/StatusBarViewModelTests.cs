namespace ZettaTrade.App.Tests.ViewModels;

using System.Reactive.Subjects;
using FluentAssertions;
using NSubstitute;
using Xunit;
using ZettaTrade.App.ViewModels;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// Tests for the status bar theme and language chips: they must show the
/// managers' current value on construction and track later changes live.
/// The manager stubs expose <see cref="BehaviorSubject{T}"/> streams, which is
/// exactly the replay-on-subscribe contract both managers provide.
/// </summary>
public sealed class StatusBarViewModelTests
{
    [Fact]
    public void Constructor_WithManagersHoldingCurrentValue_ShowsCurrentThemeAndLanguageImmediately()
    {
        // Arrange: both managers broadcast their current value on subscribe.
        var themeManager = Substitute.For<IThemeManager>();
        themeManager.ThemeChanged.Returns(new BehaviorSubject<string>("Dark"));
        var localizationManager = Substitute.For<ILocalizationManager>();
        localizationManager.LanguageChanged.Returns(new BehaviorSubject<string>("en"));

        // Act
        var viewModel = new StatusBarViewModel(themeManager, localizationManager);

        // Assert
        viewModel.CurrentThemeName.Should().Be("Dark");
        viewModel.CurrentLanguage.Should().Be("en");
    }

    [Fact]
    public void ManagerStreamChange_OnThemeOrLanguageEmitted_UpdatesChips()
    {
        // Arrange
        var themeChanged = new BehaviorSubject<string>("Dark");
        var themeManager = Substitute.For<IThemeManager>();
        themeManager.ThemeChanged.Returns(themeChanged);
        var languageChanged = new BehaviorSubject<string>("en");
        var localizationManager = Substitute.For<ILocalizationManager>();
        localizationManager.LanguageChanged.Returns(languageChanged);
        var viewModel = new StatusBarViewModel(themeManager, localizationManager);

        // Act
        themeChanged.OnNext("Light");
        languageChanged.OnNext("ru");

        // Assert
        viewModel.CurrentThemeName.Should().Be("Light");
        viewModel.CurrentLanguage.Should().Be("ru");
    }
}
