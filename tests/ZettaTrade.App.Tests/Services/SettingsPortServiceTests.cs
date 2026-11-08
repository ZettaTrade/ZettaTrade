namespace ZettaTrade.App.Tests.Services;

using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ZettaTrade.App;
using ZettaTrade.App.Services;
using ZettaTrade.Infrastructure.Configuration;
using ZettaTrade.Infrastructure.Exchange;
using ZettaTrade.Infrastructure.Fees;
using ZettaTrade.Infrastructure.RateLimiting;

/// <summary>
/// The settings port's export/import contract (T-100) over a real store in a
/// per-test temp directory:
/// <list type="bullet">
/// <item>The export carries the registry-owned stored keys ONLY (a key the
/// store holds outside the registry — the secret-store lane or a raw tuning
/// key — never rides the file) and is byte-stable across exports.</item>
/// <item>The roundtrip acceptance: export → import → export is
/// byte-identical — a fresh store restoring the file re-exports exactly the
/// same bytes, and an import of one's own file plans nothing.</item>
/// <item>The validation never substitutes: a malformed document, a foreign
/// schema, an unsupported version, an entry the registry does not own and a
/// value that fails its stored-shape kind all reject the WHOLE file with the
/// typed reason while the store stays byte-stable.</item>
/// </list>
/// </summary>
public sealed class SettingsPortServiceTests
{
    #region Constants

    /// <summary>
    /// The full-document frame every foreign-structure case rides: the
    /// port's own export shape pinned textually (InlineData needs constants;
    /// the marker/version belong to the port service and the golden legs
    /// assert them side by side with the wrapped forms).
    /// </summary>
    private const string DocumentHead =
        "{ \"schema\": \"zettatrade-settings-port\", \"version\": 1, \"settings\": { ";

    /// <summary>The full-document frame's closing tail.</summary>
    private const string DocumentTail = " } }";

    #endregion Constants

    #region Roundtrip golden

    /// <summary>
    /// The acceptance's golden leg: a fully tuned store exports, a fresh
    /// store restores that exact file and re-exports it byte-identical.
    /// </summary>
    [Fact]
    public async Task ExportImportExport_AFullyTunedState_RoundTripsByteIdentical()
    {
        using var source = await CreatePortWithTunedStateAsync();
        var exported = await source.Port.ExportAsync();

        using var document = JsonDocument.Parse(exported);
        document.RootElement.GetProperty("schema").GetString()
            .Should().Be(SettingsPortService.SchemaMarker);
        document.RootElement.GetProperty("version").GetInt32()
            .Should().Be(SettingsPortService.SchemaVersion);
        exported.Should().Contain("\"trading-pro\"");
        exported.Should().Contain("\"ETH/BTC\"");

        // The indicators rows ride as compact-JSON text inside their string
        // wrapper (the RowsText kind). The wrapper's inner quotes escape in
        // whichever form the shipped serializer names the quote (the \"
        // and the \u0022 count the same — the store's contract is the
        // parse), so the payload is asserted through its own parse.
        var indicators = document.RootElement
            .GetProperty("settings")
            .GetProperty(UiSettingsKeys.Section)
            .GetProperty(UiSettingsKeys.ChartIndicators)
            .GetString();
        indicators.Should().NotBeNull();
        using var rows = JsonDocument.Parse(indicators!);
        rows.RootElement.ValueKind.Should().Be(JsonValueKind.Array);
        rows.RootElement[0].GetProperty("name").GetString().Should().Be("EMA");

        using var target = new SettingsPortKit();
        var plan = await target.Port.DraftImportAsync(exported);
        plan.Mutations.Should().HaveCountGreaterThan(0, "the fresh store lacks every exported key");
        await target.Port.ApplyImportAsync(plan);

        (await target.Port.ExportAsync())
            .Should()
            .Be(exported, "a store restored from the file re-exports it byte-identical");
    }

    /// <summary>
    /// The roundtrip's no-op leg: re-importing one's own export into the
    /// store it came from plans nothing and leaves the next export identical.
    /// </summary>
    [Fact]
    public async Task ReImportingOnesOwnExport_PlansNothing_AndLeavesTheExportIdentical()
    {
        using var kit = await CreatePortWithTunedStateAsync();
        var exported = await kit.Port.ExportAsync();

        var plan = await kit.Port.DraftImportAsync(exported);
        plan.Mutations.Should().BeEmpty("every entry already describes the stored state");
        plan.Rows.Should().BeEmpty("a no-op import previews no impact rows either");

        (await kit.Port.ExportAsync())
            .Should()
            .Be(exported, "a no-op import must not move the next export's bytes");
    }

