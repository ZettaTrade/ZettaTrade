namespace ZettaTrade.App.Tests.ViewModels;

using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
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
using ZettaTrade.Infrastructure.Shell;

/// <summary>
/// The settings' external-servers block (T-068): the drafts seed from the
/// persisted list on every open and carry the manager's snapshots as
/// per-entry state rows; the save maps the rows into the persisted list
/// verbatim (a blank-named row drops with its logged warning — a nameless
/// entry keys nothing), applies the list live through the manager and — the
/// same no-op discipline every other row keeps — an unchanged save stays
/// silent (no rewrite, no apply, no redial).
/// </summary>
public sealed class SettingsViewModelMcpClientTests
{
    #region Kit

    /// <summary>The hand-written manager double: captures applies, publishes states on demand.</summary>
    private sealed class FakeManager : IMcpClientManager
    {
        private IReadOnlyList<McpClientState> _states = [];

        public IReadOnlyList<McpClientState> States => _states;

        /// <summary>Overrides <see cref="States"/> for the seed — the rows read this snapshot.</summary>
        public void SeedStates(params McpClientState[] states) => _states = states;

        public Subject<IReadOnlyList<McpClientState>> Stream { get; } = new();

        public IObservable<IReadOnlyList<McpClientState>> StatesChanged => Stream;

        public List<IReadOnlyList<McpServerConfig>> Applied { get; } = [];

        public Task ApplyAsync(IReadOnlyList<McpServerConfig> servers, CancellationToken ct = default)
        {
            Applied.Add(servers);
            return Task.CompletedTask;
        }

        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>One wired external-servers kit: the ViewModel, the settings spy and the manager double.</summary>
    private sealed record McpKit(SettingsViewModel ViewModel, ISettingsService Settings, FakeManager Manager);

    /// <summary>Build a ViewModel whose persisted external-server value is the given JSON.</summary>
    private static McpKit CreateKit(string persistedServersJson = McpClientManager.DefaultServersJson)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetAsync<bool>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns(false);
        settings.GetAsync<string>(Arg.Any<string>(), Arg.Any<string>())
            .Returns("");
        settings.GetAsync<string>(
                McpClientManager.ServersSection,
                McpClientManager.ServersKey,
                McpClientManager.DefaultServersJson)
            .Returns(persistedServersJson);

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

        var manager = new FakeManager();
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
            CatalogOf(),
            manager,
            NullLogger<SettingsViewModel>.Instance);

