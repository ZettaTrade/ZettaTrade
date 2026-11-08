namespace ZettaTrade.App.Services;

using System.Text;
using System.Text.Json;

/// <summary>
/// The alert definitions' persistence codec (T-096): one JSON payload inside
/// the <c>Alerts / Definitions</c> settings key, encoded and decoded through
/// this single named seam so the on-wire shape stays exactly this
/// implementation's business. The payload is versioned and all-or-nothing at
/// the ROOT — a wholly unreadable roster throws (the caller decides what a
/// broken payload means) — while an entry that cannot be honored is skipped
/// and counted, never fabricated: its facts stay in the stored key (the
/// engine never rewrites the key on load) and no half-decoded roster is
/// substituted.
/// </summary>
public static class AlertCodec
{
    #region Private Fields

    /// <summary>The only payload version this codec writes or reads.</summary>
    private const int PayloadVersion = 1;

    /// <summary>The payload's member names (written exact, read case-insensitive).</summary>
    private const string VersionMember = "version";
    private const string AlertsMember = "alerts";

    /// <summary>The stored entry's member names.</summary>
    private const string IdMember = "id";
    private const string SymbolMember = "symbol";
    private const string KindMember = "kind";
    private const string TriggerMember = "trigger";
    private const string TargetPriceMember = "targetPrice";
    private const string ThresholdPercentMember = "thresholdPercent";
    private const string WindowMinutesMember = "windowMinutes";
    private const string NoteMember = "note";
    private const string StatusMember = "status";
    private const string CreatedUtcMember = "createdUtc";
    private const string SnoozedUntilUtcMember = "snoozedUntilUtc";
    private const string LastFiredUtcMember = "lastFiredUtc";

    /// <summary>The enums the codec stores: every value parses by its exact enum name, case-insensitively.</summary>
    private static readonly Dictionary<string, PriceAlertKind> StoredKinds = Built(PriceAlertKind.Price, PriceAlertKind.PercentMove);
    private static readonly Dictionary<string, AlertTrigger> StoredTriggers = Built(AlertTrigger.Above, AlertTrigger.Below, AlertTrigger.At);
    private static readonly Dictionary<string, AlertStatus> StoredStatuses = Built(AlertStatus.Armed, AlertStatus.Snoozed, AlertStatus.Fired);

    /// <summary>The payload's UTF-8 encoding the codec authors itself (the settings store is UTF-8).</summary>
    private static readonly Encoding PayloadEncoding = Encoding.UTF8;

    /// <summary>Build a case-insensitive name→value map from the enum's own values (no hand-copied strings).</summary>
    private static Dictionary<string, T> Built<T>(params T[] values)
        where T : struct, Enum =>
        values.ToDictionary(value => value.ToString()!, StringComparer.OrdinalIgnoreCase);

    #endregion Private Fields

    #region Encoding

