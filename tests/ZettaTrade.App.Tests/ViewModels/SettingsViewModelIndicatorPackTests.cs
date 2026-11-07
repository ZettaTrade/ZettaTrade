namespace ZettaTrade.App.Tests.ViewModels;

using System.Reactive.Linq;
using FluentAssertions;
using Livery.Localization;
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
/// The standard pack's rows on the indicators tab (T-092): the six bundled
/// plugins ride the exact contract the T-067 SMA row established — the row
/// is a view over the scanned plugin catalog (it renders only for a plugin
/// the session actually loaded), a save without the plugin never fabricates
/// a config row for it, and a loaded row keeps the former save discipline:
/// a real difference rewrites the row array (the changed row appends or
/// updates, every other row rides), an unchanged or unreadable one stays
/// silent. The whole tab still lands as ONE settings write.
/// </summary>
public sealed class SettingsViewModelIndicatorPackTests
{
    #region Kit

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

    /// <summary>The full bundled set: the sample plus every pack plugin.</summary>
    private static readonly string[] TheWholeCatalog =
    [
        "SMA", "EMA", "WMA", "RSI", "MACD", "ATR", "BOLL",
    ];

    /// <summary>
    /// Set one pack row's drafts (the checkbox tick and the period text) by
    /// the row's canonical name — the settings facts key rows by name, the
    /// ViewModel surfaces them as typed properties.
    /// </summary>
    private static void DraftThePackRow(
        SettingsViewModel viewModel,
        string name,
        bool tick,
        string periodText)
    {
        switch (name)
        {
            case "EMA":
                viewModel.IsEmaEnabled = tick;
                viewModel.EmaPeriodText = periodText;
                break;
            case "WMA":
                viewModel.IsWmaEnabled = tick;
                viewModel.WmaPeriodText = periodText;
                break;
            case "RSI":
                viewModel.IsRsiEnabled = tick;
                viewModel.RsiPeriodText = periodText;
                break;
            case "MACD":
                viewModel.IsMacdEnabled = tick;
                viewModel.MacdPeriodText = periodText;
                break;
            case "ATR":
                viewModel.IsAtrEnabled = tick;
                viewModel.AtrPeriodText = periodText;
                break;
            case "BOLL":
                viewModel.IsBollEnabled = tick;
                viewModel.BollPeriodText = periodText;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(name), name, "Not a pack row name");
        }
    }

    #endregion Kit

    #region Row Derivation

    /// <summary>
    /// The drafts derive per name from the persisted rows: each row's own
    /// enable and period repaint its pair of properties, a name with no
    /// cached row reads as an unticked box beside an empty period box.
    /// </summary>
    [Fact]
    public async Task ResetSelectionsAsync_ThePackRows_DeriveFromTheirOwnedRows()
    {
        // Arrange: the shipped default plus three pack rows of mixed shape.
        var (viewModel, _) = CreateViewModel(
            TheWholeCatalog,
            persistedIndicatorConfig: """[{"name":"SMA","enabled":true,"period":20},{"name":"EMA","enabled":true,"period":9},{"name":"RSI","enabled":false,"period":14},{"name":"BOLL","enabled":false,"period":20}]""");

        // Act
        await viewModel.ResetSelectionsAsync();

        // Assert — every name reads ONLY its own row.
        viewModel.IsEmaEnabled.Should().BeTrue();
        viewModel.EmaPeriodText.Should().Be("9");
        viewModel.IsWmaEnabled.Should().BeFalse("no WMA row is cached");
        viewModel.WmaPeriodText.Should().BeEmpty("no WMA row is cached");
        viewModel.IsRsiEnabled.Should().BeFalse();
        viewModel.RsiPeriodText.Should().Be("14");
        viewModel.IsMacdEnabled.Should().BeFalse("no MACD row is cached");
        viewModel.MacdPeriodText.Should().BeEmpty("no MACD row is cached");
        viewModel.IsAtrEnabled.Should().BeFalse("no ATR row is cached");
        viewModel.AtrPeriodText.Should().BeEmpty("no ATR row is cached");
        viewModel.IsBollEnabled.Should().BeFalse();
        viewModel.BollPeriodText.Should().Be("20");
    }

    [Fact]
    public async Task ResetSelectionsAsync_WithAnEmptyConfig_ThePackRowBoxesReadEmpty()
    {
        // Arrange + Act: the shipped default carries the SMA row only.
        var (viewModel, _) = CreateViewModel(TheWholeCatalog);
        await viewModel.ResetSelectionsAsync();

        // Assert
        viewModel.IsEmaEnabled.Should().BeFalse();
        viewModel.EmaPeriodText.Should().BeEmpty();
        viewModel.IsBollEnabled.Should().BeFalse();
        viewModel.BollPeriodText.Should().BeEmpty();
    }

    #endregion Row Derivation

    #region Save Roundtrip

    /// <summary>
    /// Registration-parallel roundtrip: every pack name, ticked with a
    /// period, lands its row in the persisted array right behind the SMA
    /// row the shipped default carries — the same single-write save the
    /// SMA row has always used.
    /// </summary>
    [Theory]
    [InlineData("EMA", "9")]
    [InlineData("WMA", "10")]
    [InlineData("RSI", "14")]
    [InlineData("MACD", "12")]
    [InlineData("ATR", "14")]
    [InlineData("BOLL", "20")]
    public async Task SaveAsync_ATickedPackRow_AppendsItsRowToThePersistedArray(string name, string periodText)
    {
        // Arrange
        var (viewModel, settings) = CreateViewModel([name]);
        await viewModel.ResetSelectionsAsync();

        // Act
        DraftThePackRow(viewModel, name, tick: true, periodText);
        await viewModel.SaveAsync();

        // Assert — the exact wire shape the ChartIndicatorConfig write emits.
        var expected = $$"""[{"name":"SMA","enabled":true,"period":20},{"name":"{{name}}","enabled":true,"period":{{periodText}}}]""";
        await settings.Received(1).SetAsync(
            UiSettingsKeys.Section,
            UiSettingsKeys.ChartIndicators,
            expected);
    }

    /// <summary>
    /// One pack row's difference rewrites the array while every other row
    /// (the SMA row, the other pack rows too) rides — and the whole tab
    /// lands as exactly ONE settings write.
    /// </summary>
    [Fact]
    public async Task SaveAsync_OnePackRowDifference_RidesEveryOtherRowInOneWrite()
    {
        // Arrange: the shipped default plus an EMA row that stays put.
        var (viewModel, settings) = CreateViewModel(
            TheWholeCatalog,
            persistedIndicatorConfig: """[{"name":"SMA","enabled":true,"period":20},{"name":"EMA","enabled":true,"period":9}]""");
        await viewModel.ResetSelectionsAsync();

        // Act: only the BOLL row's drafts change.
        viewModel.IsBollEnabled = true;
        viewModel.BollPeriodText = "20";
        await viewModel.SaveAsync();

        // Assert — the SMA and EMA rows ride verbatim, the BOLL row appends.
        await settings.Received(1).SetAsync(
            UiSettingsKeys.Section,
            UiSettingsKeys.ChartIndicators,
            """[{"name":"SMA","enabled":true,"period":20},{"name":"EMA","enabled":true,"period":9},{"name":"BOLL","enabled":true,"period":20}]""");
    }

    /// <summary>
    /// An existing pack row's period update REPLACES its own entry in
    /// place of the old one (no duplicate rows ever accumulate).
    /// </summary>
    [Fact]
    public async Task SaveAsync_APeriodUpdateOnAnExistingPackRow_ReplacesItsOwnEntry()
    {
        // Arrange: the shipped default plus an EMA row at 9.
        var (viewModel, settings) = CreateViewModel(
            TheWholeCatalog,
            persistedIndicatorConfig: """[{"name":"SMA","enabled":true,"period":20},{"name":"EMA","enabled":true,"period":9}]""");
        await viewModel.ResetSelectionsAsync();

        // Act: the EMA period moves 9 -> 26 (the classic MACD slow length).
        viewModel.EmaPeriodText = "26";
        await viewModel.SaveAsync();

        // Assert — still two rows, the EMA one updated in its own entry.
        await settings.Received(1).SetAsync(
            UiSettingsKeys.Section,
            UiSettingsKeys.ChartIndicators,
            """[{"name":"SMA","enabled":true,"period":20},{"name":"EMA","enabled":true,"period":26}]""");
    }

    [Fact]
    public async Task SaveAsync_TheUnchangedPackTab_StaysSilent()
    {
        // Arrange: every plugin loaded, every draft at its cached value.
        var (viewModel, settings) = CreateViewModel(TheWholeCatalog);
        await viewModel.ResetSelectionsAsync();

        // Act
        await viewModel.SaveAsync();

        // Assert — the no-op discipline every row keeps.
        await settings.DidNotReceive().SetAsync(
            UiSettingsKeys.Section,
            UiSettingsKeys.ChartIndicators,
            Arg.Any<string>());
    }

    /// <summary>
    /// An unreadable pack period skips its row: even a ticked real
    /// difference never writes, nothing defaults (the persisted config
    /// rides untouched — a later honest save reuses the cached values).
    /// </summary>
    [Fact]
    public async Task SaveAsync_AnUnreadablePackPeriod_SkipsTheRowWithoutWriting()
    {
        // Arrange
        var (viewModel, settings) = CreateViewModel(["SMA", "EMA"]);
        await viewModel.ResetSelectionsAsync();

        // Act
        viewModel.IsEmaEnabled = true;
        viewModel.EmaPeriodText = "nine";
        await viewModel.SaveAsync();

        // Assert
        await settings.DidNotReceive().SetAsync(
            UiSettingsKeys.Section,
            UiSettingsKeys.ChartIndicators,
            Arg.Any<string>());
    }

    /// <summary>
    /// The natural zero state (no row cached, the box empty, the enable
    /// unchanged) saves nothing — silence, not a warning: the SMA row's
    /// own discipline carries over.
    /// </summary>
    [Fact]
    public async Task SaveAsync_AnEmptyUnchangedPackRow_StaysSilent()
    {
        // Arrange
        var (viewModel, settings) = CreateViewModel(["SMA", "EMA"]);
        await viewModel.ResetSelectionsAsync();

        // Act: the period box only — empty like the cached zero state.
        viewModel.EmaPeriodText = "";
        await viewModel.SaveAsync();

        // Assert
        await settings.DidNotReceive().SetAsync(
            UiSettingsKeys.Section,
            UiSettingsKeys.ChartIndicators,
            Arg.Any<string>());
    }

    /// <summary>
    /// Without the pack plugin, a programmatically dirtied draft saves
    /// nothing: the gate runs on the CATALOG's word, not on the row's
    /// visibility — a hidden row's drafts never reach the persisted config.
    /// </summary>
    [Fact]
    public async Task SaveAsync_WithoutThePackPlugin_NeverSavesTheHiddenRow()
    {
        // Arrange: the SMA sample is loaded, EMA (hidden) drafts dirtied.
        var (viewModel, settings) = CreateViewModel(["SMA"]);
        await viewModel.ResetSelectionsAsync();

        // Act
        viewModel.IsEmaEnabled = true;
        viewModel.EmaPeriodText = "9";
        await viewModel.SaveAsync();

        // Assert
        await settings.DidNotReceive().SetAsync(
            UiSettingsKeys.Section,
            UiSettingsKeys.ChartIndicators,
            Arg.Any<string>());
    }

    /// <summary>
    /// A persisted pack row without its plugin still rides a REAL write of
    /// another row: the T-067 rewrite discipline — the save rewrites only
    /// rows it owns, the foreign row rides verbatim (proven here against a
    /// SMA period change landing on the same key).
    /// </summary>
    [Fact]
    public async Task SaveAsync_AForeignPackRow_RidesARealSmaWrite()
    {
        // Arrange: the EMA row persists from an older session, its plugin
        // is gone; the SMA sample works.
        var (viewModel, settings) = CreateViewModel(
            ["SMA"],
            persistedIndicatorConfig: """[{"name":"SMA","enabled":true,"period":20},{"name":"EMA","enabled":true,"period":9}]""");
        await viewModel.ResetSelectionsAsync();

        // Act: a real SMA difference lands the write.
        viewModel.SmaPeriodText = "14";
        await viewModel.SaveAsync();

        // Assert — the EMA row rides untouched inside the rewritten array.
        await settings.Received(1).SetAsync(
            UiSettingsKeys.Section,
            UiSettingsKeys.ChartIndicators,
            """[{"name":"EMA","enabled":true,"period":9},{"name":"SMA","enabled":true,"period":14}]""");
    }

    #endregion Save Roundtrip
}
