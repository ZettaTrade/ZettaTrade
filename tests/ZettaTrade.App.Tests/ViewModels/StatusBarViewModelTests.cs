namespace ZettaTrade.App.Tests.ViewModels;

using System;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using ZettaTrade.App;
using ZettaTrade.App.Services;
using ZettaTrade.App.ViewModels;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// Tests for the status bar: the theme and language chips must show the
/// managers' current value on construction and track later changes live;
/// the exchange picker is hidden for a single adapter, visible (and
/// switching-driving) for two; the persisted-choice fallback plays the loud
/// localized notice in the connection chip for a bounded window and is not
/// silently overwritten by a regular state refresh before it expires.
/// </summary>
public sealed class StatusBarViewModelTests
{
    private static readonly ThemeInfo[] Themes =
    [
        new("dark", "Dark", true, []),
        new("light", "Light", true, []),
    ];

    private readonly IThemeManager _themeManager = Substitute.For<IThemeManager>();
    private readonly ILocalizationManager _localizationManager = Substitute.For<ILocalizationManager>();
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly IMarketDataAdapter _first = Substitute.For<IMarketDataAdapter>();
    private readonly IMarketDataAdapter _second = Substitute.For<IMarketDataAdapter>();

    public StatusBarViewModelTests()
    {
        _themeManager.ThemeChanged.Returns(new BehaviorSubject<string>("dark"));
        _themeManager.AvailableThemes.Returns(Themes);
        _localizationManager.LanguageChanged.Returns(new BehaviorSubject<string>("en"));

        _first.Id.Returns("first");
        _first.Name.Returns("First");
        _second.Id.Returns("second");
        _second.Name.Returns("Second");

        // No persisted choice: the facade restore (and any explicit one in
        // the fallback tests) always starts here.
        _settings.GetAsync<string>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(string.Empty);
    }

    private ActiveExchangeService CreateService(int adapters = 2)
    {
        var catalog = adapters == 1 ? new[] { _first } : new[] { _first, _second };
        return new ActiveExchangeService(
            catalog,
            _settings,
            NullLogger<ActiveExchangeService>.Instance);
    }

    private StatusBarViewModel CreateViewModel(
        ActiveExchangeService? exchanges = null,
        TimeSpan? fallbackNoticeDuration = null) =>
        new(
            _themeManager,
            _localizationManager,
            exchanges ?? CreateService(),
            "0.1.0-test",
            fallbackNoticeDuration,
            NullLogger<StatusBarViewModel>.Instance);

    [Fact]
    public void Constructor_WithManagersHoldingCurrentValue_ShowsDisplayNamesImmediately()
    {
        // Arrange: the theme manager broadcasts its current value on subscribe.
        var viewModel = CreateViewModel();

        // Assert
        viewModel.CurrentThemeName.Should().Be("Dark"); // DisplayName resolved from the canonical name
        viewModel.CurrentLanguage.Should().Be("en");
    }

    [Fact]
    public void ManagerStreamChange_OnThemeOrLanguageEmitted_UpdatesChips()
    {
        var themeChanged = new BehaviorSubject<string>("dark");
        var languageChanged = new BehaviorSubject<string>("en");
        _themeManager.ThemeChanged.Returns(themeChanged);
        _localizationManager.LanguageChanged.Returns(languageChanged);
        var viewModel = CreateViewModel();

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
        _themeManager.ThemeChanged.Returns(new BehaviorSubject<string>("dark"));
        _themeManager.AvailableThemes.Returns([]);

        // Act
        var viewModel = CreateViewModel();

        // Assert
        viewModel.CurrentThemeName.Should().Be("dark");
    }

    [Fact]
    public void Ctor_WithAppVersion_ShowsTheVersionChip()
    {
        var viewModel = CreateViewModel();

        viewModel.AppVersion.Should().Be("0.1.0-test");
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
        _localizationManager.GetString("Status.Connected").Returns("Connected");
        _localizationManager.GetString("Status.Disconnected").Returns("Disconnected");

        var viewModel = CreateViewModel();

        viewModel.UpdateConnectionStatus(true);
        viewModel.ConnectionStatus.Should().Be("Connected", "the connected key is localized");

        viewModel.UpdateConnectionStatus(false);
        viewModel.ConnectionStatus.Should().Be("Disconnected", "the disconnected key is localized");
    }

    [Fact]
    public void LanguageSwitch_KeepsTheConnectionChipLocalized()
    {
        var languageChanged = new BehaviorSubject<string>("en");
        _localizationManager.LanguageChanged.Returns(languageChanged);
        _localizationManager.GetString("Status.Connected").Returns("Connected");

        var viewModel = CreateViewModel();
        viewModel.UpdateConnectionStatus(true);

        languageChanged.OnNext("ru");

        viewModel.ConnectionStatus.Should().Be("Connected", "the chip re-derives from the stored state, not from a stale text");
    }

