namespace ZettaTrade.App.Services;

using System.Text.Json;
using ZettaTrade.AI.Chat;
using ZettaTrade.AI.Mcp;
using ZettaTrade.App;
using ZettaTrade.Core.Enums;
using ZettaTrade.Infrastructure.Exchange;
using ZettaTrade.Infrastructure.Fees;
using ZettaTrade.Infrastructure.Persistence;
using ZettaTrade.Infrastructure.RateLimiting;

/// <summary>
/// The stored-shape kind of one <see cref="PortSettingsKey"/>'s values: the
/// level the settings port validates an imported (or stamped) payload at.
/// The rule is kind-level only — an unparsable or wrong-kind payload is
/// rejected outright (nothing coerced, nothing defaulted), while
/// value-level semantics ride the consumers' own established canons
/// (an unknown theme name keeps the startup theme, an unrecognized time
/// frame keeps the default one, a malformed fee schedule entry is dropped
/// with a warning — the same hand-edited-settings-file behavior).
/// </summary>
public enum PortValueKind
{
    /// <summary>A JSON string (theme name, language code, URL, id).</summary>
    Text,

    /// <summary>A JSON boolean (an on/off switch).</summary>
    Switch,

    /// <summary>A JSON number (a width, a period, a count, a threshold).</summary>
    Number,

    /// <summary>A JSON string naming a defined time frame (the chart picker's persisted choice).</summary>
    TimeFrameName,

    /// <summary>A JSON string whose payload itself parses as a JSON array (the stored compact-JSON documents that key row lists).</summary>
    RowsText,

    /// <summary>A JSON string whose payload itself parses as a JSON object (the stored compact-JSON symbol-mark maps).</summary>
    MarkMapText,

    /// <summary>A JSON object whose every property value is a string — with the one exception the exchange endpoint records ship: the optional boolean isTestnet identity member (the transport URL pairs plus the lane identity the trading-mode chip reads).</summary>
    UrlPairs,

    /// <summary>A JSON object whose every property value is an object (the per-exchange fee schedule map).</summary>
    ScheduleMap,
}

/// <summary>
/// One port-safe settings key: the (section, key) address, the stored-shape
/// kind and the fresh-install default value it resets to.
/// </summary>
/// <param name="Section">The settings section the key lives in.</param>
/// <param name="Key">The key inside the section.</param>
/// <param name="Kind">The stored-shape kind the port validates values against.</param>
/// <param name="Default">
/// The shipped default the factory reset stamps a STORED entry back to, or
/// null when the key's default IS the fresh-install absence (a stored
/// entry removes — the consumers' absent-key behavior IS the default; see
/// the per-key docs). An entry the store never wrote stays absent: its
/// absence already resolves to the shipped default through the read-time
/// fallback, so a reset of a pristine store writes nothing.
/// </param>
public sealed record PortSettingsKey(string Section, string Key, PortValueKind Kind, JsonElement? Default)
{
    /// <summary>The composite key the settings store addresses the entry by.</summary>
    public string CompositeKey => $"{Section}\0{Key}";
}

/// <summary>
/// The canonical registry of the settings the user adjusts in the app and
/// the settings port (T-100) carries: every export contains exactly these
/// keys (and only the ones the store actually holds), every import accepts
/// only these (an unknown section or key rejects the whole file — nothing
/// partial ever lands), and the factory reset returns each key to its
/// listed default. Secrets are NOT here and can never be: the registry
/// addresses the settings service only, and the port service never takes
/// an <c>ISecretStore</c> — the structural guarantee the export carries no
/// credential (boarding on the T-046 secret-store canon).
/// </summary>
/// <remarks>
/// Defaults are serialized from the SAME constants the consuming code
/// applies at a fresh start (each entry's doc names its source), with the
/// settings service's own camel-case value shape — a reset stamps exactly
/// what a stored choice would have looked like. The per-adapter limiter
/// budget keys (the <c>Limiter</c> settings section, one entry per
/// registered adapter and budget) are deliberately outside the registry:
/// they are formed at adapter registration time, they are operator-facing
/// tuning, and their absence keeps the shipped budgets standing.
/// </remarks>
public static class PortSettingsRegistry
{
    /// <summary>The registry's own camel-case value shape (the settings service serializes values the same way).</summary>
    private static readonly JsonSerializerOptions DefaultValueOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// The camel-case property name the endpoint records' boolean IsTestnet
    /// member carries in the stored/exported JSON (the serialization policy
    /// above writes it; the trading-mode chip reads the same member at the
    /// runtime level). The UrlPairs shape allows exactly this one
    /// non-string member.
    /// </summary>
    private const string IsTestnetPropertyName = "isTestnet";

