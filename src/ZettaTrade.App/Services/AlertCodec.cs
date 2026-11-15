namespace ZettaTrade.App.Services;

using System.Linq;
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

    /// <summary>
    /// The only payload version this codec writes (T-204's roster schema
    /// versioning: version 3 carries the re-arm facts — the mode, the
    /// cooldown window and the sound choice). Reads versions 1-3 — a
    /// version 1 roster (T-096's shape) decodes exactly as it always did
    /// and version 2 (T-203's) likewise; both predate the re-arm facts and
    /// decode them as the defaults (OneShot, no window, the beep), while
    /// version 3's entries demand their re-arm facts by name.
    /// </summary>
    private const int PayloadVersion = 3;

    /// <summary>The payload versions this codec reads (it writes only <see cref="PayloadVersion"/>).</summary>
    private static readonly int[] ReadableVersions = [1, 2, PayloadVersion];

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
    private const string IndicatorMember = "indicator";
    private const string IndicatorNameMember = "name";
    private const string IndicatorPeriodMember = "period";
    private const string IndicatorSourceMember = "source";
    private const string IndicatorLevelMember = "level";
    private const string ReArmMember = "reArm";
    private const string CooldownMinutesMember = "cooldownMinutes";
    private const string SoundMember = "sound";

    /// <summary>The enums the codec stores: every value parses by its exact enum name, case-insensitively.</summary>
    private static readonly Dictionary<string, PriceAlertKind> StoredKinds =
        Built(PriceAlertKind.Price, PriceAlertKind.PercentMove, PriceAlertKind.IndicatorCross, PriceAlertKind.FundingCross);
    private static readonly Dictionary<string, AlertTrigger> StoredTriggers = Built(AlertTrigger.Above, AlertTrigger.Below, AlertTrigger.At);
    private static readonly Dictionary<string, AlertStatus> StoredStatuses =
        Built(AlertStatus.Armed, AlertStatus.Snoozed, AlertStatus.Fired, AlertStatus.Paused);
    private static readonly Dictionary<string, AlertCrossSource> StoredSources =
        Built(AlertCrossSource.IndicatorValue, AlertCrossSource.Price);
    private static readonly Dictionary<string, AlertReArmMode> StoredReArmModes =
        Built(AlertReArmMode.OneShot, AlertReArmMode.PerCross, AlertReArmMode.Cooldown);
    private static readonly Dictionary<string, AlertSoundChoice> StoredSounds =
        Built(AlertSoundChoice.Silent, AlertSoundChoice.Beep);

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

        if (definition.Indicator is { } indicator)
        {
            writer.WriteStartObject(IndicatorMember);
            writer.WriteString(IndicatorNameMember, indicator.Name);
            writer.WriteNumber(IndicatorPeriodMember, indicator.Period);
            writer.WriteString(IndicatorSourceMember, indicator.Source.ToString());
            writer.WriteNumber(IndicatorLevelMember, indicator.Level);
            writer.WriteEndObject();
        }

        // The re-arm facts (T-204): version 3 demands them on every entry,
        // so the mode and the sound always land — even at their defaults —
        // and the cooldown window lands only when its mode reads it.
        writer.WriteString(ReArmMember, definition.ReArm.ToString());
        if (definition.CooldownMinutes > 0)
        {
            writer.WriteNumber(CooldownMinutesMember, definition.CooldownMinutes);
        }

        writer.WriteString(SoundMember, definition.Sound.ToString());
        writer.WriteEndObject();
    }

    #endregion Encoding

    #region Decoding

    /// <summary>
    /// Decode one stored payload: the root must carry the wrapper shape
    /// (a readable version — the codec writes 3 and reads 1, 2 and 3 —
    /// plus an alerts array), every entry that cannot be honored exactly is
    /// skipped with a count — no default-substituted rows. A wholly
    /// unreadable root
    /// — malformed JSON or a wrong shape alike — throws ONE uniform
    /// <see cref="ArgumentException"/> to the caller (broken payloads are a
    /// caller's logged fault; nothing here fabricates an empty roster).
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
            !ReadableVersions.Contains(parsedVersion) ||
            FindMember(root, AlertsMember) is not { Value.ValueKind: JsonValueKind.Array } alerts)
        {
            throw new ArgumentException("The alert payload is not a recognizable versioned roster.", nameof(payload));
        }

        var valid = new List<AlertDefinition>();
        var skipped = 0;
        foreach (var element in alerts.Value.EnumerateArray())
        {
            if (TryReadDefinition(element, parsedVersion, out var definition))
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
    private static bool TryReadDefinition(JsonElement entry, int payloadVersion, out AlertDefinition definition)
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
        // entry a positive percent inside a positive window, an
        // indicator-cross entry (T-203) a resolvable-looking spec with a real
        // cross edge, a funding-cross entry (T-240) a present (signed)
        // threshold member with a real cross edge. Facts the other kinds
        // unused-fields (even junk there) stay harmlessly decodable — the
        // demark is the kind, never a silent value guess.
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
        // threshold magnitude, never the stored edge) — and the
        // IndicatorCross kind demands a real cross edge by name (At is
        // meaningless for a cross: an entry carrying it is skipped).
        AlertTrigger? parsedTrigger;
        if (kind == PriceAlertKind.Price)
        {
            parsedTrigger = ReadEnum(entry, TriggerMember, StoredTriggers);
            if (parsedTrigger is null)
            {
                return false;
            }
        }
        else if (kind == PriceAlertKind.IndicatorCross)
        {
            parsedTrigger = ReadEnum(entry, TriggerMember, StoredTriggers);
            if (parsedTrigger is not AlertTrigger.Above and not AlertTrigger.Below)
            {
                return false;
            }
        }
        else if (kind == PriceAlertKind.FundingCross)
        {
            parsedTrigger = ReadEnum(entry, TriggerMember, StoredTriggers);
            if (parsedTrigger is not AlertTrigger.Above and not AlertTrigger.Below)
            {
                return false;
            }
        }
        else
        {
            parsedTrigger = AlertTrigger.At;
        }

        if (kind == PriceAlertKind.PercentMove &&
            (thresholdPercent is not { } parsedThreshold || parsedThreshold <= 0m || windowMinutes <= 0))
        {
            return false;
        }

        // The funding-cross threshold (T-240): the member's PRESENCE is the
        // demand — its stored value is a signed percent (zero and negatives
        // are honest thresholds), so only a missing or non-numeric member is
        // the skipped entry.
        if (kind == PriceAlertKind.FundingCross && targetPrice is null)
        {
            return false;
        }

        // The indicator-cross spec (T-203): the nested object is demanded
        // whole — a name, a strictly positive period, a stored source and a
        // level number for the indicator-value source. A version 1 payload
        // predates the kind entirely: an entry claiming it there is skipped
        // (its facts cannot exist in that shape).
        AlertIndicatorSpec? indicator = null;
        if (kind == PriceAlertKind.IndicatorCross)
        {
            if (payloadVersion < 2 ||
                FindMember(entry, IndicatorMember) is not { Value.ValueKind: JsonValueKind.Object } indicatorEntry)
            {
                return false;
            }

            var name = ReadStringMember(indicatorEntry.Value, IndicatorNameMember);
            var period = ReadIntegerMember(indicatorEntry.Value, IndicatorPeriodMember);
            var source = ReadEnum(indicatorEntry.Value, IndicatorSourceMember, StoredSources);
            var level = ReadNumberMember(indicatorEntry.Value, IndicatorLevelMember);
            if (name is not { Length: > 0 } ||
                period is not { } parsedPeriod || parsedPeriod <= 0 ||
                source is null ||
                (source == AlertCrossSource.IndicatorValue && level is null))
            {
                return false;
            }

            indicator = new AlertIndicatorSpec(name, parsedPeriod, source.Value, level ?? 0m);
        }

        // The re-arm facts (T-204): a version 3 entry demands its mode and
        // its sound by exact enum name, and a Cooldown mode its window; a
        // version 1/2 payload predates the facts entirely and decodes them
        // as the defaults (the terminal one-shot with the beep). The
        // cooldown minutes of the other modes are unused facts — junk there
        // stays harmlessly decodable, the demark is the mode.
        AlertReArmMode reArm;
        AlertSoundChoice sound;
        int cooldownMinutes;
        if (payloadVersion < 3)
        {
            reArm = AlertReArmMode.OneShot;
            cooldownMinutes = 0;
            sound = AlertSoundChoice.Beep;
        }
        else
        {
            var parsedReArm = ReadEnum(entry, ReArmMember, StoredReArmModes);
            var parsedSound = ReadEnum(entry, SoundMember, StoredSounds);
            cooldownMinutes = ReadIntegerMember(entry, CooldownMinutesMember) ?? 0;
            if (parsedReArm is null || parsedSound is null ||
                (parsedReArm == AlertReArmMode.Cooldown && cooldownMinutes <= 0))
            {
                return false;
            }

            reArm = parsedReArm.Value;
            sound = parsedSound.Value;
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
            ReadTicksMember(entry, LastFiredUtcMember),
            indicator,
            reArm,
            cooldownMinutes,
            sound);
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