    /// <summary>
    /// Encode the definitions roster as the versioned settings payload; an
    /// empty roster encodes as the skeleton (the shape is always exact —
    /// nothing inspected later relies on trailing fields).
    /// </summary>
    public static string Encode(IReadOnlyList<AlertDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber(VersionMember, PayloadVersion);
            writer.WritePropertyName(AlertsMember);
            writer.WriteStartArray();
            foreach (var definition in definitions)
            {
                WriteDefinition(writer, definition);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return PayloadEncoding.GetString(buffer.ToArray());
    }

    /// <summary>Write one stored entry; every fact lands, the absent ones stay absent.</summary>
    private static void WriteDefinition(Utf8JsonWriter writer, AlertDefinition definition)
    {
        writer.WriteStartObject();
        writer.WriteString(IdMember, definition.Id.ToString());
        writer.WriteString(SymbolMember, definition.SymbolName);
        writer.WriteString(KindMember, definition.Kind.ToString());
        writer.WriteString(TriggerMember, definition.Trigger.ToString());
        writer.WriteNumber(TargetPriceMember, definition.TargetPrice);
        writer.WriteNumber(ThresholdPercentMember, definition.ThresholdPercent);
        writer.WriteNumber(WindowMinutesMember, definition.WindowMinutes);
        writer.WriteString(NoteMember, definition.Note);
        writer.WriteString(StatusMember, definition.Status.ToString());
        writer.WriteNumber(CreatedUtcMember, definition.CreatedUtc.Ticks);
        if (definition.SnoozedUntilUtc is { } snoozed)
        {
            writer.WriteNumber(SnoozedUntilUtcMember, snoozed.Ticks);
        }

        if (definition.LastFiredUtc is { } fired)
        {
            writer.WriteNumber(LastFiredUtcMember, fired.Ticks);
        }

        writer.WriteEndObject();
    }

    #endregion Encoding

    #region Decoding

    /// <summary>
    /// Decode one stored payload: the root must carry the wrapper shape
    /// (version 1 + an alerts array), every entry that cannot be honored
    /// exactly is skipped with a count — no default-substituted rows. A
    /// wholly unreadable root — malformed JSON or a wrong shape alike —
    /// throws ONE uniform <see cref="ArgumentException"/> to the caller
    /// (broken payloads are a caller's logged fault; nothing here fabricates
    /// an empty roster).
    /// </summary>
    public static AlertDecodingResult Decode(string payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);
        try
        {
            return DecodeCore(payload);
        }
        catch (JsonException ex)
        {
            // Malformed JSON is the same caller fault as a wrong shape: one
            // uniform, documented exception instead of implementation leaks.
            throw new ArgumentException(
                "The alert payload is not a recognizable versioned roster.",
                nameof(payload),
                ex);
        }
    }

    /// <summary>The parsing core behind <see cref="Decode"/> (its wrapped fault surface).</summary>
    private static AlertDecodingResult DecodeCore(string payload)
    {
        using var document = JsonDocument.Parse(payload, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        });
        var root = document.RootElement;
        if (root.ValueKind is not JsonValueKind.Object)
        {
            throw new ArgumentException("The alert payload is not a recognizable versioned roster.", nameof(payload));
        }

        if (FindMember(root, VersionMember) is not { Value.ValueKind: JsonValueKind.Number } versionValue ||
            !versionValue.Value.TryGetInt32(out var parsedVersion) ||
            parsedVersion != PayloadVersion ||
            FindMember(root, AlertsMember) is not { Value.ValueKind: JsonValueKind.Array } alerts)
        {
            throw new ArgumentException("The alert payload is not a recognizable versioned roster.", nameof(payload));
        }

        var valid = new List<AlertDefinition>();
        var skipped = 0;
        foreach (var element in alerts.Value.EnumerateArray())
        {
            if (TryReadDefinition(element, out var definition))
            {
                valid.Add(definition);
            }
            else
            {
                skipped++;
            }
        }