        return new McpKit(viewModel, settings, manager);
    }

    /// <summary>The catalog stub reporting the shipped SMA plugin as loaded (the rows' gate rides another suite).</summary>
    private static IIndicatorCatalog CatalogOf()
    {
        var catalog = Substitute.For<IIndicatorCatalog>();
        catalog.LoadedNames.Returns(["SMA"]);
        return catalog;
    }

    #endregion Kit

    #region Seed

    /// <summary>The open seeds the rows from the persisted list: the fields ride verbatim.</summary>
    [Fact]
    public async Task ResetSelectionsAsync_ThePersistedList_SeedsTheEditorRows()
    {
        var kit = CreateKit(
            """[{"name":"local","transport":"stdio","command":"mcp-echo","args":["--x","1"],"enabled":true},{"name":"remote","transport":"streamableHttp","enabled":false}]""");

        await kit.ViewModel.ResetSelectionsAsync();

        kit.ViewModel.McpServers.Should().HaveCount(2);
        kit.ViewModel.McpServers[0].Name.Should().Be("local");
        kit.ViewModel.McpServers[0].IsStdio.Should().BeTrue("the row's conditional fields key by the transport");
        kit.ViewModel.McpServers[0].Command.Should().Be("mcp-echo");
        kit.ViewModel.McpServers[0].IsEnabled.Should().BeTrue();

        kit.ViewModel.McpServers[1].IsHttp.Should().BeTrue();
        kit.ViewModel.McpServers[1].IsEnabled.Should().BeFalse(
            "a saved disabled entry is UNCHECKED in the editor");
    }

    /// <summary>The row's state rides the manager's snapshot: the name keys the match, an unsaved row stays "not known".</summary>
    [Fact]
    public async Task ResetSelectionsAsync_TheManagerSnapshot_SeedsTheRowStates()
    {
        var kit = CreateKit(
            """[{"name":"live","transport":"streamableHttp","enabled":true},{"name":"other","transport":"stdio","enabled":true}]""");
        kit.Manager.SeedStates(
            new McpClientState("live", true, McpClientStateKind.Connected, Detail: null));

        await kit.ViewModel.ResetSelectionsAsync();

        kit.ViewModel.McpServers[0].IsConnected.Should().BeTrue("the snapshot keys 'live' to its row");
        kit.ViewModel.McpServers[0].IsStateKnown.Should().BeTrue();

        kit.ViewModel.McpServers[1].IsStateKnown.Should().BeFalse(
            "the manager has no state for a name that left its list");
    }

    /// <summary>The live snapshots repaint the rows by name (the subscription carries the applied changes).</summary>
    [Fact]
    public async Task StatesChanged_AMatchingSnapshot_RepaintsTheRow()
    {
        var kit = CreateKit(
            """[{"name":"live","transport":"streamableHttp","enabled":true}]""");
        await kit.ViewModel.ResetSelectionsAsync();

        kit.ViewModel.McpServers[0].IsConnected.Should().BeFalse();

        kit.Manager.Stream.OnNext(
            [new McpClientState("live", true, McpClientStateKind.Connected, Detail: null)]);

        kit.ViewModel.McpServers[0].IsConnected.Should().BeTrue();
    }

    #endregion Seed

    #region Save

    /// <summary>The edited rows save verbatim: the persisted write lands first, the live apply follows.</summary>
    [Fact]
    public async Task SaveAsync_TheEditedRows_WritesAndApplies()
    {
        var kit = CreateKit();
        await kit.ViewModel.ResetSelectionsAsync();

        kit.ViewModel.AddMcpServerCommand.Execute().Subscribe();
        var row = kit.ViewModel.McpServers[0];
        row.Name = "stub";
        row.TransportIndex = (int)McpTransport.StreamableHttp;
        row.Address = "127.0.0.1";
        row.PortText = "8080";
        row.IsEnabled = true;

        await kit.ViewModel.SaveAsync();

        await kit.Settings.Received(1).SetAsync(
            McpClientManager.ServersSection,
            McpClientManager.ServersKey,
            """[{"name":"stub","transport":"streamableHttp","enabled":true,"url":"http://127.0.0.1:8080"}]""");
        kit.Manager.Applied.Should().ContainSingle().Which.Should().ContainSingle().Which
            .Should().Match<McpServerConfig>(config =>
                config.Name == "stub" &&
                config.Transport == McpTransport.StreamableHttp &&
                config.Url == "http://127.0.0.1:8080");
    }

    /// <summary>An unchanged save stays silent: no rewrite, no apply, no redial attempt.</summary>
    [Fact]
    public async Task SaveAsync_TheUnchangedRows_AreSilent()
    {
        var persisted = """[{"name":"s","transport":"stdio","command":"mcp-echo","enabled":true}]""";
        var kit = CreateKit(persisted);
        await kit.ViewModel.ResetSelectionsAsync();

        await kit.ViewModel.SaveAsync();

        await kit.Settings.DidNotReceive().SetAsync(
            McpClientManager.ServersSection,
            McpClientManager.ServersKey,
            Arg.Any<string>());
        kit.Manager.Applied.Should().BeEmpty("the manager never reconciles a save that changed nothing");
    }

    /// <summary>A blank-named row drops with a logged warning; a real edit among the rows makes the save carry the healthy ones.</summary>
    [Fact]
    public async Task SaveAsync_ABlankNamedRow_DropsButTheOthersPersist()
    {
        var kit = CreateKit();
        await kit.ViewModel.ResetSelectionsAsync();

        var blank = new McpServerRow { Name = "", Command = "mcp-echo" };
        kit.ViewModel.AddMcpServerCommand.Execute().Subscribe();
        var healthy = kit.ViewModel.McpServers[0];
        healthy.Name = "stub";
        healthy.TransportIndex = (int)McpTransport.StreamableHttp;
        healthy.Address = "127.0.0.1";
        healthy.PortText = "8080";
        kit.ViewModel.McpServers.Add(blank); // the blank row rides the same save

        await kit.ViewModel.SaveAsync();

        await kit.Settings.Received(1).SetAsync(
            McpClientManager.ServersSection,
            McpClientManager.ServersKey,
            """[{"name":"stub","transport":"streamableHttp","enabled":true,"url":"http://127.0.0.1:8080"}]""");
        kit.Manager.Applied.Should().ContainSingle().Which.Should().ContainSingle().Which
            .Name.Should().Be("stub");
    }

    /// <summary>The HTTP row saves its timeout option as typed (a present number rides the entry).</summary>
    [Fact]
    public async Task SaveAsync_TheTimeoutOption_RidesTheEntry()
    {
        var kit = CreateKit();
        await kit.ViewModel.ResetSelectionsAsync();

        kit.ViewModel.AddMcpServerCommand.Execute().Subscribe();
        var row = kit.ViewModel.McpServers[0];
        row.Name = "stub";
        row.TransportIndex = (int)McpTransport.StreamableHttp;
        row.Address = "mcp.example.com";
        row.PortText = "443";
        row.TimeoutText = "7";

        await kit.ViewModel.SaveAsync();

        await kit.Settings.Received(1).SetAsync(
            McpClientManager.ServersSection,
            McpClientManager.ServersKey,
            """[{"name":"stub","transport":"streamableHttp","enabled":true,"url":"http://mcp.example.com:443","httpTimeoutSeconds":7}]""");
    }

    /// <summary>An unparsable timeout text reports the row problem and saves the entry WITHOUT the option (never a made-up value).</summary>
    [Fact]
    public async Task SaveAsync_AnUnparsableTimeoutText_SavesWithoutTheOption()
    {
        var kit = CreateKit();
        await kit.ViewModel.ResetSelectionsAsync();

        kit.ViewModel.AddMcpServerCommand.Execute().Subscribe();
        var row = kit.ViewModel.McpServers[0];
        row.Name = "stub";
        row.TransportIndex = (int)McpTransport.StreamableHttp;
        row.Address = "mcp.example.com";
        row.PortText = "443";
        row.TimeoutText = "soon";

        await kit.ViewModel.SaveAsync();

        await kit.Settings.Received(1).SetAsync(
            McpClientManager.ServersSection,
            McpClientManager.ServersKey,
            """[{"name":"stub","transport":"streamableHttp","enabled":true,"url":"http://mcp.example.com:443"}]""");
    }

    #endregion Save
}
