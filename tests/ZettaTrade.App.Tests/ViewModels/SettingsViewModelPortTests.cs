namespace ZettaTrade.App.Tests.ViewModels;

using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
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

/// <summary>The half-day-agnostic status byte ReactiveUI.Primitives names RxVoid.</summary>
using RxVoid = ReactiveUI.Primitives.RxVoid;

/// <summary>
/// The settings window's port tab (T-100) over a real store and port: the
/// validated import drafts a dry-run preview that unlocks the apply, a
/// rejected file surfaces the localized honesty row and touches nothing, the
/// apply walks the one locked batch live, and the factory reset publishes
/// its outcome through the typed confirmation — a decline writes nothing.
/// </summary>
public sealed class SettingsViewModelPortTests
{
    #region The tab's honest posture

    /// <summary>Without the port surface the tab simply does not exist (the honest DI posture). </summary>
    [Fact]
    public void WithoutThePortSurface_TheTabStaysAway()
    {
        using var kit = new SettingsViewModelPortKit(withPort: false);

        kit.ViewModel.IsPortAvailable.Should().BeFalse();
        kit.ViewModel.IsImportPreviewVisible.Should().BeFalse();
        kit.ViewModel.HasImportError.Should().BeFalse();
        kit.ViewModel.HasPortStatusText.Should().BeFalse();
    }

    /// <summary>With the port and the confirmation injected, the tab exists.</summary>
    [Fact]
    public void WithThePortSurface_TheTabIsAvailable()
    {
        using var kit = new SettingsViewModelPortKit(withPort: true);

        kit.ViewModel.IsPortAvailable.Should().BeTrue();
    }

    #endregion The tab's honest posture

    #region The draft: the dry-run preview

    /// <summary>
    /// A validated import drafts its preview rows and unlocks the apply, and
    /// the store stays behind until the user's apply.
    /// </summary>
    [Fact]
    public async Task AValidDraft_FillsThePreview_UnlocksTheApply_AndWritesNothingYet()
    {
        using var kit = new SettingsViewModelPortKit(withPort: true);

        await kit.ViewModel.DraftImportAsync(
            PortDocument("\"UI\": { \"ChartUse12HourClock\": true, \"BookPanelWidth\": 360 }"));

        kit.ViewModel.ImportPreview
            .Select(row => row.Key)
            .Should()
            .BeEquivalentTo([UiSettingsKeys.ChartUse12HourClock, UiSettingsKeys.BookPanelWidth]);
        kit.ViewModel.IsImportPreviewVisible.Should().BeTrue("the plan is pending the user's apply");
        kit.ViewModel.HasImportError.Should().BeFalse();
        (await kit.ViewModel.ApplyImportCommand.CanExecute.FirstAsync())
            .Should().BeTrue("a pending plan unlocks the apply");

        (await kit.Settings.GetAsync<bool>(UiSettingsKeys.Section, UiSettingsKeys.ChartUse12HourClock, false))
            .Should().BeFalse("the draft writes nothing before the apply");
    }

    /// <summary>
    /// A rejected draft surfaces the localized honesty row with the typed
    /// reason's key, keeps the preview away and the apply locked.
    /// </summary>
    [Fact]
    public async Task ARejectedDraft_AnswersTheHonestyRow_AndLocksTheApply()
    {
        using var kit = new SettingsViewModelPortKit(withPort: true);
        await kit.ViewModel.DraftImportAsync(
            PortDocument("\"UI\": { \"ChartUse12HourClock\": true }"));

        await kit.ViewModel.DraftImportAsync(
            PortDocument("\"Fauna\": { \"Mystery\": 1 }"));

        kit.ViewModel.HasImportError.Should().BeTrue();
        kit.ViewModel.ImportErrorText
            .Should()
            .Contain("Port.Error.UnknownEntry");
        kit.ViewModel.IsImportPreviewVisible.Should().BeFalse();
        kit.ViewModel.ImportPreview.Should().BeEmpty("the former draft died with the rejection");
        (await kit.ViewModel.ApplyImportCommand.CanExecute.FirstAsync())
            .Should().BeFalse("a rejected file leaves nothing to apply");

        (await kit.Settings.GetAsync<bool>(UiSettingsKeys.Section, UiSettingsKeys.ChartUse12HourClock, false))
            .Should().BeFalse("the rejection wrote nothing");
    }

