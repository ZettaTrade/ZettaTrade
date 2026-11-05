namespace ZettaTrade.App.Tests.ViewModels;

using System.Reactive.Linq;
using FluentAssertions;
using Livery.Themes;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using ZettaTrade.AI.Mcp;
using ZettaTrade.App.Services;
using ZettaTrade.App.ViewModels;
using ZettaTrade.Charting;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Infrastructure.Configuration;
using ZettaTrade.Infrastructure.Shell;

/// <summary>
/// The indicators tab's plugin gate (T-067): the SMA row is a view over the
/// scanned plugin catalog — it renders only when the catalog actually
/// loaded the plugin, the honest empty state shows for a catalog that
/// registered zero indicators, and a save without the plugin never
/// fabricates a config row for it (the persisted config rides untouched).
/// With the plugin loaded, the row keeps its exact former save contract: a
/// real period/enable difference rewrites the row array, an unchanged one
/// stays silent.
/// </summary>
public sealed class SettingsViewModelIndicatorTests
{
    #region Kit

    /// <summary>The SMA row name the loaded stub reports (the shipped sample plugin's canonical name).</summary>
    private const string SmaIndicatorName = "SMA";

    /// <summary>One wired indicators-tab kit: the ViewModel plus the settings service spy.</summary>
    private sealed record IndicatorKit(SettingsViewModel ViewModel, ISettingsService Settings);

    /// <summary>
    /// Build a ViewModel against a catalog stub reporting exactly the given
    /// loaded set, seeded with the given persisted indicator config (the
    /// exact key the ViewModel reads — the last matching setup wins over
    /// the generic ones).
    /// </summary>
    private static IndicatorKit CreateViewModel(
        IReadOnlyList<string> loadedIndicators,
        string persistedIndicatorConfig = UiSettingsKeys.DefaultChartIndicators)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetAsync<bool>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns(false);
        settings.GetAsync<string>(Arg.Any<string>(), Arg.Any<string>())
            .Returns("");
        settings.GetAsync<string>(
                UiSettingsKeys.Section,
                UiSettingsKeys.ChartIndicators,
                UiSettingsKeys.DefaultChartIndicators)
            .Returns(persistedIndicatorConfig);

        var themeManager = Substitute.For<IThemeManager>();
        themeManager.CurrentTheme.Returns("dark");
        themeManager.AvailableThemes.Returns([new("dark", "Dark", true, [])]);
        var localization = Substitute.For<ILocalizationManager>();
        localization.CurrentLanguage.Returns("en");
        localization.AvailableLanguages.Returns([new("en", "English", "English", true)]);

