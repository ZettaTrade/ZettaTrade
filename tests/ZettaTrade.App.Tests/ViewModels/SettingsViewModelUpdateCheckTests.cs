namespace ZettaTrade.App.Tests.ViewModels;

using System;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using FluentAssertions;
using Livery.Localization;
using Livery.Themes;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using ZettaTrade.AI.Mcp;
using ZettaTrade.App;
using ZettaTrade.App.Services;
using ZettaTrade.App.ViewModels;
using ZettaTrade.Charting;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Infrastructure.Shell;

/// <summary>
/// The settings window's Application section (T-213): the update-check
/// checkbox drafts like every other selection (seeded from the persisted
/// choice on every open, unsaved picks discarded), the explicit save
/// persists only a real difference — and no host apply is needed: the
/// checker's own change stream picks the saved flip live. An externally
/// flipped choice reaches the next open's draft through the seed cache.
/// </summary>
public sealed class SettingsViewModelUpdateCheckTests
{
    /// <summary>
    /// The persisted enable choice drives the checkbox: the stored choice
    /// seeds it, the default (on — an opt-out switch) keeps it checked.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ResetSelectionsAsync_ThePersistedChoice_SeedsTheCheckbox(bool persistedUpdateCheck)
    {
        var kit = CreateKit(persistedUpdateCheck);

        await kit.ViewModel.ResetSelectionsAsync();

        kit.ViewModel.IsUpdateCheckEnabled.Should().Be(persistedUpdateCheck);
    }

    /// <summary>
    /// An unsaved checkbox pick dies at the next open (the same draft
    /// contract every other selection keeps).
    /// </summary>
    [Fact]
    public async Task ResetSelectionsAsync_AfterAnUnsavedPick_DiscardsTheChange()
    {
        var kit = CreateKit(persistedUpdateCheck: false);

        kit.ViewModel.IsUpdateCheckEnabled = true;
        await kit.ViewModel.ResetSelectionsAsync();

        kit.ViewModel.IsUpdateCheckEnabled.Should().BeFalse();
    }

    /// <summary>
    /// Saving a flipped choice persists it and touches nothing else: the
    /// checker's own change stream applies the flip live, so the host (the
    /// only live-apply collaborator this window owns) is never asked.
    /// </summary>
    [Fact]
    public async Task SaveAsync_TheFlip_PersistsWithoutAHostCall()
    {
        var kit = CreateKit(persistedUpdateCheck: false);

        kit.ViewModel.IsUpdateCheckEnabled = true;
        await kit.ViewModel.SaveAsync();

        await kit.Settings.Received(1).SetAsync(
            AppSettingsKeys.Section,
            AppSettingsKeys.UpdateCheck,
            true);
        await kit.Host.DidNotReceiveWithAnyArgs().StartAsync(Arg.Any<CancellationToken>());
        await kit.Host.DidNotReceiveWithAnyArgs().StopAsync(Arg.Any<CancellationToken>());
        kit.ViewModel.IsUpdateCheckEnabled.Should().BeTrue("the draft stays the applied truth after the save");
    }