    /// <summary>An import that validates but changes nothing tells it out loud.</summary>
    [Fact]
    public async Task ANoChangeImport_SpeaksItself()
    {
        using var kit = new SettingsViewModelPortKit(withPort: true);
        await kit.ViewModel.ExportToFileAsync(Path.Combine(kit.Directory, "baseline.ztsettings"));

        var exported = await File.ReadAllTextAsync(Path.Combine(kit.Directory, "baseline.ztsettings"));
        await kit.ViewModel.DraftImportAsync(exported);

        kit.ViewModel.IsImportPreviewVisible.Should().BeFalse("nothing to show");
        kit.ViewModel.PortStatusText
            .Should()
            .Contain("Port.ImportNoChange");
        kit.ViewModel.HasImportError.Should().BeFalse();
    }

    #endregion The draft: the dry-run preview

    #region The apply

    /// <summary>
    /// The apply walks the one locked batch: the store holds the imported
    /// values the moment the command returns (the live re-apply the
    /// acceptance asks for), the preview clears and the status line reports.
    /// </summary>
    [Fact]
    public async Task AnApply_LandsTheValuesLive_AndClearsThePreview()
    {
        using var kit = new SettingsViewModelPortKit(withPort: true);
        await kit.ViewModel.DraftImportAsync(
            PortDocument("\"UI\": { \"ChartUse12HourClock\": true, \"BookPanelWidth\": 360 }"));

        await kit.ViewModel.ApplyImportCommand.Execute(RxVoid.Default);

        (await kit.Settings.GetAsync<bool>(UiSettingsKeys.Section, UiSettingsKeys.ChartUse12HourClock))
            .Should().BeTrue("the imported choice is in the store");
        (await kit.Settings.GetAsync<double>(UiSettingsKeys.Section, UiSettingsKeys.BookPanelWidth))
            .Should().Be(360);
        kit.ViewModel.IsImportPreviewVisible.Should().BeFalse();
        kit.ViewModel.ImportPreview.Should().BeEmpty();
        kit.ViewModel.PortStatusText
            .Should()
            .Contain("Port.ImportApplied");
    }

    #endregion The apply

    #region The factory reset

    /// <summary>A declined reset resolves the gate once and writes nothing.</summary>
    [Fact]
    public async Task ADeclinedReset_ResolvesTheGate_AndWritesNothing()
    {
        using var kit = new SettingsViewModelPortKit(withPort: true);
        await kit.Settings.SetAsync(UiSettingsKeys.Section, UiSettingsKeys.Theme, "trading-pro");
        kit.Confirmations
            .RequestConfirmationAsync(Arg.Any<ConfirmationRequest>(), Arg.Any<CancellationToken>())
            .Returns(false);

        await kit.ViewModel.ResetToDefaultsAsync();

        await kit.Confirmations.Received(1).RequestConfirmationAsync(
            Arg.Is<ConfirmationRequest>(request => request.MessageKey == SettingsPortService.ResetConfirmMessageKey),
            Arg.Any<CancellationToken>());
        kit.ViewModel.PortStatusText
            .Should()
            .Contain("Port.ResetDeclined");
        (await kit.Settings.GetAsync<string>(UiSettingsKeys.Section, UiSettingsKeys.Theme))
            .Should().Be("trading-pro", "a declined gate leaves the tuned choice standing");
    }

    /// <summary>An accepted reset stamps the defaults the store held back.</summary>
    [Fact]
    public async Task AnAcceptedReset_StampsTheDefaults()
    {
        using var kit = new SettingsViewModelPortKit(withPort: true);
        await kit.Settings.SetAsync(UiSettingsKeys.Section, UiSettingsKeys.Theme, "trading-pro");
        await kit.Settings.SetAsync(UiSettingsKeys.Section, UiSettingsKeys.ChartUse12HourClock, true);
        kit.Confirmations
            .RequestConfirmationAsync(Arg.Any<ConfirmationRequest>(), Arg.Any<CancellationToken>())
            .Returns(true);

        await kit.ViewModel.ResetToDefaultsAsync();

        kit.ViewModel.PortStatusText
            .Should()
            .Contain("Port.ResetDone");
        (await kit.Settings.GetAsync<bool>(UiSettingsKeys.Section, UiSettingsKeys.ChartUse12HourClock))
            .Should().BeFalse("the reset stamps the shipped default back");
        (await kit.Settings.GetAsync<string>(UiSettingsKeys.Section, UiSettingsKeys.Theme))
            .Should().BeNull("the theme's default is the fresh-install absence");
    }

