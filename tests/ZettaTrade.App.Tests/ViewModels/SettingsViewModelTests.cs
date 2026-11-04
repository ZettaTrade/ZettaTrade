namespace ZettaTrade.App.Tests.ViewModels;

using System.Reactive.Linq;
using System.Reactive.Subjects;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ReactiveUI.Primitives;
using Xunit;
using ZettaTrade.App;
using ZettaTrade.App.Services;
using ZettaTrade.App.ViewModels;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.Infrastructure.Configuration;
using ZettaTrade.Infrastructure.Shell;
using ZettaTrade.UI.Themes;

/// <summary>
/// Tests for the settings window: the selections start at the managers'
/// applied state (reset on every open, unsaved picks are discarded), and the
/// explicit save applies and persists only real differences — an unchanged
/// selection produces no manager calls and no settings rewrite. The pairs
/// tab manages the hidden pairs as a draft: a hide or reveal move only
/// repaints the two lists, Save alone parses the draft against the applied
/// map and persists a real difference (the other exchanges' entries ride).
/// The themes folder button must hand the config themes directory to the
/// injected shell abstraction, and theme load errors surface as a live
/// warning list.
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
        var (viewModel, _, _, _, _, _, _, _) = CreateViewModel(currentTheme: "Light", currentLanguage: "ru");

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
    public async Task ResetSelectionsAsync_ThePersistedClockChoice_DrivesTheTimeFormatIndex()
    {
        var (viewModel, _, _, _, _, _, _, _) = CreateViewModel(
            currentTheme: "Dark",
            currentLanguage: "en",
            persistedUse12HourClock: true);

        viewModel.SelectedTimeFormatIndex.Should().Be(1); // "12-hour"

        await viewModel.ResetSelectionsAsync();

        viewModel.SelectedTimeFormatIndex.Should().Be(1, "an open re-sync keeps the persisted choice");
    }

    [Fact]
    public async Task ResetSelectionsAsync_AfterUnsavedClockSelection_DiscardsTheChange()
    {
        var (viewModel, _, _, _, _, _, _, _) = CreateViewModel(currentTheme: "Dark", currentLanguage: "en");

        viewModel.SelectedTimeFormatIndex = 1; // "12-hour", unsaved
        await viewModel.ResetSelectionsAsync();

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
        var (viewModel, settings, _, _, _, _, _, _) =
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
    public async Task ResetSelectionsAsync_AfterUnsavedSelectionChange_DiscardsTheChange()
    {
        // Arrange
        var (viewModel, _, _, _, _, _, _, _) = CreateViewModel(currentTheme: "Dark", currentLanguage: "en");

        // Act
        viewModel.SelectedThemeIndex = 1;
        viewModel.SelectedLanguageIndex = 1;
        await viewModel.ResetSelectionsAsync();

        // Assert
        viewModel.SelectedThemeIndex.Should().Be(0); // applied: "dark"
        viewModel.SelectedLanguageIndex.Should().Be(0); // applied: "en"
    }

    [Fact]
    public async Task SaveAsync_WithChangedSelections_AppliesAndPersistsBoth()
    {
        // Arrange
        var (viewModel, settings, themeManager, localization, _, _, _, _) =
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
        var (viewModel, settings, themeManager, localization, _, _, _, _) =
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
        var (viewModel, settings, themeManager, localization, _, _, _, _) =
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
        var (activeExchanges, symbolList, _) = CreatePairSources(settings);

        // Act
        var viewModel = new SettingsViewModel(
            settings,
            themeManager,
            localization,
            Substitute.For<IShellUtils>(),
            activeExchanges,
            symbolList,
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
        var (activeExchanges, symbolList, _) = CreatePairSources(settings);

        // Act
        var viewModel = new SettingsViewModel(
            settings,
            themeManager,
            localization,
            Substitute.For<IShellUtils>(),
            activeExchanges,
            symbolList,
            Substitute.For<Microsoft.Extensions.Logging.ILogger<SettingsViewModel>>());

        // Assert
        viewModel.SelectedThemeIndex.Should().Be(1); // the row whose Name is the applied identity
    }

    [Fact]
    public async Task OpenThemesFolderCommand_Executed_OpensConfigThemesFolder()
    {
        // Arrange
        var (viewModel, _, _, _, shellUtils, _, _, _) =
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
        var (viewModel, _, _, _, _, themeLoadErrors, _, _) =
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

    /// <summary>
    /// The pairs tab opens on the two lists: the workspace exchange's
    /// listing minus the persisted hidden names (left) and the persisted
    /// hidden names (right), the exchange's guard order preserved.
    /// </summary>
    [Fact]
    public async Task ResetSelectionsAsync_PopulatesTheTwoPairLists()
    {
        var (viewModel, _, _, _, _, _, _, _) = CreateViewModel(
            currentTheme: "Dark",
            currentLanguage: "en",
            persistedHiddenMap: """{"demo":["SOL/USDC"]}""");

        await viewModel.ResetSelectionsAsync();

        viewModel.AllPairs.Should().Equal("BTC/USDT", "ETH/BTC");
        viewModel.HiddenPairs.Should().Equal("SOL/USDC");
    }

    /// <summary>
    /// A hide move repaints the lists at once but never persists — the map
    /// is only written by the explicit save.
    /// </summary>
    [Fact]
    public async Task TheHideMove_RepaintsTheLists_WithoutAnySettingsWrite()
    {
        var (viewModel, settings, _, _, _, _, _, _) =
            CreateViewModel(
                currentTheme: "Dark",
                currentLanguage: "en",
                persistedHiddenMap: """{"demo":["SOL/USDC"]}""");

        await viewModel.ResetSelectionsAsync();
        viewModel.SelectedAllPair = "ETH/BTC";
        await viewModel.HidePairCommand.Execute(RxVoid.Default);

        viewModel.AllPairs.Should().Equal("BTC/USDT");
        viewModel.HiddenPairs.Should().Equal("ETH/BTC", "SOL/USDC");
        await settings.DidNotReceive().SetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    /// <summary>
    /// The save applies the hidden-pairs draft: the map is rewritten with
    /// the draft exchange's entry replaced by the draft (sorted), and
    /// nothing is written while the draft matches the applied state.
    /// </summary>
    [Fact]
    public async Task SaveAsync_TheHiddenDraft_PersistsOnlyARealDifference()
    {
        var (viewModel, settings, _, _, _, _, _, _) =
            CreateViewModel(
                currentTheme: "Dark",
                currentLanguage: "en",
                persistedHiddenMap: """{"demo":["SOL/USDC"]}""");

        await viewModel.ResetSelectionsAsync();
        await viewModel.SaveAsync();

        await settings.DidNotReceive().SetAsync(
            UiSettingsKeys.WorkspaceSection,
            UiSettingsKeys.WorkspaceHiddenSymbols,
            Arg.Any<string>());

        viewModel.SelectedAllPair = "ETH/BTC";
        await viewModel.HidePairCommand.Execute(RxVoid.Default);
        await viewModel.SaveAsync();

        await settings.Received(1).SetAsync(
            UiSettingsKeys.WorkspaceSection,
            UiSettingsKeys.WorkspaceHiddenSymbols,
            """{"demo":["ETH/BTC","SOL/USDC"]}""");
    }

    /// <summary>
    /// A reveal move of a listed hidden pair: the draft drops the name and
    /// the save rewrites the map — the other exchanges' entries survive.
    /// </summary>
    [Fact]
    public async Task TheRevealMove_DropsTheName_AndTheOtherExchangesSurvive()
    {
        var (viewModel, settings, _, _, _, _, _, _) =
            CreateViewModel(
                currentTheme: "Dark",
                currentLanguage: "en",
                persistedHiddenMap: """{"other":["X/USDT"],"demo":["SOL/USDC"]}""");

        await viewModel.ResetSelectionsAsync();
        viewModel.SelectedHiddenPair = "SOL/USDC";
        await viewModel.RevealPairCommand.Execute(RxVoid.Default);

        viewModel.AllPairs.Should().Equal("BTC/USDT", "ETH/BTC", "SOL/USDC");
        viewModel.HiddenPairs.Should().BeEmpty();

        await viewModel.SaveAsync();

        await settings.Received(1).SetAsync(
            UiSettingsKeys.WorkspaceSection,
            UiSettingsKeys.WorkspaceHiddenSymbols,
            """{"other":["X/USDT"]}""");
    }

    /// <summary>
    /// A hidden name the exchange no longer lists survives in the hidden
    /// list (it is the map's own content), and revealing it cleans it from
    /// the document.
    /// </summary>
    [Fact]
    public async Task RevealOfAStaleName_CleansItFromTheMap()
    {
        var (viewModel, settings, _, _, _, _, _, _) =
            CreateViewModel(
                currentTheme: "Dark",
                currentLanguage: "en",
                persistedHiddenMap: """{"demo":["GONE/USDT"]}""");

        await viewModel.ResetSelectionsAsync();

        viewModel.AllPairs.Should().Equal("BTC/USDT", "ETH/BTC", "SOL/USDC");
        viewModel.HiddenPairs.Should().Equal("GONE/USDT");

        viewModel.SelectedHiddenPair = "GONE/USDT";
        await viewModel.RevealPairCommand.Execute(RxVoid.Default);
        await viewModel.SaveAsync();

        await settings.Received(1).SetAsync(
            UiSettingsKeys.WorkspaceSection,
            UiSettingsKeys.WorkspaceHiddenSymbols,
            "{}");
    }

    /// <summary>
    /// The pair moves act on a pick: with none, both commands are disabled
    /// (the buttons gray out); a pick arms the matching command.
    /// </summary>
    [Fact]
    public async Task ThePairMoves_RequireAPick()
    {
        var (viewModel, _, _, _, _, _, _, _) = CreateViewModel(currentTheme: "Dark", currentLanguage: "en");

        await viewModel.ResetSelectionsAsync();

        bool hideEnabled = true;
        using var hideSubscription = viewModel.HidePairCommand.CanExecute
            .Subscribe(value => hideEnabled = value);
        bool revealEnabled = true;
        using var revealSubscription = viewModel.RevealPairCommand.CanExecute
            .Subscribe(value => revealEnabled = value);

        hideEnabled.Should().BeFalse("no pair is picked right after the reset");
        revealEnabled.Should().BeFalse();

        viewModel.SelectedAllPair = "ETH/BTC";
        hideEnabled.Should().BeTrue("the all-pairs pick arms the hide move");
        revealEnabled.Should().BeFalse();

        viewModel.SelectedAllPair = null;
        viewModel.SelectedHiddenPair = "SOL/USDC";
        hideEnabled.Should().BeFalse();
        revealEnabled.Should().BeTrue("the hidden pick arms the reveal move");
    }

    /// <summary>
    /// A listing fetch failure must not break the window: the appearance
    /// selections still reset, the pair lists stay stale and the save never
    /// writes a draft with no exchange context behind it.
    /// </summary>
    [Fact]
    public async Task RefreshSymbolPairsAsync_AFailedListing_KeepsTheWindowWorking()
    {
        var (viewModel, settings, _, _, _, _, _, _) =
            CreateViewModel(
                currentTheme: "Dark",
                currentLanguage: "en",
                persistedHiddenMap: """{"demo":["SOL/USDC"]}""",
                symbolNames: null,
                listingFails: true);

        await viewModel.ResetSelectionsAsync();

        viewModel.SelectedThemeIndex.Should().Be(0, "the appearance selections still reset");
        viewModel.AllPairs.Should().BeEmpty("the listing never arrived, the lists stay stale");

        await viewModel.SaveAsync();

        await settings.DidNotReceiveWithAnyArgs().SetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    private static (
        SettingsViewModel ViewModel,
        ISettingsService Settings,
        IThemeManager ThemeManager,
        ILocalizationManager Localization,
        IShellUtils ShellUtils,
        BehaviorSubject<IReadOnlyList<ThemeFileLoadError>> ThemeLoadErrors,
        ActiveExchangeService ActiveExchanges,
        SymbolListProvider SymbolList)
        CreateViewModel(
            string currentTheme,
            string currentLanguage,
            BehaviorSubject<IReadOnlyList<ThemeFileLoadError>>? themeLoadErrors = null,
            bool persistedUse12HourClock = false,
            string persistedHiddenMap = "",
            IReadOnlyList<string>? symbolNames = null,
            bool listingFails = false)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetAsync<bool>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns(persistedUse12HourClock);
        settings.GetAsync<string>(Arg.Any<string>(), Arg.Any<string>())
            .Returns(persistedHiddenMap);
        var themeManager = Substitute.For<IThemeManager>();
        themeManager.CurrentTheme.Returns(currentTheme);
        themeManager.AvailableThemes.Returns(Themes);
        var localization = Substitute.For<ILocalizationManager>();
        localization.CurrentLanguage.Returns(currentLanguage);
        localization.AvailableLanguages.Returns(Languages);
        var shellUtils = Substitute.For<IShellUtils>();
        themeLoadErrors ??= new BehaviorSubject<IReadOnlyList<ThemeFileLoadError>>([]);

        var (activeExchanges, symbolList, adapter) = CreatePairSources(settings, symbolNames);
        if (listingFails)
        {
            adapter.GetSymbolsAsync(Arg.Any<CancellationToken>())
                .Returns(callInfo => Task.FromException<IReadOnlyList<Symbol>>(
                    new InvalidOperationException("listing unavailable")));
        }

        var viewModel = new SettingsViewModel(
            settings,
            themeManager,
            localization,
            shellUtils,
            activeExchanges,
            symbolList,
            Substitute.For<Microsoft.Extensions.Logging.ILogger<SettingsViewModel>>(),
            themeLoadErrors);

        return (viewModel, settings, themeManager, localization, shellUtils, themeLoadErrors, activeExchanges, symbolList);
    }

    /// <summary>
    /// The pair sources for one settings test factory: the workspace
    /// exchange facade over a single listing adapter (the adapter identity
    /// "demo" matches the persisted maps' exchange entry) and the cache
    /// -backed symbol provider standing in for the composition root's one.
    /// </summary>
    private static (
        ActiveExchangeService ActiveExchanges,
        SymbolListProvider SymbolList,
        IMarketDataAdapter Adapter)
        CreatePairSources(ISettingsService settings, IReadOnlyList<string>? symbolNames = null)
    {
        var adapter = Substitute.For<IMarketDataAdapter>();
        adapter.Id.Returns("demo");
        adapter.Name.Returns("Demo");
        var listing = (symbolNames ?? (IReadOnlyList<string>)
        [
            "BTC/USDT",
            "ETH/BTC",
            "SOL/USDC",
        ])
            .Select(name => Symbol.Parse(name))
            .ToList();
        adapter.GetSymbolsAsync(Arg.Any<CancellationToken>()).Returns(listing);

        var activeExchanges = new ActiveExchangeService(
            [adapter],
            Substitute.For<ISettingsService>(),
            NullLogger<ActiveExchangeService>.Instance);
        var symbolList = new SymbolListProvider(
            activeExchanges,
            NullLogger<SymbolListProvider>.Instance);

        return (activeExchanges, symbolList, adapter);
    }
}
