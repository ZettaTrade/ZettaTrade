namespace ZettaTrade.App.Tests.ViewModels;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Livery.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using ZettaTrade.App.Services;
using ZettaTrade.App.Tests.Views.Headless;
using ZettaTrade.App.ViewModels;
using ZettaTrade.Charting;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.UI.Charting;
using RxVoid = ReactiveUI.Primitives.RxVoid;

/// <summary>
/// The named chart templates' manager facts (T-202): the stored envelope
/// fills the rows and the default, a save captures the attached chart's
/// face and upserts by name, a rename carries the default pointer along
/// and refuses onto an existing name, a delete clears the pointer it
/// pointed at, the default pick is idempotent and validates against a
/// fresh read, a foreign envelope refuses every mutation without
/// clobbering the file, the external writers apply live, and the
/// chart-side default seeding: the primary's entry-less symbols seed
/// their type and scale mode from the standing default (facet by facet, a
/// read that never materializes a map entry, a stored entry keeps
/// winning), a slot carries no manager and never seeds. The apply rides
/// the charts' own maps read-modify-write plus the verbatim global
/// facets, and refuses without a standing symbol. The facts ride the
/// serialized dispatcher pump (the manager's external-writer and language
/// handlers post through ObserveOnUiThread).
/// </summary>
[Collection("AvaloniaDispatcherSerial")]
public sealed class ChartTemplatesViewModelTests
{
    static ChartTemplatesViewModelTests() => AvaloniaDispatcherPump.EnsureInstalled();