    /// <summary>
    /// An unchanged choice is a no-op: no settings rewrite at all (the same
    /// threshold every other row keeps).
    /// </summary>
    [Fact]
    public async Task SaveAsync_AnUnchangedChoice_StaysSilent()
    {
        var kit = CreateKit(persistedUpdateCheck: false);

        kit.ViewModel.IsUpdateCheckEnabled = false;
        await kit.ViewModel.SaveAsync();

        await kit.Settings.DidNotReceiveWithAnyArgs().SetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>());
        await kit.Host.DidNotReceiveWithAnyArgs().StartAsync(Arg.Any<CancellationToken>());
        await kit.Host.DidNotReceiveWithAnyArgs().StopAsync(Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// An externally flipped choice lands in the next open's draft through
    /// the seed cache — the change stream itself never writes anything (the
    /// checker's live re-arm is the stream's only consequence).
    /// </summary>
    [Fact]
    public async Task TheExternallyFlippedChoice_FeedsTheNextOpen_AndTheStreamStaysSilent()
    {
        var kit = CreateKit(persistedUpdateCheck: false);

        kit.Changes.OnNext(new SettingsChange(
            AppSettingsKeys.Section,
            AppSettingsKeys.UpdateCheck,
            false,
            true));

        await kit.ViewModel.ResetSelectionsAsync();
        kit.ViewModel.IsUpdateCheckEnabled.Should().BeTrue("the fresh cache seeds the next open's draft");
        await kit.Settings.DidNotReceive().SetAsync(
            AppSettingsKeys.Section,
            AppSettingsKeys.UpdateCheck,
            Arg.Any<bool>());
        await kit.Host.DidNotReceiveWithAnyArgs().StartAsync(Arg.Any<CancellationToken>());
        await kit.Host.DidNotReceiveWithAnyArgs().StopAsync(Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A non-bool payload on the choice stream leaves the cached choice in
    /// charge (the type guard every settings stream keeps).
    /// </summary>
    [Fact]
    public async Task AStreamNonBoolPayload_DoesNotPoisonTheChoice()
    {
        var kit = CreateKit(persistedUpdateCheck: false);

        kit.Changes.OnNext(new SettingsChange(
            AppSettingsKeys.Section,
            AppSettingsKeys.UpdateCheck,
            false,
            "enabled"));

        await kit.ViewModel.ResetSelectionsAsync();
        kit.ViewModel.IsUpdateCheckEnabled.Should().BeFalse();
    }

    #region Factories

    /// <summary>
    /// One wired Application-section kit: a per-key settings substitute (the
    /// clock and MCP rows' cached choices ride the generic and MCP-key
    /// matchers first, the update-check key's choice the key-specific one —
    /// the last matching setup wins), the demo exchange facade and the host
    /// + runner pair the window reads.
    /// </summary>
    private static UpdateCheckKit CreateKit(
        bool persistedUpdateCheck)
    {
        var settings = Substitute.For<ISettingsService>();
        // The clock row's cached choice (the only other bool read of the VM
        // without a key-specific matcher here).
        settings.GetAsync<bool>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns(false);
        // The MCP enable choice rides next (the VM seeds it in the
        // constructor too; these facts never exercise its rows).
        settings.GetAsync<bool>(
                Arg.Any<string>(),
                Arg.Is<string>(key => key == UiSettingsKeys.McpServerEnabled),
                Arg.Any<bool>())
            .Returns(false);
        // The update-check choice rides LAST: the last matching setup wins.
        settings.GetAsync<bool>(
                Arg.Any<string>(),
                Arg.Is<string>(key => key == AppSettingsKeys.UpdateCheck),
                Arg.Any<bool>())
            .Returns(persistedUpdateCheck);
        var changes = new Subject<SettingsChange>();
        settings.Changes.Returns(changes);

        var themeManager = Substitute.For<IThemeManager>();
        themeManager.CurrentTheme.Returns("Dark");
        themeManager.AvailableThemes.Returns([new ThemeInfo("dark", "Dark", true, [])]);
        var localization = Substitute.For<ILocalizationManager>();
        localization.CurrentLanguage.Returns("en");
        localization.AvailableLanguages.Returns([new LanguageInfo("en", "English", "English", true)]);
        var shellUtils = Substitute.For<IShellUtils>();

        var adapter = Substitute.For<IMarketDataAdapter>();
        adapter.Id.Returns("demo");
        var activeExchanges = new ActiveExchangeService(
            [adapter],
            Substitute.For<ISettingsService>(),
            NullLogger<ActiveExchangeService>.Instance);
        var symbolList = new SymbolListProvider(
            activeExchanges,
            NullLogger<SymbolListProvider>.Instance);

        var host = Substitute.For<IMcpServerHost>();
        var runner = new McpHostService(
            settings,
            host,
            NullLogger<McpHostService>.Instance);

        var catalog = Substitute.For<IIndicatorCatalog>();
        catalog.LoadedNames.Returns(["SMA"]);

        var manager = Substitute.For<IMcpClientManager>();
        manager.StatesChanged.Returns(Observable.Empty<IReadOnlyList<McpClientState>>());

        var viewModel = new SettingsViewModel(
            settings,
            themeManager,
            localization,
            shellUtils,
            runner,
            activeExchanges,
            symbolList,
            catalog,
            manager,
            NullLogger<SettingsViewModel>.Instance);

        return new UpdateCheckKit(viewModel, settings, changes, host, runner);
    }

    #endregion
}

/// <summary>One wired Application-section kit with every collaborator a test drives.</summary>
internal sealed record UpdateCheckKit(
    SettingsViewModel ViewModel,
    ISettingsService Settings,
    Subject<SettingsChange> Changes,
    IMcpServerHost Host,
    McpHostService Runner);