    [Fact]
    public void BeforeTheFirstConnectionReport_TheChipStaysEmpty()
    {
        var viewModel = CreateViewModel();

        viewModel.ConnectionStatus.Should().BeEmpty("no state report means no chip text");
    }

    [Fact]
    public void SingleRegisteredAdapter_ThePickerStaysHidden()
    {
        var service = CreateService(adapters: 1);

        var viewModel = CreateViewModel(service);

        viewModel.HasExchangeChoice.Should().BeFalse("a selector over one entry is meaningless by design");
        viewModel.ExchangeNames.Should().ContainSingle().Which.Should().Be("First");
        viewModel.ActiveExchangeName.Should().Be("First");
        viewModel.SelectedExchangeName.Should().Be("First");
    }

    [Fact]
    public void TwoRegisteredAdapters_ThePickerIsVisibleWithBothNames()
    {
        var viewModel = CreateViewModel();

        viewModel.HasExchangeChoice.Should().BeTrue("two registered entries make the selector meaningful");
        viewModel.ExchangeNames.Should().ContainInOrder(
            new[] { "First", "Second" },
            "registration order feeds the picker");
        viewModel.SelectedExchangeName.Should().Be("First", "the picker mirrors the behavioral facade");
        viewModel.ActiveExchangeName.Should().Be("First");
    }

    [Fact]
    public async Task PickerSelection_DrivesTheSwitchThroughTheFacade()
    {
        var service = CreateService();
        var viewModel = CreateViewModel(service);

        var flipped = new TaskCompletionSource<IMarketDataAdapter>(TaskCreationOptions.RunContinuationsAsynchronously);
        var emissions = 0;
        using var changes = service.AdapterChanges.Subscribe(adapter =>
        {
            if (++emissions == 2)
            {
                flipped.TrySetResult(adapter);
            }
        });

        // Act: the user picks the second exchange in the picker.
        viewModel.SelectedExchangeName = "Second";

        // The picker selection must flip the facade promptly.
        var winner = await Task.WhenAny(flipped.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        winner.Should().Be(flipped.Task, "the picker selection must flip the facade promptly");
        (await flipped.Task).Should().Be(_second);

        await _settings.Received(1).SetAsync("Workspace", "ExchangeId", "second");
        viewModel.ActiveExchangeName.Should().Be("Second", "the chip follows the switch");
        viewModel.SelectedExchangeName.Should().Be("Second", "the facade echo dedupes, the picker keeps the pick");
    }

    [Fact]
    public async Task UnavailablePersistedChoice_PlaysTheNoticeThenExpiresToTheRegularChip()
    {
        _settings.GetAsync<string>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns("gamma");
        _localizationManager.GetString("Status.ExchangeFallback").Returns("Saved exchange unavailable");
        _localizationManager.GetString("Status.Connected").Returns("Connected");

        var service = CreateService();
        var viewModel = CreateViewModel(service, TimeSpan.FromMilliseconds(60));
        viewModel.UpdateConnectionStatus(true);

        // Act: the restore cannot honor the persisted choice and falls back.
        await service.InitializeAsync();

        viewModel.ConnectionStatus.Should().Be("Saved exchange unavailable", "the fallback warning owns the chip");

        // The expiry is a thread-pool timer; under the full suite (four test
        // processes in parallel) the scheduling can slip by tens of
        // milliseconds at a burst — thirteen windows is a safe margin.
        await Task.Delay(800);

        viewModel.ConnectionStatus.Should().Be("Connected", "the notice expires within the configured window");
    }

    [Fact]
    public async Task FallbackNotice_SurvivesTheStartupStateReport_UntilExpiry()
    {
        // Arrange: the connection poll's first state report usually lands on
        // top of the notice — the loud warning must not be silently replaced.
        _settings.GetAsync<string>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns("gamma");
        _localizationManager.GetString("Status.ExchangeFallback").Returns("Saved exchange unavailable");
        _localizationManager.GetString("Status.Connected").Returns("Connected");

        var service = CreateService();
        var viewModel = CreateViewModel(service, TimeSpan.FromMilliseconds(60));

        await service.InitializeAsync();
        viewModel.ConnectionStatus.Should().Be("Saved exchange unavailable");

        // Act: the state report arrives during the notice window.
        viewModel.UpdateConnectionStatus(true);

        viewModel.ConnectionStatus.Should().Be("Saved exchange unavailable", "the regular refresh cannot overwrite the notice");

        await Task.Delay(800);

        viewModel.ConnectionStatus.Should().Be("Connected", "after expiry the regular chip text re-derives");
    }
}