    [Fact]
    public async Task AnInitWithoutStoredTemplates_KeepsTheRowsEmpty_AndWritesNothing()
    {
        var (manager, settings, _, _, _) = CreateKit();

        await manager.InitializeAsync();
        await AvaloniaDispatcherPump.DrainAsync();

        manager.Rows.Should().BeEmpty("a fresh profile carries no named templates");
        manager.DefaultTemplate.Should().BeNull("no default stands");

        // The restore reads: it never rewrites anything it just read.
        await settings.DidNotReceive().SetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task ASave_CapturesTheAttachedChartFace_AndPersistsTheUpsert()
    {
        var (chart, manager, settings, _) = CreateChartKit();
        await AwaitChartSettledAsync(chart);
        await manager.InitializeAsync();
        await AvaloniaDispatcherPump.DrainAsync();

        manager.SaveNameRequested.RegisterHandler(context =>
        {
            context.SetOutput("Scalp");
            return Task.CompletedTask;
        });
        await manager.SaveCommand.Execute(RxVoid.Default);

        await settings.Received(1).SetAsync(
            UiSettingsKeys.Section,
            UiSettingsKeys.ChartTemplates,
            Arg.Is<string>(json =>
                json.Contains("\"name\":\"Scalp\",\"chartType\":\"Candlesticks\",\"scaleMode\":\"Linear\"", StringComparison.Ordinal) &&
                json.Contains("\"showVwap\":true,\"showVolumeProfile\":true", StringComparison.Ordinal)));
        manager.Rows.Should().ContainSingle("the saved template joins the rows")
            .Which.Title.Should().Be("Scalp");
    }

    [Fact]
    public async Task ASave_UnderAnExistingName_ReplacesTheFace()
    {
        var (chart, manager, settings, _) = CreateChartKit();
        await AwaitChartSettledAsync(chart);
        await manager.InitializeAsync();
        manager.SaveNameRequested.RegisterHandler(context =>
        {
            context.SetOutput("Scalp");
            return Task.CompletedTask;
        });
        await manager.SaveCommand.Execute(RxVoid.Default);

        // The user flips the rendering type, then saves under the same
        // name: the stored face replaces, no second entry appears.
        chart.ChartType = ChartSeriesType.HeikinAshi;
        await AvaloniaDispatcherPump.DrainAsync();
        await manager.SaveCommand.Execute(RxVoid.Default);

        await settings.Received(2).SetAsync(UiSettingsKeys.Section, UiSettingsKeys.ChartTemplates, Arg.Any<string>());
        await settings.Received(1).SetAsync(
            UiSettingsKeys.Section,
            UiSettingsKeys.ChartTemplates,
            Arg.Is<string>(json => json.Contains("\"name\":\"Scalp\",\"chartType\":\"HeikinAshi\"", StringComparison.Ordinal)));
        manager.Rows.Should().ContainSingle("an upsert never duplicates the name");
    }

    [Fact]
    public async Task ARename_UpdatesTheName_AndCarriesTheDefaultPointerAlong()
    {
        var (manager, settings, _, _, _) = await CreateKitWithStoredAsync(StoredPairJson, StoredDefault("Swing"));
        manager.RenameRequested.RegisterHandler(context =>
        {
            context.SetOutput("Intraday");
            return Task.CompletedTask;
        });

        var row = manager.Rows.Single(static candidate => candidate.Title.StartsWith("Swing", StringComparison.Ordinal));
        await row.Actions[2].Command.Execute(RxVoid.Default);

        await settings.Received(1).SetAsync(
            UiSettingsKeys.Section,
            UiSettingsKeys.ChartTemplates,
            Arg.Is<string>(json => json.Contains("Intraday", StringComparison.Ordinal) && !json.Contains("Swing", StringComparison.Ordinal)));
        await settings.Received(1).SetAsync(UiSettingsKeys.Section, UiSettingsKeys.ChartTemplateDefault, "Intraday");
        manager.DefaultTemplate.Should().NotBeNull().And.Be(
            new ChartTemplate("Intraday", ChartSeriesType.Area, PriceScaleMode.Percent, "[]", false, false),
            "the default pointer follows the rename");
        manager.Rows.Should().ContainSingle(static candidate => candidate.Title.StartsWith("Intraday", StringComparison.Ordinal))
            .Which.Title.Should().Contain("(default)", "the renamed default keeps its marker");
    }

    [Fact]
    public async Task ARename_OntoAnExistingName_Refuses_AndReportsThroughTheChart()
    {
        var (chart, manager, settings, _, _) = await CreateChartKitWithStoredAsync(
            StoredPairJson,
            StoredDefault("Scalp"));
        await manager.InitializeAsync();
        await AvaloniaDispatcherPump.DrainAsync();
        manager.RenameRequested.RegisterHandler(context =>
        {
            context.SetOutput("Swing");
            return Task.CompletedTask;
        });

        var row = manager.Rows.Single(static candidate => candidate.Title.StartsWith("Scalp", StringComparison.Ordinal));
        await row.Actions[2].Command.Execute(RxVoid.Default);

        chart.ErrorMessage.Should().Be("Chart.Templates.RenameClash", "the refusal reports through the attached chart's error line");
        await settings.DidNotReceive().SetAsync(UiSettingsKeys.Section, UiSettingsKeys.ChartTemplates, Arg.Any<string>());
        await settings.DidNotReceive().SetAsync(UiSettingsKeys.Section, UiSettingsKeys.ChartTemplateDefault, Arg.Any<string>());
        manager.DefaultTemplate.Should().NotBeNull().And.Be(
            new ChartTemplate("Scalp", ChartSeriesType.Line, PriceScaleMode.Linear, "[]", true, true),
            "a refused rename changes nothing");
    }

    [Fact]
    public async Task ADelete_ClearsTheDefaultPointer_WhenItPointedAtTheDeleted()
    {
        var (manager, settings, _, _, published, _) = await CreateKitWithPublishedAsync(
            StoredPairJson,
            StoredDefault("Swing"));

        var row = manager.Rows.Single(static candidate => candidate.Title.StartsWith("Swing", StringComparison.Ordinal));
        await row.Actions[3].Command.Execute(RxVoid.Default);

        await settings.Received(1).SetAsync(
            UiSettingsKeys.Section,
            UiSettingsKeys.ChartTemplates,
            Arg.Is<string>(json => json.Contains("Scalp", StringComparison.Ordinal) && !json.Contains("Swing", StringComparison.Ordinal)));
        await settings.Received(1).SetAsync(UiSettingsKeys.Section, UiSettingsKeys.ChartTemplateDefault, "");
        manager.DefaultTemplate.Should().BeNull("the deleted default leaves no default standing");
        manager.Rows.Should().ContainSingle().Which.Title.Should().Be("Scalp");
        published.Skip(1).First().Should().BeNull("the cleared default publishes");
    }

    [Fact]
    public async Task ASetDefault_PointsAtTheFreshTemplate_Publishes_AndTheSecondPickIsSilent()
    {
        var (manager, settings, _, _, published, _) = await CreateKitWithPublishedAsync(StoredPairJson, null);

        var row = manager.Rows.Single(static candidate => candidate.Title.StartsWith("Swing", StringComparison.Ordinal));
        await row.Actions[1].Command.Execute(RxVoid.Default);
        await row.Actions[1].Command.Execute(RxVoid.Default);

        await settings.Received(1).SetAsync(UiSettingsKeys.Section, UiSettingsKeys.ChartTemplateDefault, "Swing");
        manager.DefaultTemplate.Should().NotBeNull().And.Be(
            new ChartTemplate("Swing", ChartSeriesType.Area, PriceScaleMode.Percent, "[]", false, false),
            "the pick validates against a fresh read and points at the stored record");
        manager.Rows.Should().ContainSingle(static candidate => candidate.Title.StartsWith("Swing", StringComparison.Ordinal))
            .Which.Title.Should().Contain("(default)");
        published.Should().ContainSingle().Which.Should().NotBeNull().And.Be(
            new ChartTemplate("Swing", ChartSeriesType.Area, PriceScaleMode.Percent, "[]", false, false),
            "the pick publishes once — the second is the idempotent no-op");
    }

    [Fact]
    public async Task AMutationsOnAForeignVersion_Refuse_AndACompatibleWriteRestoresUsability()
    {
        const string compatibleJson = """{"version":1,"templates":[{"name":"Scalp","chartType":"Line","scaleMode":"Linear","indicators":"[]","showVwap":true,"showVolumeProfile":true}]}""";
        var (chart, manager, settings, changes, _) = await CreateChartKitWithStoredAsync(
            """{"version":2,"templates":[]}""",
            null);

        await manager.InitializeAsync();
        await AvaloniaDispatcherPump.DrainAsync();
        manager.Rows.Should().BeEmpty("a foreign envelope parses to no templates");

        var saveWasAsked = false;
        manager.SaveNameRequested.RegisterHandler(context =>
        {
            saveWasAsked = true;
            context.SetOutput("Scalp");
            return Task.CompletedTask;
        });
        await manager.SaveCommand.Execute(RxVoid.Default);

        saveWasAsked.Should().BeFalse("the refusal fires before the prompt");
        chart.ErrorMessage.Should().Be("Chart.Templates.NewerVersion", "the refusal reports through the attached chart's error line");
        await settings.DidNotReceive().SetAsync(UiSettingsKeys.Section, UiSettingsKeys.ChartTemplates, Arg.Any<string>());

        // A compatible external write restores usability: the fresh parse
        // clears the refusal guard, the rows return, the default pick runs
        // (the stored file behind the substitute's read re-configures with
        // it — the real service's write does exactly that).
        changes.OnNext(new SettingsChange(
            UiSettingsKeys.Section,
            UiSettingsKeys.ChartTemplates,
            null,
            compatibleJson));
        settings.GetAsync<string>(UiSettingsKeys.Section, UiSettingsKeys.ChartTemplates, Arg.Any<string>())
            .Returns(compatibleJson);
        await AvaloniaDispatcherPump.DrainAsync();

        manager.Rows.Should().ContainSingle().Which.Title.Should().Be("Scalp");
        await manager.Rows.Single().Actions[1].Command.Execute(RxVoid.Default);
        await settings.Received(1).SetAsync(UiSettingsKeys.Section, UiSettingsKeys.ChartTemplateDefault, "Scalp");
    }

    [Fact]
    public async Task AnExternalDocumentWrite_AppliesLive_AndAGarbageOneEmptiesTheRows()
    {
        var (manager, _, changes, _, _) = CreateKit();
        await manager.InitializeAsync();
        await AvaloniaDispatcherPump.DrainAsync();

        changes.OnNext(new SettingsChange(
            UiSettingsKeys.Section,
            UiSettingsKeys.ChartTemplates,
            null,
            StoredTemplates("""{"version":1,"templates":[{"name":"Scalp","chartType":"Line","scaleMode":"Linear","indicators":"[]","showVwap":true,"showVolumeProfile":true}]}""")));
        await AvaloniaDispatcherPump.DrainAsync();
        manager.Rows.Should().ContainSingle().Which.Title.Should().Be("Scalp", "an external writer's parse becomes the rows");

        changes.OnNext(new SettingsChange(
            UiSettingsKeys.Section,
            UiSettingsKeys.ChartTemplates,
            "previous",
            """{"version":9,"templates":[]}"""));
        await AvaloniaDispatcherPump.DrainAsync();
        manager.Rows.Should().BeEmpty("a foreign envelope parses to no rows");
        manager.DefaultTemplate.Should().BeNull();
    }

    [Fact]
    public async Task AnExternalDefaultWrite_ReResolves_AndPublishesTheResolvedTemplate()
    {
        var (manager, _, changes, _, published, _) = await CreateKitWithPublishedAsync(StoredPairJson, null);

        // An external writer points the default at a stored template: the
        // name re-resolves and the resolved template publishes.
        changes.OnNext(new SettingsChange(UiSettingsKeys.Section, UiSettingsKeys.ChartTemplateDefault, "", "Scalp"));
        await AvaloniaDispatcherPump.DrainAsync();
        manager.DefaultTemplate.Should().NotBeNull().And.Be(
            new ChartTemplate("Scalp", ChartSeriesType.Line, PriceScaleMode.Linear, "[]", true, true));
        published.Last().Should().NotBeNull().And.Be(
            new ChartTemplate("Scalp", ChartSeriesType.Line, PriceScaleMode.Linear, "[]", true, true),
            "the resolved template publishes");

        // A ghost pointer resolves to NO default — never a substitution.
        changes.OnNext(new SettingsChange(UiSettingsKeys.Section, UiSettingsKeys.ChartTemplateDefault, "Scalp", "Ghost"));
        await AvaloniaDispatcherPump.DrainAsync();
        manager.DefaultTemplate.Should().BeNull("a ghost pointer resolves to no default — never a substitution");
        published.Last().Should().BeNull("the resolved change publishes");
    }

    [Fact]
    public async Task ADefaultTemplate_SeedsEveryEntryLessSymbol_AndTheSeedNeverMaterializesAMapEntry()
    {
        var (chart, manager, settings, _, _) = await CreateChartKitWithStoredAsync(
            StoredTemplates("""{"version":1,"templates":[{"name":"Scalp","chartType":"HeikinAshi","scaleMode":"Logarithmic","indicators":"[]","showVwap":true,"showVolumeProfile":true}]}"""),
            StoredDefault("Scalp"));
        await manager.InitializeAsync();
        await AwaitChartSettledAsync(chart);
        await AvaloniaDispatcherPump.DrainAsync();

        chart.ChartType.Should().Be(ChartSeriesType.HeikinAshi, "the standing default seeds the entry-less workspace symbol");
        chart.ScaleMode.Should().Be(PriceScaleMode.Logarithmic, "the per-symbol facets seed facet by facet");

        // The seed is a read: the maps' persistence pipeline never learned
        // of it — a stored preference appears only when the user flips the
        // selector themselves.
        await settings.DidNotReceive().SetAsync(UiSettingsKeys.Section, UiSettingsKeys.ChartSymbolTypes, Arg.Any<string>());
        await settings.DidNotReceive().SetAsync(UiSettingsKeys.Section, UiSettingsKeys.ChartSymbolScales, Arg.Any<string>());

        // A user pick materializes its symbol's entry; another entry-less
        // pick re-seeds from the standing default.
        chart.ChartType = ChartSeriesType.Line;
        await AvaloniaDispatcherPump.DrainAsync();
        chart.SelectedSymbol = Symbol.Parse("ETH/USDT");
        await AvaloniaDispatcherPump.DrainAsync();

        chart.ChartType.Should().Be(ChartSeriesType.HeikinAshi, "the entry-less pick re-seeds from the default");
        chart.ScaleMode.Should().Be(PriceScaleMode.Logarithmic);
    }

    [Fact]
    public async Task AStoredEntry_WinsOverTheDefault_FacetByFacet()
    {
        var (chart, manager, _, _, _) = await CreateChartKitWithStoredAsync(
            StoredTemplates("""{"version":1,"templates":[{"name":"Scalp","chartType":"HeikinAshi","scaleMode":"Logarithmic","indicators":"[]","showVwap":true,"showVolumeProfile":true}]}"""),
            StoredDefault("Scalp"),
            symbolTypesJson: """{"BTC/USDT":"Line"}""");
        await manager.InitializeAsync();
        await AwaitChartSettledAsync(chart);
        await AvaloniaDispatcherPump.DrainAsync();

        chart.ChartType.Should().Be(ChartSeriesType.Line, "a stored entry keeps winning over the default");
        chart.ScaleMode.Should().Be(PriceScaleMode.Logarithmic, "the facets seed independently: no scale entry, the default's scale seeds");
    }

    [Fact]
    public async Task ASlot_NeverCarriesTheManager_NorSeedsTheDefault()
    {
        var (manager, settings, _, _, exchanges) = await CreateKitWithStoredAsync(
            StoredTemplates("""{"version":1,"templates":[{"name":"Scalp","chartType":"HeikinAshi","scaleMode":"Logarithmic","indicators":"[]","showVwap":true,"showVolumeProfile":true}]}"""),
            StoredDefault("Scalp"));

        // The grid slot resolves its chart without the manager (the factory
        // passes none) but over the kit's own settings substitute: the
        // shared template state stays the primary's.
        var slot = MainWorld.CreateChart(exchanges, settings, slotId: "1");
        await AwaitChartSettledAsync(slot);
        await AvaloniaDispatcherPump.DrainAsync();

        slot.Templates.Should().BeNull("a slot carries no manager");
        slot.HasTemplates.Should().BeFalse("the affordance hides on the slots");
        slot.PersistsFaceChoices.Should().BeFalse("a slot's face picks stay in-session");
        slot.ChartType.Should().Be(ChartSeriesType.Candlesticks, "the default seeds the primary's entry-less symbols only — never a slot's");
        slot.ScaleMode.Should().Be(PriceScaleMode.Linear);
    }

    [Fact]
    public async Task AnApply_WritesTheMapsAndTheGlobalFacets_AndTheChartAppliesLive()
    {
        const string indicatorsJson = """[{"name":"EMA","enabled":true,"period":12}]""";
        var storedDocument = StoredTemplates(
            """{"version":1,"templates":[{"name":"Scalp","chartType":"HeikinAshi","scaleMode":"Logarithmic","indicators":""" +
            JsonSerializer.Serialize(indicatorsJson) +
            ""","showVwap":false,"showVolumeProfile":false}]}""");
        var (chart, manager, settings, changes, _) = await CreateChartKitWithStoredAsync(storedDocument, null);
        await manager.InitializeAsync();
        await AwaitChartSettledAsync(chart);
        await AvaloniaDispatcherPump.DrainAsync();

        await manager.Rows.Single(static candidate => candidate.Title == "Scalp").Actions[0].Command.Execute(RxVoid.Default);

        await settings.Received(1).SetAsync(
            UiSettingsKeys.Section,
            UiSettingsKeys.ChartSymbolTypes,
            Arg.Is<string>(json => json.Contains("\"BTC/USDT\":\"HeikinAshi\"", StringComparison.Ordinal)));
        await settings.Received(1).SetAsync(
            UiSettingsKeys.Section,
            UiSettingsKeys.ChartSymbolScales,
            Arg.Is<string>(json => json.Contains("\"BTC/USDT\":\"Logarithmic\"", StringComparison.Ordinal)));
        await settings.Received(1).SetAsync(UiSettingsKeys.Section, UiSettingsKeys.ChartIndicators, indicatorsJson);
        await settings.Received(1).SetAsync(UiSettingsKeys.Section, UiSettingsKeys.ChartShowVwap, false);
        await settings.Received(1).SetAsync(UiSettingsKeys.Section, UiSettingsKeys.ChartShowVolumeProfile, false);

        // The real service echoes the writes into the change stream: the
        // chart's own consumers apply everything live under their restore
        // gates (no double write — the manager's write is the only one).
        var typesJson = SymbolChartTypeMap.Serialize(
            SymbolChartTypeMap.WithEntry(SymbolChartTypeMap.Parse("{}"), "BTC/USDT", ChartSeriesType.HeikinAshi));
        var scalesJson = SymbolScaleModeMap.Serialize(
            SymbolScaleModeMap.WithEntry(SymbolScaleModeMap.Parse("{}"), "BTC/USDT", PriceScaleMode.Logarithmic));
        changes.OnNext(new SettingsChange(UiSettingsKeys.Section, UiSettingsKeys.ChartSymbolTypes, "{}", typesJson));
        changes.OnNext(new SettingsChange(UiSettingsKeys.Section, UiSettingsKeys.ChartSymbolScales, "{}", scalesJson));
        changes.OnNext(new SettingsChange(UiSettingsKeys.Section, UiSettingsKeys.ChartShowVwap, true, false));
        await AvaloniaDispatcherPump.DrainAsync();

        chart.ChartType.Should().Be(ChartSeriesType.HeikinAshi, "the standing symbol re-addressed the fresh map");
        chart.ScaleMode.Should().Be(PriceScaleMode.Logarithmic);
        chart.ShowVwap.Should().BeFalse("the overlay toggle applied live with its cache leading");

        // Exactly one ChartSymbolTypes write reaches the store: the
        // manager's apply — the chart's own restore re-addresses under its
        // gate and never rewrites the map it just applied.
        await settings.Received(1).SetAsync(UiSettingsKeys.Section, UiSettingsKeys.ChartSymbolTypes, Arg.Any<string>());
    }

    [Fact]
    public async Task AnApply_WithoutAStandingSymbol_RefusesHonestly()
    {
        var (chart, manager, settings, _, _) = await CreateChartKitWithStoredAsync(
            StoredTemplates("""{"version":1,"templates":[{"name":"Scalp","chartType":"HeikinAshi","scaleMode":"Logarithmic","indicators":"[]","showVwap":false,"showVolumeProfile":false}]}"""),
            null);
        await manager.InitializeAsync();
        await AwaitChartSettledAsync(chart);

        chart.SelectedSymbol = null;
        await AvaloniaDispatcherPump.DrainAsync();

        await manager.Rows.Single().Actions[0].Command.Execute(RxVoid.Default);

        chart.ErrorMessage.Should().Be("Chart.Templates.ApplyNoSymbol", "the per-symbol facets need a symbol to address");
        await settings.DidNotReceive().SetAsync(UiSettingsKeys.Section, UiSettingsKeys.ChartSymbolTypes, Arg.Any<string>());
        await settings.DidNotReceive().SetAsync(UiSettingsKeys.Section, UiSettingsKeys.ChartShowVwap, Arg.Any<bool>());
    }

    /// <summary>The stored Scalp/Swing pair the multi-template facts share.</summary>
    private const string StoredPairJson =
        """
        {"version":1,"templates":[
          {"name":"Scalp","chartType":"Line","scaleMode":"Linear","indicators":"[]","showVwap":true,"showVolumeProfile":true},
          {"name":"Swing","chartType":"Area","scaleMode":"Percent","indicators":"[]","showVwap":false,"showVolumeProfile":false}
        ]}
        """;

    /// <summary>The stored templates document's exact key read.</summary>
    private static string StoredTemplates(string json) => json;

    /// <summary>The stored default's name as the key carries it.</summary>
    private static string StoredDefault(string name) => name;

    /// <summary>
    /// One manager over a substitute settings service and localization: the
    /// stored envelope and default's name land through their own key reads,
    /// everything else echoes its stored default back (a fresh profile's
    /// shape), the strings echo their keys with the default marker canned.
    /// The init walk does NOT run here — every kit decides its own order.
    /// </summary>
    private static (ChartTemplatesViewModel Manager, ISettingsService Settings, Subject<SettingsChange> Changes, ILocalizationManager Localization, ActiveExchangeService Exchanges) CreateKit(
        string? templatesJson = null,
        string? defaultNameJson = null)
    {
        var settings = Substitute.For<ISettingsService>();
        var changes = new Subject<SettingsChange>();
        settings.Changes.Returns(changes);
        settings.GetAsync<string>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(async ci => ci.ArgAt<string>(2));
        settings.GetAsync<string>(Arg.Any<string>(), Arg.Any<string>())
            .Returns(string.Empty);
        settings.SetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.CompletedTask);
        settings.SetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns(Task.CompletedTask);
        if (templatesJson is not null)
        {
            settings.GetAsync<string>(UiSettingsKeys.Section, UiSettingsKeys.ChartTemplates, Arg.Any<string>())
                .Returns(templatesJson);
        }

        if (defaultNameJson is not null)
        {
            settings.GetAsync<string>(UiSettingsKeys.Section, UiSettingsKeys.ChartTemplateDefault, Arg.Any<string>())
                .Returns(defaultNameJson);
        }

        var localization = Substitute.For<ILocalizationManager>();
        localization.LanguageChanged.Returns(Observable.Never<string>());
        localization.GetString(Arg.Any<string>()).Returns(ci => ci.ArgAt<string>(0));
        localization.GetString("Chart.Templates.DefaultSuffix").Returns("(default)");

        var adapter = new MainWorld.FakeMarketDataAdapter("demo", "Demo", "BTC/USDT", "ETH/USDT");
        var exchanges = new ActiveExchangeService(
            new IMarketDataAdapter[] { adapter },
            settings,
            NullLogger<ActiveExchangeService>.Instance);

        var manager = new ChartTemplatesViewModel(
            settings,
            localization,
            NullLogger<ChartTemplatesViewModel>.Instance);

        return (manager, settings, changes, localization, exchanges);
    }