    /// <summary>The port-safe user-adjustable keys, in a fixed, test-stable order.</summary>
    private static readonly PortSettingsKey[] Keys =
    [
        // The appearance selection (the settings window's appearance tab).
        // A fresh install stores nothing; the startup read resolves both to
        // the shipped fallbacks (the dark theme, the OS culture), so the
        // default IS absence — a reset removes both stored choices.
        Text(UiSettingsKeys.Section, UiSettingsKeys.Theme),
        Text(UiSettingsKeys.Section, UiSettingsKeys.Language),

        // The chart clock, overlay toggles and layout widths (the
        // appearance tab's persisted knobs).
        Switch(UiSettingsKeys.Section, UiSettingsKeys.ChartUse12HourClock, UiSettingsKeys.DefaultUse12HourClock),
        Switch(
            UiSettingsKeys.Section,
            UiSettingsKeys.ChartShowVwap,
            UiSettingsKeys.DefaultChartShowVwap),
        Switch(
            UiSettingsKeys.Section,
            UiSettingsKeys.ChartShowVolumeProfile,
            UiSettingsKeys.DefaultChartShowVolumeProfile),
        Switch(
            UiSettingsKeys.Section,
            UiSettingsKeys.ShowAiPanel,
            UiSettingsKeys.DefaultShowAiPanel),
        Number(UiSettingsKeys.Section, UiSettingsKeys.BookPanelWidth, UiSettingsKeys.DefaultBookPanelWidth),
        Number(UiSettingsKeys.Section, UiSettingsKeys.AiPanelWidth, UiSettingsKeys.DefaultAiPanelWidth),
        Number(UiSettingsKeys.Section, UiSettingsKeys.TapePanelWidth, UiSettingsKeys.DefaultTapePanelWidth),

        // The persisted chart time frame: a defined TimeFrame name or absence
        // (absence keeps the chart's own default frame).
        Text(UiSettingsKeys.Section, UiSettingsKeys.ChartTimeFrame, PortValueKind.TimeFrameName),

        // The indicator overlay rows (the indicators tab's saved config, a
        // compact-JSON row array in one string).
        Text(UiSettingsKeys.Section, UiSettingsKeys.ChartIndicators, PortValueKind.RowsText),

        // The first-run disclaimer version: a fresh install has acknowledged
        // nothing, and a reset re-shows the disclaimer exactly once.
        Number(UiSettingsKeys.Section, UiSettingsKeys.DisclaimerVersion, UiSettingsKeys.DefaultDisclaimerVersion),

        // The workspace context: exchange choice, symbol, pair marks. A fresh
        // install stores none of them (the bootstrap resolves all of them),
        // so absence is the default.
        Text(
            UiSettingsKeys.WorkspaceSection,
            ActiveExchangeService.ExchangeIdSettingKey),
        Text(UiSettingsKeys.WorkspaceSection, UiSettingsKeys.WorkspaceSymbolId),
        Text(
            UiSettingsKeys.WorkspaceSection,
            UiSettingsKeys.WorkspaceFavoriteSymbols,
            PortValueKind.MarkMapText),
        Text(
            UiSettingsKeys.WorkspaceSection,
            UiSettingsKeys.WorkspaceHiddenSymbols,
            PortValueKind.MarkMapText),

        // The local MCP server's enable choice (the settings window's MCP tab).
        Switch(
            UiSettingsKeys.McpSection,
            UiSettingsKeys.McpServerEnabled,
            UiSettingsKeys.DefaultMcpServerEnabled),

        // The external MCP client list (the MCP tab's editor rows, a
        // compact-JSON array in one string — the shipped default is no
        // entries).
        Text(
            McpClientManager.ServersSection,
            McpClientManager.ServersKey,
            PortValueKind.RowsText,
            Json(McpClientManager.DefaultServersJson)),

        // The notification center (T-097): the DND flag and the four
        // per-source mutes — the port roundtrips them faithfully, and a
        // reset hands out the open, unmuted default posture.
        Switch(
            NotificationSettingsKeys.Section,
            NotificationSettingsKeys.DoNotDisturb,
            NotificationSettingsKeys.DefaultDoNotDisturb),
        Switch(
            NotificationSettingsKeys.Section,
            NotificationSettingsKeys.MuteFill,
            NotificationSettingsKeys.DefaultMuted),
        Switch(
            NotificationSettingsKeys.Section,
            NotificationSettingsKeys.MuteCancel,
            NotificationSettingsKeys.DefaultMuted),
        Switch(
            NotificationSettingsKeys.Section,
            NotificationSettingsKeys.MuteReject,
            NotificationSettingsKeys.DefaultMuted),
        Switch(
            NotificationSettingsKeys.Section,
            NotificationSettingsKeys.MuteAdapterState,
            NotificationSettingsKeys.DefaultMuted),

        // The order ticket's pre-submit confirmation (T-078): the live and
        // paper master switches and the skip-below threshold.
        Switch(ConfirmSettingsKeys.Section, ConfirmSettingsKeys.Enabled, ConfirmSettingsKeys.EnabledValue),
        Switch(
            ConfirmSettingsKeys.Section,
            ConfirmSettingsKeys.EnabledInPaper,
            ConfirmSettingsKeys.EnabledInPaperValue),
        Number(
            ConfirmSettingsKeys.Section,
            ConfirmSettingsKeys.SkipBelowNotionalUsd,
            ConfirmSettingsKeys.SkipBelowNotionalValue),

        // The position sizing helper's default risk percent.
        Number(SizerSettingsKeys.Section, SizerSettingsKeys.DefaultRiskPercent, SizerSettingsKeys.DefaultRiskPercentValue),

        // The daily loss governor (T-189): the master switch and the loss
        // threshold, plus the manual reset marker (an ISO timestamp
        // string; absence is the default — the window then spans the whole
        // local day).
        Switch(RiskSettingsKeys.Section, RiskSettingsKeys.DailyLossLimitEnabled, RiskSettingsKeys.DailyLossLimitEnabledValue),
        Number(RiskSettingsKeys.Section, RiskSettingsKeys.DailyLossLimitAmount, RiskSettingsKeys.DailyLossLimitAmountValue),
        Text(RiskSettingsKeys.Section, RiskSettingsKeys.DailyLossResetAt),

        // The exchange transport endpoints (T-015): the URL pairs the
        // endpoint registry overrides its shipped production pairs with.
        UrlPairs(ExchangeEndpointRegistry.SettingsSection, ExchangeEndpointRegistry.BinanceKey, BinanceEndpoints.Production),
        UrlPairs(ExchangeEndpointRegistry.SettingsSection, ExchangeEndpointRegistry.BybitKey, BybitEndpoints.Production),
        UrlPairs(ExchangeEndpointRegistry.SettingsSection, ExchangeEndpointRegistry.OkxKey, OkxEndpoints.Production),

        // The AI module (Ollama endpoint + model tag, the local-first chat).
        Text(AiSettingsKeys.Section, AiSettingsKeys.OllamaUrl, json: Json(AiSettingsKeys.DefaultOllamaUrl)),
        Text(AiSettingsKeys.Section, AiSettingsKeys.Model, json: Json(AiSettingsKeys.DefaultModel)),

        // The candle cache policy (T-085).
        Switch(
            SqliteCandleCacheStore.SettingsSection,
            SqliteCandleCacheStore.EnabledSettingsKey,
            SqliteCandleCacheStore.DefaultEnabled),
        Number(
            SqliteCandleCacheStore.SettingsSection,
            SqliteCandleCacheStore.MaxBarsPerSeriesSettingsKey,
            SqliteCandleCacheStore.DefaultMaxBarsPerSeries),

        // The REST rate limiter's operator knobs (T-106): the global switch
        // and the background share. The per-adapter budget keys are formed
        // per registered adapter and stay outside the registry (see the
        // remarks).
        Switch(RestRateLimiterOptions.SettingsSection, RestRateLimiter.EnabledKey, true),
        Number(
            RestRateLimiterOptions.SettingsSection,
            RestRateLimiter.BackgroundShareKey,
            RestRateLimiterOptions.DefaultBackgroundShare),

        // The per-exchange fee schedules (T-080): a fresh install configures
        // nothing (the shipped paper schedule is code-side), so absence is
        // the default.
        Text(FeeScheduleService.SettingsSection, FeeScheduleService.SettingsKey, PortValueKind.ScheduleMap),

        // The user-managed watchlists (T-196): the whole document — the
        // lists and the active name — rides ONE versioned codec payload in
        // one string. A fresh install stores nothing (the starter majors
        // list stands), so the default IS absence — a reset removes the
        // stored document and the starter list returns.
        Text(WatchlistSettingsKeys.Section, WatchlistSettingsKeys.DocumentKey),
    ];