        return new AlertDecodingResult(valid, skipped);
    }

    /// <summary>Read one stored entry — exact or not at all.</summary>
    private static bool TryReadDefinition(JsonElement entry, out AlertDefinition definition)
    {
        definition = null!;
        if (entry.ValueKind is not JsonValueKind.Object)
        {
            return false;
        }

        var id = ReadIdMember(entry, IdMember);
        var symbol = ReadStringMember(entry, SymbolMember);
        var kind = ReadEnum(entry, KindMember, StoredKinds);
        var status = ReadEnum(entry, StatusMember, StoredStatuses);
        var created = ReadTicksMember(entry, CreatedUtcMember);
        var targetPrice = ReadNumberMember(entry, TargetPriceMember);
        var thresholdPercent = ReadNumberMember(entry, ThresholdPercentMember);
        var windowMinutes = ReadIntegerMember(entry, WindowMinutesMember) ?? 0;

        // Each kind reads only its own facts and demands them exactly: a
        // price entry needs a positive level AND a named edge, a percent-move
        // entry a positive percent inside a positive window. Facts the other
        // kind unused-fields (even junk there) stay harmlessly decodable —
        // the demark is the kind, never a silent value guess.
        if (id == Guid.Empty ||
            symbol is not { Length: > 0 } ||
            kind is null ||
            status is null ||
            created is null)
        {
            return false;
        }

        if (kind == PriceAlertKind.Price &&
            (targetPrice is not { } parsedTarget || parsedTarget <= 0m))
        {
            return false;
        }

        // The resolved trigger: the Price kind's stored edge — the PercentMove
        // kind leaves the neutral At slot in place (its direction rides the
        // threshold magnitude, never the stored edge).
        var parsedTrigger = kind == PriceAlertKind.Price
            ? ReadEnum(entry, TriggerMember, StoredTriggers)
            : AlertTrigger.At;
        if (parsedTrigger is null)
        {
            return false;
        }

        if (kind == PriceAlertKind.PercentMove &&
            (thresholdPercent is not { } parsedThreshold || parsedThreshold <= 0m || windowMinutes <= 0))
        {
            return false;
        }

        definition = new AlertDefinition(
            id,
            symbol,
            kind.Value,
            parsedTrigger.Value,
            targetPrice ?? 0m,
            thresholdPercent ?? 0m,
            windowMinutes,
            ReadStringMember(entry, NoteMember) ?? string.Empty,
            status.Value,
            created.Value,
            ReadTicksMember(entry, SnoozedUntilUtcMember),
            ReadTicksMember(entry, LastFiredUtcMember));
        return true;
    }

    /// <summary>Find a member on one stored element by case-insensitive name; unresolved names yield null.</summary>
    private static JsonProperty? FindMember(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property;
            }
        }

        return null;
    }

    /// <summary>Read one enum member by exact enum-name, case-insensitively; anything else is null (no coercion).</summary>
    private static T? ReadEnum<T>(JsonElement element, string name, IReadOnlyDictionary<string, T> stored)
        where T : struct, Enum =>
        FindMember(element, name) is { Value.ValueKind: JsonValueKind.String } property &&
        property.Value.GetString() is { } text &&
        stored.TryGetValue(text, out var value)
            ? value
            : null;

    /// <summary>Read one string member; anything but a string is null.</summary>
    private static string? ReadStringMember(JsonElement element, string name) =>
        FindMember(element, name) is { Value.ValueKind: JsonValueKind.String } property
            ? property.Value.GetString()
            : null;

    /// <summary>Read one integer member; anything but a number is null.</summary>
    private static int? ReadIntegerMember(JsonElement element, string name) =>
        FindMember(element, name) is { Value.ValueKind: JsonValueKind.Number, Value: { } value } &&
        value.TryGetInt32(out var parsed)
            ? parsed
            : null;

    /// <summary>Read one decimal member; anything but a number is null.</summary>
    private static decimal? ReadNumberMember(JsonElement element, string name) =>
        FindMember(element, name) is { Value.ValueKind: JsonValueKind.Number, Value: { } value } &&
        value.TryGetDecimal(out var parsed)
            ? parsed
            : null;

    /// <summary>Read one ticks-encoded UTC stamp member; anything but a long is null.</summary>
    private static DateTimeOffset? ReadTicksMember(JsonElement element, string name) =>
        FindMember(element, name) is { Value.ValueKind: JsonValueKind.Number, Value: { } value } &&
        value.TryGetInt64(out var parsed)
            ? new DateTimeOffset(parsed, TimeSpan.Zero)
            : null;

    /// <summary>The GUID identity; both a non-string and an unparsable text are an empty (rejected) id.</summary>
    private static Guid ReadIdMember(JsonElement element, string name) =>
        FindMember(element, name) is { Value.ValueKind: JsonValueKind.String, Value: { } value } &&
        Guid.TryParse(value.GetString(), out var parsed)
            ? parsed
            : Guid.Empty;

    #endregion Decoding
}

/// <summary>
/// One decode pass' outcome (T-096): the entries that can be evaluated
/// exactly and the count of the skipped ones, so the caller can WARN with the
/// exact magnitude instead of pretending every stored alert survived.
/// </summary>
/// <param name="Valid">The decodable definitions, in stored order.</param>
/// <param name="SkippedCount">How many stored entries could not be honored exactly.</param>
public sealed record AlertDecodingResult(IReadOnlyList<AlertDefinition> Valid, int SkippedCount);