    /// <summary>
    /// Two exports of one untouched state are byte-identical: the section
    /// order, the key order and the writer shape are the port's own fixed
    /// posture, whatever the store's internal dictionary order is.
    /// </summary>
    [Fact]
    public async Task TwoExports_OfOneUntouchedState_AreByteIdentical()
    {
        using var kit = await CreatePortWithTunedStateAsync();
        var first = await kit.Port.ExportAsync();
        var second = await kit.Port.ExportAsync();

        second.Should().Be(first);
    }

    #endregion Roundtrip golden

    #region The export's secret-freedom

    /// <summary>
    /// The trading-safety gate: a secret the store holds (the secret-store
    /// lane persists outside the registry) and a plain non-registry tuning
    /// key NEVER reach the file, and the export leaves the store untouched.
    /// </summary>
    [Fact]
    public async Task TheExport_NeverCarriesKeysOutsideTheRegistry()
    {
        using var kit = await CreatePortWithTunedStateAsync();
        await kit.Settings.SetAsync("Secrets", "ApiKey", "hunter2");
        await kit.Settings.SetAsync(UiSettingsKeys.Section, "ThirdPartyWidth", 42);

        var exported = await kit.Port.ExportAsync();

        exported.Should().NotContain("hunter2");
        exported.Should().NotContain("Secrets");
        exported.Should().NotContain("ThirdPartyWidth");
        (await kit.Settings.GetAsync<string>("Secrets", "ApiKey"))
            .Should().Be("hunter2", "the export moves nothing out of the store");
    }

    #endregion The export's secret-freedom

    #region Schema validation rejects

    /// <summary>
    /// Documents that fail the port's structural gate reject with the exact
    /// typed reason and the store stays byte-stable after every rejection
    /// (nothing partial ever lands).
    /// </summary>
    [Theory]
    [InlineData("this is not json at all", SettingsPortErrorReason.MalformedJson)]
    [InlineData("[1, 2, 3]", SettingsPortErrorReason.UnknownSchema)]
    [InlineData("{ \"settings\": { } }", SettingsPortErrorReason.UnknownSchema)]
    [InlineData("{ \"schema\": \"other-port\", \"version\": 1, \"settings\": { } }", SettingsPortErrorReason.UnknownSchema)]
    [InlineData("{ \"schema\": \"zettatrade-settings-port\", \"version\": 2, \"settings\": { } }", SettingsPortErrorReason.UnsupportedVersion)]
    [InlineData("{ \"schema\": \"zettatrade-settings-port\", \"version\": 1.0, \"settings\": { } }", SettingsPortErrorReason.UnsupportedVersion)]
    [InlineData("{ \"schema\": \"zettatrade-settings-port\", \"version\": 1 }", SettingsPortErrorReason.MalformedJson)]
    [InlineData(DocumentHead + "\"UI\": \"nope\"" + DocumentTail, SettingsPortErrorReason.MalformedJson)]
    [InlineData(DocumentHead + "\"Fauna\": { \"Mystery\": 1 }" + DocumentTail, SettingsPortErrorReason.UnknownEntry)]
    [InlineData(DocumentHead + "\"UI\": { \"ThirdPartyWidth\": 42 }" + DocumentTail, SettingsPortErrorReason.UnknownEntry)]
    public async Task AStructurallyRejectedFile_ThrowsTheTypedReason_AndKeepsTheStoreStable(
        string foreignText,
        SettingsPortErrorReason reason)
    {
        using var kit = await CreatePortWithTunedStateAsync();
        var before = await kit.Port.ExportAsync();

        var act = () => kit.Port.DraftImportAsync(foreignText);

        (await act.Should().ThrowAsync<SettingsPortImportException>())
            .Which.Reason.Should().Be(reason);
        (await kit.Port.ExportAsync())
            .Should()
            .Be(before, "a rejected import never touches the store");
    }

    /// <summary>
    /// A value that fails its key's stored-shape kind rejects the whole file:
    /// a boolean key gets only JSON booleans, a time-frame name only a
    /// defined frame, a row-list text only text whose payload parses as an
    /// array — never a coerced default.
    /// </summary>
    [Theory]
    [InlineData("\"UI\": { \"ChartUse12HourClock\": \"please\" }")] // a text where the clock switch stores a boolean.
    [InlineData("\"UI\": { \"ChartTimeFrame\": \"M42\" }")] // a string that names no defined time frame.
    [InlineData("\"UI\": { \"ChartIndicators\": \"[not an array payload\" }")] // a text whose payload is not an array.
    [InlineData("\"Workspace\": { \"ExchangeId\": [\"demo\"] }")] // an array where the exchange id stores text.
    [InlineData("\"Notifications\": { \"DoNotDisturb\": 1 }")] // a number where the DND switch stores a boolean.
    [InlineData("\"Exchange\": { \"Binance\": { \"RestBaseUrl\": \"https://no-rest-pair\", \"WsUrl\": 1 } }")] // a string-pairs record with a number value.
    public async Task AWrongPayloadKind_RejectsTheWholeFile(string settingsBody)
    {
        using var kit = await CreatePortWithTunedStateAsync();
        var before = await kit.Port.ExportAsync();

        var act = () => kit.Port.DraftImportAsync(PortDocument(settingsBody));

        (await act.Should().ThrowAsync<SettingsPortImportException>())
            .Which.Reason.Should().Be(SettingsPortErrorReason.WrongPayloadKind);
        (await kit.Port.ExportAsync())
            .Should()
            .Be(before, "a rejected import never touches the store");
    }