    /// <summary>
    /// One manager over the stored documents: the init walk ran before the
    /// facts read the rows (the walk's order the composition keeps).
    /// </summary>
    private static async Task<(
        ChartTemplatesViewModel Manager,
        ISettingsService Settings,
        Subject<SettingsChange> Changes,
        ILocalizationManager Localization,
        ActiveExchangeService Exchanges)> CreateKitWithStoredAsync(string templatesJson, string? defaultNameJson)
    {
        var (manager, settings, changes, localization, exchanges) = CreateKit(templatesJson, defaultNameJson);
        await manager.InitializeAsync();
        await AvaloniaDispatcherPump.DrainAsync();

        return (manager, settings, changes, localization, exchanges);
    }

    /// <summary>
    /// One manager over the stored documents with the default's publishes
    /// collecting into a shared list (the subscription stays alive for the
    /// fact's reads).
    /// </summary>
    private static async Task<(
        ChartTemplatesViewModel Manager,
        ISettingsService Settings,
        Subject<SettingsChange> Changes,
        ILocalizationManager Localization,
        List<ChartTemplate?> Published,
        ActiveExchangeService Exchanges)> CreateKitWithPublishedAsync(string templatesJson, string? defaultNameJson)
    {
        var (manager, settings, changes, localization, exchanges) = CreateKit(templatesJson, defaultNameJson);
        var published = new List<ChartTemplate?>();
        manager.DefaultTemplateChanged.Subscribe(published.Add);
        await manager.InitializeAsync();
        await AvaloniaDispatcherPump.DrainAsync();

        return (manager, settings, changes, localization, published, exchanges);
    }