    #endregion The factory reset

    #region Factories

    /// <summary>Wrap a settings payload into a full (schema, version, settings) document.</summary>
    private static string PortDocument(string settingsBody) =>
        $$"""
        {
          "schema": "{{SettingsPortService.SchemaMarker}}",
          "version": {{SettingsPortService.SchemaVersion}},
          "settings": { {{settingsBody}} }
        }
        """;

    #endregion Factories
}

/// <summary>
/// One wired port-tab kit (T-100): a real store over a fresh temp directory,
/// the real port over it, substitute managers and dialogs, and a
/// localization whose answers are the keys themselves (every outcome row is
/// assertable by its key).
/// </summary>
internal sealed class SettingsViewModelPortKit : IDisposable
{
    /// <summary>Create the kit: the store, the port and the ViewModel over them.</summary>
    public SettingsViewModelPortKit(bool withPort)
    {
        Directory = Path.Combine(Path.GetTempPath(), "t100-vm-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Directory);
        Settings = new SettingsService(
            Path.Combine(Directory, "settings.json"),
            NullLogger<SettingsService>.Instance);
        Port = new SettingsPortService(Settings, NullLogger<SettingsPortService>.Instance);
        Confirmations = Substitute.For<IConfirmationService>();

        var themeManager = Substitute.For<IThemeManager>();
        themeManager.CurrentTheme.Returns("Dark");
        themeManager.AvailableThemes.Returns([new ThemeInfo("dark", "Dark", true, [])]);
        var localization = Substitute.For<ILocalizationManager>();
        localization.CurrentLanguage.Returns("en");
        localization.AvailableLanguages.Returns([new LanguageInfo("en", "English", "English", true)]);
        localization.GetString(Arg.Any<string>()).Returns(callInfo => callInfo.ArgAt<string>(0));
        localization.GetString(Arg.Any<string>(), Arg.Any<object[]>())
            .Returns(callInfo => callInfo.ArgAt<string>(0));

        var adapter = Substitute.For<IMarketDataAdapter>();
        adapter.Id.Returns("demo");
        var activeExchanges = new ActiveExchangeService(
            [adapter],
            Substitute.For<ISettingsService>(),
            NullLogger<ActiveExchangeService>.Instance);

        SmaCatalog = Substitute.For<IIndicatorCatalog>();
        SmaCatalog.LoadedNames.Returns(["SMA"]);
        var clients = Substitute.For<IMcpClientManager>();
        clients.StatesChanged.Returns(Observable.Empty<IReadOnlyList<McpClientState>>());

        ViewModel = new SettingsViewModel(
            Settings,
            themeManager,
            localization,
            Substitute.For<IShellUtils>(),
            new McpHostService(
                Substitute.For<ISettingsService>(),
                Substitute.For<IMcpServerHost>(),
                NullLogger<McpHostService>.Instance),
            activeExchanges,
            new SymbolListProvider(activeExchanges, NullLogger<SymbolListProvider>.Instance),
            SmaCatalog,
            clients,
            NullLogger<SettingsViewModel>.Instance,
            settingsPort: withPort ? Port : null,
            confirmations: withPort ? Confirmations : null);
    }

    /// <summary>The temp directory the store's file lives in (removed on dispose).</summary>
    public string Directory { get; }

    /// <summary>The real store the port rides (also the ViewModel's own settings face).</summary>
    public SettingsService Settings { get; }

    /// <summary>The real port service.</summary>
    public SettingsPortService Port { get; }

    /// <summary>The typed confirmation substitute the reset gates itself behind.</summary>
    public IConfirmationService Confirmations { get; }

    /// <summary>The ViewModel under test.</summary>
    public SettingsViewModel ViewModel { get; }

    /// <summary>The indicator catalog substitute the constructor's rows read.</summary>
    public IIndicatorCatalog SmaCatalog { get; }

    /// <summary>Stop the store's watcher and remove the temp directory.</summary>
    public void Dispose()
    {
        Settings.Dispose();
        System.IO.Directory.Delete(Directory, recursive: true);
    }
}

