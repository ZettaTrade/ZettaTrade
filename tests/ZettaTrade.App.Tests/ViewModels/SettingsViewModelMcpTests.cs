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
/// The settings window's MCP tab: the enable checkbox drafts like every other
/// selection (seeded from the persisted state on every open, unsaved picks
/// discarded), the explicit save applies and persists only a real difference
/// — and the saved state starts/stops the host live through the runner whose
/// state the three honest rows render. An externally flipped choice reaches
/// the next open's draft through the seed cache; the change stream itself
/// never rewrites anything.
/// </summary>
public sealed class SettingsViewModelMcpTests
{
    /// <summary>
    /// The persisted enable choice drives the checkbox: the stored enabled
    /// choice seeds it checked, the default keeps it unchecked.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ResetSelectionsAsync_ThePersistedEnableChoice_SeedsTheCheckbox(bool persistedMcpEnabled)
    {
        var kit = CreateKit(persistedMcpEnabled);

        await kit.ViewModel.ResetSelectionsAsync();

        kit.ViewModel.IsMcpServerEnabled.Should().Be(persistedMcpEnabled);
    }

    /// <summary>
    /// An unsaved checkbox pick dies at the next open (the same draft
    /// contract every other selection keeps).
    /// </summary>
    [Fact]
    public async Task ResetSelectionsAsync_AfterAnUnsavedPick_DiscardsTheChange()
    {
        var kit = CreateKit(persistedMcpEnabled: false);

        kit.ViewModel.IsMcpServerEnabled = true;
        await kit.ViewModel.ResetSelectionsAsync();

        kit.ViewModel.IsMcpServerEnabled.Should().BeFalse();
    }

    /// <summary>
    /// Saving a flipped choice persists it and applies the flip live: the host
    /// starts the server in-process and the honest rows surface Running.
    /// </summary>
    [Fact]
    public async Task SaveAsync_TheEnableFlip_PersistsAppliesAndSurfacesTheRunningState()
    {
        var kit = CreateKit(persistedMcpEnabled: false);

        kit.ViewModel.IsMcpServerEnabled = true;
        await kit.ViewModel.SaveAsync();

        await kit.Settings.Received(1).SetAsync(
            UiSettingsKeys.McpSection,
            UiSettingsKeys.McpServerEnabled,
            true);
        await kit.Host.Received(1).StartAsync(Arg.Any<CancellationToken>());
        kit.ViewModel.IsMcpRunning.Should().BeTrue();
        kit.ViewModel.IsMcpStopped.Should().BeFalse();
        kit.ViewModel.IsMcpFailed.Should().BeFalse();
    }