    #endregion Schema validation rejects

    #region Factories

    /// <summary>
    /// One port over a seeded tuned store: one sampled value from every
    /// stored-shape kind the registry owns.
    /// </summary>
    private static async Task<SettingsPortKit> CreatePortWithTunedStateAsync()
    {
        var kit = new SettingsPortKit();
        await SeedTunedStateAsync(kit.Settings);
        return kit;
    }

    /// <summary>The tuned posture the golden legs seed over the store.</summary>
    private static async Task SeedTunedStateAsync(SettingsService settings)
    {
        await settings.SetAsync(UiSettingsKeys.Section, UiSettingsKeys.Theme, "trading-pro");
        await settings.SetAsync(UiSettingsKeys.Section, UiSettingsKeys.Language, "ru");
        await settings.SetAsync(UiSettingsKeys.Section, UiSettingsKeys.ChartUse12HourClock, true);
        await settings.SetAsync(UiSettingsKeys.Section, UiSettingsKeys.ChartShowVwap, false);
        await settings.SetAsync(UiSettingsKeys.Section, UiSettingsKeys.ShowAiPanel, false);
        await settings.SetAsync(UiSettingsKeys.Section, UiSettingsKeys.BookPanelWidth, 340);
        await settings.SetAsync(UiSettingsKeys.Section, UiSettingsKeys.AiPanelWidth, 260);
        await settings.SetAsync(UiSettingsKeys.Section, UiSettingsKeys.TapePanelWidth, 220);
        await settings.SetAsync(UiSettingsKeys.Section, UiSettingsKeys.ChartTimeFrame, "M15");
        await settings.SetAsync(
            UiSettingsKeys.Section,
            UiSettingsKeys.ChartIndicators,
            """[{"name":"EMA","enabled":true,"period":50}]""");
        await settings.SetAsync(UiSettingsKeys.Section, UiSettingsKeys.DisclaimerVersion, 3);
        await settings.SetAsync(
            UiSettingsKeys.WorkspaceSection,
            ActiveExchangeService.ExchangeIdSettingKey,
            "demo");
        await settings.SetAsync(UiSettingsKeys.WorkspaceSection, UiSettingsKeys.WorkspaceSymbolId, "ETH/BTC");

        // The mark maps ride as compact JSON strings (the MarkMapText kind):
        // the store wraps the payload the chart's pickers read as text.
        await settings.SetAsync(
            UiSettingsKeys.WorkspaceSection,
            UiSettingsKeys.WorkspaceFavoriteSymbols,
            """{ "demo": ["BTC/USDT"] }""");

        await settings.SetAsync(UiSettingsKeys.McpSection, UiSettingsKeys.McpServerEnabled, true);

        await settings.SetAsync(
            ExchangeEndpointRegistry.SettingsSection,
            ExchangeEndpointRegistry.BinanceKey,
            new BinanceEndpoints("https://rest.binance-test.example", "wss://ws.binance-test.example"));
        await settings.SetAsync(
            ExchangeEndpointRegistry.SettingsSection,
            ExchangeEndpointRegistry.BybitKey,
            new BybitEndpoints("https://rest.bybit-test.example", "wss://ws.bybit-test.example"));

        await settings.SetAsync(RestRateLimiterOptions.SettingsSection, RestRateLimiter.EnabledKey, false);
        await settings.SetAsync(RestRateLimiterOptions.SettingsSection, RestRateLimiter.BackgroundShareKey, 0.5);

        var schedules = JsonDocument.Parse(
            """{ "demo": { "makerRateBps": 15, "takerRateBps": 25, "minimumFee": 0.1 } }""").RootElement;
        await settings.SetAsync(FeeScheduleService.SettingsSection, FeeScheduleService.SettingsKey, schedules.Clone());
    }

    /// <summary>Wrap a settings payload into a full (schema, version, settings) document.</summary>
    private static string PortDocument(string settingsBody) =>
        DocumentHead + settingsBody + DocumentTail;

    #endregion Factories
}