    /// <summary>
    /// The chart-coupled kit: the manager rides the primary's construction
    /// (the chart attaches itself), the stored per-symbol map reads land
    /// through their own key reads, and the facts decide the init order.
    /// </summary>
    private static async Task<(
        ChartViewModel Chart,
        ChartTemplatesViewModel Manager,
        ISettingsService Settings,
        Subject<SettingsChange> Changes,
        ActiveExchangeService Exchanges)> CreateChartKitWithStoredAsync(
        string templatesJson,
        string? defaultNameJson,
        string? symbolTypesJson = null,
        string? symbolScalesJson = null)
    {
        var (manager, settings, changes, _, exchanges) = CreateKit(templatesJson, defaultNameJson);
        if (symbolTypesJson is not null)
        {
            settings.GetAsync<string>(UiSettingsKeys.Section, UiSettingsKeys.ChartSymbolTypes, Arg.Any<string>())
                .Returns(symbolTypesJson);
        }

        if (symbolScalesJson is not null)
        {
            settings.GetAsync<string>(UiSettingsKeys.Section, UiSettingsKeys.ChartSymbolScales, Arg.Any<string>())
                .Returns(symbolScalesJson);
        }

        var chart = MainWorld.CreateChart(exchanges, settings, templates: manager);
        return (chart, manager, settings, changes, exchanges);
    }

    /// <summary>
    /// The fresh-profile chart kit: the primary over the manager, the facts
    /// decide the init order.
    /// </summary>
    private static (ChartViewModel Chart, ChartTemplatesViewModel Manager, ISettingsService Settings, Subject<SettingsChange> Changes) CreateChartKit()
    {
        var (manager, settings, changes, _, exchanges) = CreateKit();
        var chart = MainWorld.CreateChart(exchanges, settings, templates: manager);
        return (chart, manager, settings, changes);
    }

    /// <summary>
    /// One settled-quiet wait the chart facts share: the bootstrap lands
    /// its selection through the serialized dispatcher.
    /// </summary>
    private static async Task AwaitChartSettledAsync(params ChartViewModel[] charts)
    {
        var budget = TimeSpan.FromSeconds(5);
        var started = DateTime.UtcNow;
        while (true)
        {
            if (charts.All(chart => chart.SelectedSymbol is not null))
            {
                return;
            }

            if (DateTime.UtcNow - started > budget)
            {
                throw new Xunit.Sdk.XunitException("the chart's bootstrap never landed its selection");
            }

            await Task.Delay(25);
            await AvaloniaDispatcherPump.DrainAsync();
        }
    }
}