    /// <summary>
    /// An unchanged enable choice is a no-op: no settings rewrite and no
    /// host call (the same threshold every other row keeps).
    /// </summary>
    [Fact]
    public async Task SaveAsync_AnUnchangedEnableChoice_StaysSilent()
    {
        var kit = CreateKit(persistedMcpEnabled: false);

        kit.ViewModel.IsMcpServerEnabled = false;
        await kit.ViewModel.SaveAsync();

        await kit.Settings.DidNotReceiveWithAnyArgs().SetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>());
        await kit.Host.DidNotReceiveWithAnyArgs().StartAsync(Arg.Any<CancellationToken>());
        await kit.Host.DidNotReceiveWithAnyArgs().StopAsync(Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A saved disable flip stops the server live: the persisted write lands
    /// first and the host's stop follows (the bridge works both ways, and the
    /// rows surface the stopped truth).
    /// </summary>
    [Fact]
    public async Task SaveAsync_TheDisableFlip_StopsTheServerLive()
    {
        var kit = CreateKit(persistedMcpEnabled: true);

        kit.ViewModel.IsMcpServerEnabled = false;
        await kit.ViewModel.SaveAsync();

        await kit.Settings.Received(1).SetAsync(
            UiSettingsKeys.McpSection,
            UiSettingsKeys.McpServerEnabled,
            false);
        await kit.Host.Received(1).StopAsync(Arg.Any<CancellationToken>());
        kit.ViewModel.IsMcpStopped.Should().BeTrue();
    }

    /// <summary>
    /// An externally flipped enable choice lands in the next open's draft
    /// through the seed cache — the change stream never rewrites anything
    /// (the next save applies only what the user put in the dialog).
    /// </summary>
    [Fact]
    public async Task TheExternallyFlippedChoice_FeedsTheNextOpen_AndTheStreamStaysSilent()
    {
        var kit = CreateKit(persistedMcpEnabled: false);

        kit.Changes.OnNext(new SettingsChange(
            UiSettingsKeys.McpSection,
            UiSettingsKeys.McpServerEnabled,
            false,
            true));

        await kit.ViewModel.ResetSelectionsAsync();
        kit.ViewModel.IsMcpServerEnabled.Should().BeTrue("the fresh cache seeds the next open's draft");
        await kit.Settings.DidNotReceive().SetAsync(
            UiSettingsKeys.McpSection,
            UiSettingsKeys.McpServerEnabled,
            Arg.Any<bool>());
        await kit.Host.DidNotReceiveWithAnyArgs().StartAsync(Arg.Any<CancellationToken>());
        await kit.Host.DidNotReceiveWithAnyArgs().StopAsync(Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A non-bool payload on the enable stream leaves the cached choice in
    /// charge (the type guard every settings stream keeps).
    /// </summary>
    [Fact]
    public async Task AStreamNonBoolPayload_DoesNotPoisonTheChoice()
    {
        var kit = CreateKit(persistedMcpEnabled: false);

        kit.Changes.OnNext(new SettingsChange(
            UiSettingsKeys.McpSection,
            UiSettingsKeys.McpServerEnabled,
            false,
            "enabled"));

        await kit.ViewModel.ResetSelectionsAsync();
        kit.ViewModel.IsMcpServerEnabled.Should().BeFalse();
    }

    /// <summary>
    /// The host's honest state renders exactly one row at a time: a start
    /// the transport refuses keeps the Failed row on (the saved choice stays
    /// behind, so the next launch applies it).
    /// </summary>
    [Fact]
    public async Task ARefusedStart_RendersTheFailedRowAlone()
    {
        var kit = CreateKit(persistedMcpEnabled: false, refusedStart: true);

        await kit.Runner.ApplyAsync(true);

        kit.ViewModel.IsMcpFailed.Should().BeTrue();
        kit.ViewModel.IsMcpRunning.Should().BeFalse();
        kit.ViewModel.IsMcpStopped.Should().BeFalse();
    }

    #region Factories

    /// <summary>
    /// One wired MCP-tab kit: a per-key settings substitute (the clock row's
    /// cached choice rides the generic matcher first, the MCP key's choice the
    /// key-specific one — the last matching setup wins), single-row managers,
    /// the demo exchange facade and the host + runner pair the window reads.
    /// </summary>
    private static McpKit CreateKit(
        bool persistedMcpEnabled,
        bool refusedStart = false)
    {
        var settings = Substitute.For<ISettingsService>();
        // The clock row's cached choice (the only other bool read of the VM).
        settings.GetAsync<bool>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns(false);
        // The MCP enable choice rides LAST: the last matching setup wins.
        settings.GetAsync<bool>(
                Arg.Any<string>(),
                Arg.Is<string>(key => key == UiSettingsKeys.McpServerEnabled),
                Arg.Any<bool>())
            .Returns(persistedMcpEnabled);
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
        if (refusedStart)
        {
            host.StartAsync(Arg.Any<CancellationToken>())
                .Returns(FailedTask(new InvalidOperationException("transport busy")));
        }

        var runner = new McpHostService(
            settings,
            host,
            NullLogger<McpHostService>.Instance);

        var viewModel = new SettingsViewModel(
            settings,
            themeManager,
            localization,
            shellUtils,
            runner,
            activeExchanges,
            symbolList,
            SmaLoadedCatalog(),
            ClientManagerStub(),
            NullLogger<SettingsViewModel>.Instance);

        return new McpKit(viewModel, settings, changes, host, runner);
    }

    /// <summary>
    /// A catalog stub that reports the SMA plugin as loaded: the same-state
    /// default (the shipped application carries the sample plugin). The
    /// MCP facts do not exercise the indicator rows, but the ViewModel
    /// constructor requires the gate's collaborator.
    /// </summary>
    private static IIndicatorCatalog SmaLoadedCatalog()
    {
        var catalog = Substitute.For<IIndicatorCatalog>();
        catalog.LoadedNames.Returns(["SMA"]);
        return catalog;
    }

    /// <summary>A task-shaped failure the host stub refuses with.</summary>
    private static Task FailedTask(Exception error) =>
        Task.FromException<object?>(error);

    /// <summary>
    /// The external MCP client manager stub: a telemetry-less substitute the
    /// ViewModel constructor subscribes to (the MCP host facts do not
    /// exercise the external-servers rows).
    /// </summary>
    private static IMcpClientManager ClientManagerStub()
    {
        var manager = Substitute.For<IMcpClientManager>();
        manager.StatesChanged.Returns(Observable.Empty<IReadOnlyList<McpClientState>>());
        return manager;
    }

    #endregion
}

/// <summary>One wired MCP-tab kit with every collaborator a test drives.</summary>
internal sealed record McpKit(
    SettingsViewModel ViewModel,
    ISettingsService Settings,
    Subject<SettingsChange> Changes,
    IMcpServerHost Host,
    McpHostService Runner);