    /// <summary>Expose the fixed key list (the port service iterates it; the tests pin it).</summary>
    public static IReadOnlyList<PortSettingsKey> KeysList => Keys;

    /// <summary>
    /// Look up the registry entry for a stored (section, key) pair: an
    /// unknown pair is null — and the port rejects any import carrying it.
    /// </summary>
    public static PortSettingsKey? TryGet(string section, string key) =>
        Keys.FirstOrDefault(candidate =>
            string.Equals(candidate.Section, section, StringComparison.Ordinal) &&
            string.Equals(candidate.Key, key, StringComparison.Ordinal));

    /// <summary>
    /// Validate one raw value against its entry's stored-shape kind: the
    /// port-level gate an import (or a test) rides before anything applies.
    /// </summary>
    public static bool IsValueValid(PortSettingsKey key, JsonElement value)
    {
        switch (key.Kind)
        {
            case PortValueKind.Text:
                return value.ValueKind == JsonValueKind.String;
            case PortValueKind.Switch:
                return value.ValueKind is JsonValueKind.True or JsonValueKind.False;
            case PortValueKind.Number:
                return value.ValueKind == JsonValueKind.Number;
            case PortValueKind.TimeFrameName:
                return value.ValueKind == JsonValueKind.String &&
                       value.GetString() is { } stored &&
                       ParseDefinedTimeFrame(stored);
            case PortValueKind.RowsText:
                return IsJsonTextOf(value, JsonValueKind.Array);
            case PortValueKind.MarkMapText:
                return IsJsonTextOf(value, JsonValueKind.Object);
            case PortValueKind.UrlPairs:
                return value.ValueKind == JsonValueKind.Object &&
                       value.EnumerateObject().All(pair =>
                           pair.Value.ValueKind == JsonValueKind.String ||
                           (pair.Value.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                            string.Equals(pair.Name, IsTestnetPropertyName, StringComparison.Ordinal)));
            case PortValueKind.ScheduleMap:
                return value.ValueKind == JsonValueKind.Object &&
                       value.EnumerateObject().All(pair => pair.Value.ValueKind == JsonValueKind.Object);
            default:
                return false;
        }
    }

