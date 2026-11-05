namespace ZettaTrade.App.Tests.ViewModels;

using System.Reactive.Linq;
using System.Reactive.Subjects;
using FluentAssertions;
using Livery.Themes;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ReactiveUI.Primitives;
using Xunit;
using ZettaTrade.AI.Mcp;
using ZettaTrade.App;
using ZettaTrade.App.Services;
using ZettaTrade.App.ViewModels;
using ZettaTrade.Charting;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.Infrastructure.Configuration;
using ZettaTrade.Infrastructure.Shell;

/// <summary>
/// Tests for the settings window: the selections start at the managers'
/// applied state (reset on every open, unsaved picks are discarded), and the
/// explicit save applies and persists only real differences — an unchanged
/// selection produces no manager calls and no settings rewrite. The pairs
/// tab manages both workspace marks as drafts: a hide or reveal move and a
/// row star toggle only repaint the two lists, Save alone parses the drafts
/// against the applied maps and persists a real difference (the other
/// exchanges' entries ride). A starred pair never hides. The themes folder
/// button must hand the config themes directory to the injected shell
/// abstraction, and theme load errors surface as a live warning list.
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
            new McpHostService(
                settings,
                Substitute.For<IMcpServerHost>(),
                NullLogger<McpHostService>.Instance),
            activeExchanges,
            symbolList,
            SmaLoadedCatalog(),
            ClientManagerStub(),
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
            new McpHostService(
                settings,
                Substitute.For<IMcpServerHost>(),
                NullLogger<McpHostService>.Instance),
            activeExchanges,
            symbolList,
            SmaLoadedCatalog(),
            ClientManagerStub(),
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
    /// listing (minus the persisted hidden names) on the left and the
    /// persisted hidden names (right), the exchange's guard order preserved.
    /// </summary>
    [Fact]
    public async Task ResetSelectionsAsync_PopulatesTheTwoPairLists()
    {
        var (viewModel, _, _, _, _, _, _, _) = CreateViewModel(
            currentTheme: "Dark",
            currentLanguage: "en",
            persistedHiddenMap: """{"demo":["SOL/USDC"]}""");

        await viewModel.ResetSelectionsAsync();

        AllPairNames(viewModel).Should().Equal("BTC/USDT", "ETH/BTC");
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
        PickAllPair(viewModel, "ETH/BTC");
        await viewModel.HidePairCommand.Execute(RxVoid.Default);

        AllPairNames(viewModel).Should().Equal("BTC/USDT");
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

        PickAllPair(viewModel, "ETH/BTC");
        await viewModel.HidePairCommand.Execute(RxVoid.Default);
        await viewModel.SaveAsync();

        await settings.Received(1).SetAsync(
            UiSettingsKeys.WorkspaceSection,
            UiSettingsKeys.WorkspaceHiddenSymbols,
            """{"demo":["ETH/BTC","SOL/USDC"]}""");
    }

    /// <summary>
    /// The favorites draft: a row star toggle only repaints the lists — the
    /// starred pair pins to the head, the pick rides the toggled row, and
    /// nothing persists until Save.
    /// </summary>
    [Fact]
    public async Task TheStarToggle_PinsThePairToTheHead_WithoutAnySettingsWrite()
    {
        var (viewModel, settings, _, _, _, _, _, _) =
            CreateViewModel(
                currentTheme: "Dark",
                currentLanguage: "en",
                persistedFavoriteMap: """{"demo":["BTC/USDT"]}""");

        await viewModel.ResetSelectionsAsync();
        AllPairNames(viewModel).Should().Equal("BTC/USDT", "ETH/BTC", "SOL/USDC");
        viewModel.AllPairs[0].IsFavorite.Should().BeTrue("the persisted star came from the applied map");

        await viewModel.TogglePairFavoriteCommand.Execute(viewModel.AllPairs.First(row => row.Name == "SOL/USDC"));

        AllPairNames(viewModel).Should().Equal("BTC/USDT", "SOL/USDC", "ETH/BTC");
        viewModel.AllPairs[1].IsFavorite.Should().BeTrue("the starred pair pins to the head");
        viewModel.AllPairs[1].IsFavorite.Should().BeTrue();
        viewModel.HiddenPairs.Should().BeEmpty("stars and hidden never share a row");
        viewModel.SelectedAllPair.Should()
            .Be(new PairRow("SOL/USDC", IsFavorite: true), "the pick rides the toggled row");
        await settings.DidNotReceive().SetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    /// <summary>
    /// The save applies the favorites draft: the map is rewritten with the
    /// draft exchange's entry replaced (sorted), and nothing is written
    /// while the draft matches the applied state.
    /// </summary>
    [Fact]
    public async Task SaveAsync_TheFavoritesDraft_PersistsOnlyARealDifference()
    {
        var (viewModel, settings, _, _, _, _, _, _) =
            CreateViewModel(
                currentTheme: "Dark",
                currentLanguage: "en",
                persistedFavoriteMap: """{"demo":["BTC/USDT"]}""");

        await viewModel.ResetSelectionsAsync();
        await viewModel.SaveAsync();

        await settings.DidNotReceive().SetAsync(
            UiSettingsKeys.WorkspaceSection,
            UiSettingsKeys.WorkspaceFavoriteSymbols,
            Arg.Any<string>());

        await viewModel.TogglePairFavoriteCommand.Execute(viewModel.AllPairs.First(row => row.Name == "ETH/BTC"));
        await viewModel.SaveAsync();

        await settings.Received(1).SetAsync(
            UiSettingsKeys.WorkspaceSection,
            UiSettingsKeys.WorkspaceFavoriteSymbols,
            """{"demo":["BTC/USDT","ETH/BTC"]}""");

        // The second save is a no-op: the cache took the written document.
        await viewModel.SaveAsync();
        await settings.Received(1).SetAsync(
            UiSettingsKeys.WorkspaceSection,
            UiSettingsKeys.WorkspaceFavoriteSymbols,
            Arg.Any<string>());
    }

    /// <summary>
    /// A starred pick never hides: the '>' move's gate refuses the pick
    /// while the row is starred and arms it again after the unstar.
    /// </summary>
    [Fact]
    public async Task TheHideMove_RefusesAStarredPick()
    {
        var (viewModel, _, _, _, _, _, _, _) = CreateViewModel(currentTheme: "Dark", currentLanguage: "en");

        await viewModel.ResetSelectionsAsync();
        PickAllPair(viewModel, "BTC/USDT");

        bool hideEnabled = true;
        using var hideSubscription = viewModel.HidePairCommand.CanExecute
            .Subscribe(value => hideEnabled = value);
        hideEnabled.Should().BeTrue("an unstarred pick arms the hide move");

        await viewModel.TogglePairFavoriteCommand.Execute(viewModel.AllPairs.First(row => row.Name == "BTC/USDT"));

        hideEnabled.Should().BeFalse("a starred pick never hides until the star is removed");
        viewModel.HiddenPairs.Should().BeEmpty("the refused move left no draft trace");

        await viewModel.TogglePairFavoriteCommand.Execute(viewModel.AllPairs.First(row => row.Name == "BTC/USDT"));

        hideEnabled.Should().BeTrue("the unstarred pick arms the hide move again");
    }

    /// <summary>
    /// The marks are mutually exclusive by rule: a legacy document may hold
    /// one name in BOTH maps (the earlier releases' shape freedom). The
    /// star wins the render — the pair shows in the all-pairs list (pinned)
    /// and never in the hidden list — and a save with the star removed
    /// repairs the document: the favorites map drops the name while the
    /// hidden map keeps it (the hidden row shows again, then).
    /// </summary>
    [Fact]
    public async Task ALegacyCollision_TheFavoriteWinsUntilTheStarIsRemoved()
    {
        var (viewModel, settings, _, _, _, _, _, _) =
            CreateViewModel(
                currentTheme: "Dark",
                currentLanguage: "en",
                persistedHiddenMap: """{"demo":["ETH/BTC"]}""",
                persistedFavoriteMap: """{"demo":["ETH/BTC"]}""");

        await viewModel.ResetSelectionsAsync();

        AllPairNames(viewModel).Should().Equal("ETH/BTC", "BTC/USDT", "SOL/USDC");
        viewModel.AllPairs[0].IsFavorite.Should().BeTrue();
        viewModel.HiddenPairs.Should().BeEmpty("a starred pair never renders as hidden");

        await viewModel.TogglePairFavoriteCommand.Execute(viewModel.AllPairs[0]);
        await viewModel.SaveAsync();

        // The unstar re-opens the hidden side: the favorites document drops
        // the name; the hidden document is unchanged (no rewrite).
        AllPairNames(viewModel).Should().Equal("BTC/USDT", "SOL/USDC");
        viewModel.HiddenPairs.Should().Equal("ETH/BTC");
        await settings.Received(1).SetAsync(
            UiSettingsKeys.WorkspaceSection,
            UiSettingsKeys.WorkspaceFavoriteSymbols,
            "{}");
        await settings.DidNotReceive().SetAsync(
            UiSettingsKeys.WorkspaceSection,
            UiSettingsKeys.WorkspaceHiddenSymbols,
            Arg.Any<string>());
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

        AllPairNames(viewModel).Should().Equal("BTC/USDT", "ETH/BTC", "SOL/USDC");
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

        AllPairNames(viewModel).Should().Equal("BTC/USDT", "ETH/BTC", "SOL/USDC");
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

        PickAllPair(viewModel, "ETH/BTC");
        hideEnabled.Should().BeTrue("the all-pairs pick arms the hide move");
        revealEnabled.Should().BeFalse();

        viewModel.SelectedAllPair = null;
        viewModel.SelectedHiddenPair = "SOL/USDC";
        hideEnabled.Should().BeFalse();
        revealEnabled.Should().BeTrue("the hidden pick arms the reveal move");
    }

    /// <summary>
    /// The workspace keeps at least one visible pair: with the last visible
    /// row picked, the hide move stays disarmed (the '>' button grays out) —
    /// a fully hidden list would leave the chart without any tradable
    /// context.
    /// </summary>
    [Fact]
    public async Task TheHideMove_RequiresMoreThanOneVisiblePair()
    {
        var (viewModel, _, _, _, _, _, _, _) =
            CreateViewModel(
                currentTheme: "Dark",
                currentLanguage: "en",
                persistedHiddenMap: """{"demo":["ETH/BTC","SOL/USDC"]}""");

        await viewModel.ResetSelectionsAsync();
        AllPairNames(viewModel).Should().Equal("BTC/USDT");

        bool hideEnabled = true;
        using var hideSubscription = viewModel.HidePairCommand.CanExecute
            .Subscribe(value => hideEnabled = value);

        PickAllPair(viewModel, "BTC/USDT");

        hideEnabled.Should().BeFalse("the last visible pair never hides");
    }

    /// <summary>
    /// A hide move carries the pick to the pair that took the moved row's
    /// slot: the next row heading up, the (new) last row when the tail
    /// moved — the keyboard flow never falls back to an unpicked list.
    /// </summary>
    [Fact]
    public async Task TheHideMove_CarriesThePickToTheMovedRowsSlot()
    {
        var (viewModel, _, _, _, _, _, _, _) = CreateViewModel(currentTheme: "Dark", currentLanguage: "en");

        await viewModel.ResetSelectionsAsync();

        PickAllPair(viewModel, "ETH/BTC");
        await viewModel.HidePairCommand.Execute(RxVoid.Default);

        viewModel.SelectedAllPair.Should()
            .Be(new PairRow("SOL/USDC", IsFavorite: false), "the next row heading up takes the moved slot");
        AllPairNames(viewModel).Should().Equal("BTC/USDT", "SOL/USDC");

        PickAllPair(viewModel, "SOL/USDC");
        await viewModel.HidePairCommand.Execute(RxVoid.Default);

        viewModel.SelectedAllPair.Should()
            .Be(new PairRow("BTC/USDT", IsFavorite: false), "the (new) last row takes the pick at the tail");
        AllPairNames(viewModel).Should().Equal("BTC/USDT");
    }

    /// <summary>
    /// The reveal move carries the pick the same way; draining the whole
    /// hidden list releases the pick (nothing left to select).
    /// </summary>
    [Fact]
    public async Task TheRevealMove_CarriesThePickToTheMovedRowsSlot()
    {
        var (viewModel, _, _, _, _, _, _, _) =
            CreateViewModel(
                currentTheme: "Dark",
                currentLanguage: "en",
                persistedHiddenMap: """{"demo":["ETH/BTC","SOL/USDC"]}""");

        await viewModel.ResetSelectionsAsync();
        viewModel.HiddenPairs.Should().Equal("ETH/BTC", "SOL/USDC");

        viewModel.SelectedHiddenPair = "SOL/USDC";
        await viewModel.RevealPairCommand.Execute(RxVoid.Default);

        viewModel.SelectedHiddenPair.Should().Be("ETH/BTC", "the next row heading up takes the pick");
        viewModel.HiddenPairs.Should().Equal("ETH/BTC");

        viewModel.SelectedHiddenPair = "ETH/BTC";
        await viewModel.RevealPairCommand.Execute(RxVoid.Default);

        viewModel.SelectedHiddenPair.Should().BeNull("the drained hidden list holds nothing selectable");
        viewModel.HiddenPairs.Should().BeEmpty();
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

    /// <summary>
    /// Pick one all-pairs row by its name (the pick is a row record, the
    /// tests address rows by name).
    /// </summary>
    private static void PickAllPair(SettingsViewModel viewModel, string name) =>
        viewModel.SelectedAllPair = viewModel.AllPairs.First(row => row.Name == name);

    /// <summary>
    /// The names the all-pairs list holds (its rows' site order).
    /// </summary>
    private static IEnumerable<string> AllPairNames(SettingsViewModel viewModel) =>
        viewModel.AllPairs.Select(row => row.Name);

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
            string persistedFavoriteMap = "",
            IReadOnlyList<string>? symbolNames = null,
            bool listingFails = false)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetAsync<bool>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns(persistedUse12HourClock);
        settings.GetAsync<string>(Arg.Any<string>(), Arg.Any<string>())
            .Returns(persistedHiddenMap);
        // The two workspace marks live in two keys: the specific setups must
        // ride AFTER the generic one (the last matching setup wins).
        settings.GetAsync<string>(
                UiSettingsKeys.WorkspaceSection,
                UiSettingsKeys.WorkspaceHiddenSymbols)
            .Returns(persistedHiddenMap);
        settings.GetAsync<string>(
                UiSettingsKeys.WorkspaceSection,
                UiSettingsKeys.WorkspaceFavoriteSymbols)
            .Returns(persistedFavoriteMap);
        var themeManager = Substitute.For<IThemeManager>();
        themeManager.CurrentTheme.Returns(currentTheme);
        themeManager.AvailableThemes.Returns(Themes);
        var localization = Substitute.For<ILocalizationManager>();
        localization.CurrentLanguage.Returns(currentLanguage);
        localization.AvailableLanguages.Returns(Languages);
        var shellUtils = Substitute.For<IShellUtils>();
        themeLoadErrors ??= new BehaviorSubject<IReadOnlyList<ThemeFileLoadError>>([]);

        // The MCP runner stands un-initialized: the ctor subscription rides
        // its replaying state (Stopped), the host never starts in these tests.
        var mcpHosts = new McpHostService(
            settings,
            Substitute.For<IMcpServerHost>(),
            NullLogger<McpHostService>.Instance);

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
            mcpHosts,
            activeExchanges,
            symbolList,
            SmaLoadedCatalog(),
            ClientManagerStub(),
            Substitute.For<Microsoft.Extensions.Logging.ILogger<SettingsViewModel>>(),
            themeLoadErrors);

        return (viewModel, settings, themeManager, localization, shellUtils, themeLoadErrors, activeExchanges, symbolList);
    }

    /// <summary>
    /// A catalog stub that reports the SMA plugin as loaded: the same-state
    /// default (the shipped application carries the sample plugin).
    /// </summary>
    private static IIndicatorCatalog SmaLoadedCatalog()
    {
        var catalog = Substitute.For<IIndicatorCatalog>();
        catalog.LoadedNames.Returns(["SMA"]);
        return catalog;
    }

    /// <summary>
    /// The external MCP client manager stub: a telemetry-less substitute the
    /// ViewModel constructor subscribes to (the settings tests do not
    /// exercise the external-servers rows).
    /// </summary>
    private static IMcpClientManager ClientManagerStub()
    {
        var manager = Substitute.For<IMcpClientManager>();
        manager.StatesChanged.Returns(Observable.Empty<IReadOnlyList<McpClientState>>());
        return manager;
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