        var adapter = Substitute.For<IMarketDataAdapter>();
        adapter.Id.Returns("demo");
        adapter.Name.Returns("Demo");
        var activeExchanges = new ActiveExchangeService(
            [adapter],
            Substitute.For<ISettingsService>(),
            NullLogger<ActiveExchangeService>.Instance);
        var symbolList = new SymbolListProvider(
            activeExchanges,
            NullLogger<SymbolListProvider>.Instance);

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
            CatalogOf(loadedIndicators),
            ClientManagerStub(),
            NullLogger<SettingsViewModel>.Instance);

        return new IndicatorKit(viewModel, settings);
    }

    /// <summary>The catalog stub reporting exactly the given loaded set.</summary>
    private static IIndicatorCatalog CatalogOf(IReadOnlyList<string> loadedIndicators)
    {
        var catalog = Substitute.For<IIndicatorCatalog>();
        catalog.LoadedNames.Returns(loadedIndicators);
        return catalog;
    }

    /// <summary>
    /// The external MCP client manager stub: a telemetry-less substitute the
    /// ViewModel constructor subscribes to (the indicators facts do not
    /// exercise the external-servers rows).
    /// </summary>
    private static IMcpClientManager ClientManagerStub()
    {
        var manager = Substitute.For<IMcpClientManager>();
        manager.StatesChanged.Returns(Observable.Empty<IReadOnlyList<McpClientState>>());
        return manager;
    }

    #endregion Kit

    #region Constructor Gate

    [Fact]
    public void Constructor_WithTheSmaPluginLoaded_ShowsTheRowAndHidesTheEmptyState()
    {
        // Arrange + Act
        var (viewModel, _) = CreateViewModel([SmaIndicatorName]);

        // Assert
        viewModel.IsSmaPluginLoaded.Should().BeTrue();
        viewModel.IsIndicatorCatalogEmpty.Should().BeFalse();
    }

    [Fact]
    public void Constructor_WithAnEmptyCatalog_ShowsTheEmptyStateAndHidesTheRow()
    {
        // Arrange + Act: nothing registered (missing folder or every
        // assembly skipped — the loader logs instead of throwing).
        var (viewModel, _) = CreateViewModel([]);

        // Assert
        viewModel.IsSmaPluginLoaded.Should().BeFalse();
        viewModel.IsIndicatorCatalogEmpty.Should().BeTrue();
    }

    [Fact]
    public void Constructor_WithPluginsButNoSmaRow_HidesBothSurfaces()
    {
        // Arrange + Act: plugins exist, the row is keyed by name — a row
        // surface hides, while the empty state (a statement about the whole
        // catalog) stays hidden too.
        var (viewModel, _) = CreateViewModel(["EMA"]);

        // Assert
        viewModel.IsSmaPluginLoaded.Should().BeFalse();
        viewModel.IsIndicatorCatalogEmpty.Should().BeFalse(
            "the empty state describes the whole catalog, it is not a per-row statement");
    }

    #endregion Constructor Gate

    #region Save Gate

    [Fact]
    public async Task SaveAsync_WithTheSmaPluginPresent_PersistsARealRowDifference()
    {
        // Arrange
        var (viewModel, settings) = CreateViewModel([SmaIndicatorName]);
        await viewModel.ResetSelectionsAsync();

        viewModel.IsSmaEnabled.Should().BeTrue("the default config enables the shipped row");
        viewModel.SmaPeriodText.Should().Be("20");

        // Act
        viewModel.SmaPeriodText = "14";
        await viewModel.SaveAsync();

        // Assert
        await settings.Received(1).SetAsync(
            UiSettingsKeys.Section,
            UiSettingsKeys.ChartIndicators,
            """[{"name":"SMA","enabled":true,"period":14}]""");
    }

    [Fact]
    public async Task SaveAsync_WithTheSmaPluginPresentAndAnUnchangedRow_StaysSilent()
    {
        // Arrange
        var (viewModel, settings) = CreateViewModel([SmaIndicatorName]);
        await viewModel.ResetSelectionsAsync();

        // Act
        await viewModel.SaveAsync();

        // Assert — the no-op discipline every other row keeps.
        await settings.DidNotReceive().SetAsync(
            UiSettingsKeys.Section,
            UiSettingsKeys.ChartIndicators,
            Arg.Any<string>());
    }

    [Fact]
    public async Task SaveAsync_WithTheSmaPluginAbsent_NeverFabricatesTheRow()
    {
        // Arrange: the plugins are gone while the persisted config still
        // carries other rows — a dirty draft (the row is hidden, the
        // selection sits at its defaults) must save everything EXCEPT an
        // indicators rewrite: no fabrication of a config the session does
        // not own.
        var (viewModel, settings) = CreateViewModel(
            [],
            persistedIndicatorConfig: """[{"name":"EMA","enabled":true,"period":9}]""");
        await viewModel.ResetSelectionsAsync();
        viewModel.SmaPeriodText = "30";
        viewModel.IsSmaEnabled = true;

        // Act
        await viewModel.SaveAsync();

        // Assert
        await settings.DidNotReceive().SetAsync(
            UiSettingsKeys.Section,
            UiSettingsKeys.ChartIndicators,
            Arg.Any<string>());
    }

    #endregion Save Gate
}