    /// <summary>A text (shape-checked) entry.</summary>
    private static PortSettingsKey Text(
        string section,
        string key,
        PortValueKind kind = PortValueKind.Text,
        JsonElement? json = null) => new(section, key, kind, json);

    /// <summary>A switch entry a stored copy realigns to the shipped default on reset.</summary>
    private static PortSettingsKey Switch(
        string section,
        string key,
        bool shippedDefault) =>
        new(section, key, PortValueKind.Switch, Json(shippedDefault));

    /// <summary>A number entry a stored copy realigns to the shipped default on reset.</summary>
    private static PortSettingsKey Number(
        string section,
        string key,
        double shippedDefault) =>
        new(section, key, PortValueKind.Number, Json(shippedDefault));

    /// <summary>A number entry whose shipped default is an integer (periods, counts, thresholds).</summary>
    private static PortSettingsKey Number(
        string section,
        string key,
        int shippedDefault) =>
        new(section, key, PortValueKind.Number, Json(shippedDefault));

    /// <summary>A number entry whose shipped default is a decimal threshold.</summary>
    private static PortSettingsKey Number(
        string section,
        string key,
        decimal shippedDefault) =>
        new(section, key, PortValueKind.Number, Json(shippedDefault));

    /// <summary>An endpoint URL-pairs entry a stored copy realigns to the shipped production pair on reset.</summary>
    private static PortSettingsKey UrlPairs<TPair>(
        string section,
        string key,
        TPair shippedDefault)
        where TPair : notnull =>
        new(section, key, PortValueKind.UrlPairs, Json(shippedDefault));

    /// <summary>Serialize one shipped default with the store's value shape.</summary>
    private static JsonElement Json<TValue>(TValue value) where TValue : notnull =>
        JsonSerializer.SerializeToElement(value, DefaultValueOptions);

    /// <summary>Whether the value is a string whose payload parses as a JSON document of the wanted kind.</summary>
    private static bool IsJsonTextOf(JsonElement value, JsonValueKind wantedKind)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(value.GetString()!);
            return document.RootElement.ValueKind == wantedKind;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>The chart pane's own stored-name rule: the defined frame names, case-insensitive.</summary>
    private static bool ParseDefinedTimeFrame(string stored) =>
        Enum.TryParse(stored.Trim(), ignoreCase: true, out TimeFrame parsed) &&
        Enum.IsDefined(typeof(TimeFrame), parsed);
}
