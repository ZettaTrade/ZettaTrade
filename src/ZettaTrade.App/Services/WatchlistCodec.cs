namespace ZettaTrade.App.Services;

using System.Text;
using System.Text.Json;
using ZettaTrade.App;

/// <summary>
/// The watchlist document's persistence codec (T-196): one JSON payload
/// inside the <c>Watchlists / Document</c> settings key, encoded and decoded
/// through this single named seam so the on-wire shape stays exactly this
/// implementation's business. The payload is versioned and all-or-nothing at
/// the ROOT — a wholly unreadable document throws (the caller decides what a
/// broken payload means; the catalog answers with the starter document) —
/// while a list entry that cannot be honored is skipped and counted, never
/// fabricated: its facts stay in the stored key (nothing here rewrites the
/// key on load) and no half-decoded document is substituted. Symbol texts
/// are carried raw — parsing them is the feed's own guard.
/// </summary>
public static class WatchlistCodec
{
    #region Private Fields

    /// <summary>The only payload version this codec writes or reads.</summary>
    private const int PayloadVersion = 1;

    /// <summary>The payload's member names (written exact, read case-insensitive).</summary>
    private const string VersionMember = "version";
    private const string ActiveMember = "active";
    private const string ListsMember = "lists";

    /// <summary>The stored list entry's member names.</summary>
    private const string NameMember = "name";
    private const string SymbolsMember = "symbols";

    /// <summary>The payload's UTF-8 encoding the codec authors itself (the settings store is UTF-8).</summary>
    private static readonly Encoding PayloadEncoding = Encoding.UTF8;

    #endregion Private Fields

    #region Starter

    /// <summary>
    /// The starter roster's symbols (normalized names): the majors list
    /// every fresh install shows, priced by whichever adapter serves them.
    /// The feed holds no copy of its own — the starter document below is
    /// the single source.
    /// </summary>
    public static readonly IReadOnlyList<string> StarterSymbols =
    [
        "BTC/USDT",
        "ETH/USDT",
        "SOL/USDT",
        "XRP/USDT",
        "ADA/USDT",
        "DOGE/USDT",
        "BNB/USDT",
        "LINK/USDT",
        "AVAX/USDT",
        "LTC/USDT",
    ];

    /// <summary>
    /// The starter document: one list — the majors under the section's
    /// anchor name — active. The fresh-install state, the factory-reset
    /// state and the fallback for an unreadable stored document alike.
    /// </summary>
    public static WatchlistDocument StarterDocument() => new(
        WatchlistSettingsKeys.StarterListName,
        [new WatchlistList(WatchlistSettingsKeys.StarterListName, [.. StarterSymbols])]);

    #endregion Starter

    #region Encoding

    /// <summary>
    /// Encode the document as the versioned settings payload; an empty
    /// symbols roster encodes as an empty array (the shape is always exact —
    /// nothing inspected later relies on trailing fields).
    /// </summary>
    public static string Encode(WatchlistDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber(VersionMember, PayloadVersion);
            writer.WriteString(ActiveMember, document.ActiveList);
            writer.WritePropertyName(ListsMember);
            writer.WriteStartArray();
            foreach (var list in document.Lists)
            {
                writer.WriteStartObject();
                writer.WriteString(NameMember, list.Name);
                writer.WritePropertyName(SymbolsMember);
                writer.WriteStartArray();
                foreach (var symbol in list.Symbols)
                {
                    writer.WriteStringValue(symbol);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return PayloadEncoding.GetString(buffer.ToArray());
    }

    #endregion Encoding

    #region Decoding

    /// <summary>
    /// Decode one stored payload: the root must carry the wrapper shape
    /// (version 1, an active name and a lists array), and every list entry
    /// that cannot be honored exactly is skipped with a count — no
    /// default-substituted rows. A wholly unreadable root — malformed JSON
    /// or a wrong shape alike — throws ONE uniform
    /// <see cref="ArgumentException"/> to the caller (broken payloads are a
    /// caller's logged fault; nothing here fabricates a document).
    /// </summary>
    public static WatchlistDecodingResult Decode(string payload)
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
                "The watchlist payload is not a recognizable versioned document.",
                nameof(payload),
                ex);
        }
    }

    /// <summary>The parsing core behind <see cref="Decode"/> (its wrapped fault surface).</summary>
    private static WatchlistDecodingResult DecodeCore(string payload)
    {
        using var document = JsonDocument.Parse(payload, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        });
        var root = document.RootElement;
        if (root.ValueKind is not JsonValueKind.Object)
        {
            throw new ArgumentException("The watchlist payload is not a recognizable versioned document.", nameof(payload));
        }

        if (FindMember(root, VersionMember) is not { Value.ValueKind: JsonValueKind.Number } versionValue ||
            !versionValue.Value.TryGetInt32(out var parsedVersion) ||
            parsedVersion != PayloadVersion ||
            FindMember(root, ActiveMember) is not { Value.ValueKind: JsonValueKind.String } active ||
            FindMember(root, ListsMember) is not { Value.ValueKind: JsonValueKind.Array } lists)
        {
            throw new ArgumentException("The watchlist payload is not a recognizable versioned document.", nameof(payload));
        }

        var valid = new List<WatchlistList>();
        var skipped = 0;
        foreach (var element in lists.Value.EnumerateArray())
        {
            if (TryReadList(element, out var list))
            {
                valid.Add(list);
            }
            else
            {
                skipped++;
            }
        }

        return new WatchlistDecodingResult(
            new WatchlistDocument(active.Value.GetString()!, valid),
            skipped);
    }

    /// <summary>Read one stored list entry — exact or not at all.</summary>
    private static bool TryReadList(JsonElement entry, out WatchlistList list)
    {
        list = null!;
        if (entry.ValueKind is not JsonValueKind.Object ||
            FindMember(entry, NameMember) is not { Value.ValueKind: JsonValueKind.String } name ||
            string.IsNullOrWhiteSpace(name.Value.GetString()) ||
            FindMember(entry, SymbolsMember) is not { Value.ValueKind: JsonValueKind.Array } symbols)
        {
            return false;
        }

        var texts = new List<string>();
        foreach (var element in symbols.Value.EnumerateArray())
        {
            // The stored symbol texts are strings, exactly — a foreign kind
            // means the entry cannot be honored as stored, so the whole
            // entry is skipped (never a partial or fabricated roster).
            if (element.ValueKind is not JsonValueKind.String)
            {
                return false;
            }

            texts.Add(element.GetString()!);
        }

        list = new WatchlistList(name.Value.GetString()!, texts);
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

    #endregion Decoding
}

/// <summary>
/// The user's watchlist document (T-196): the active list's name plus the
/// lists themselves, in stored order. Immutable — every catalog mutation
/// produces a fresh instance, and the in-memory swap is one atomic
/// reference write.
/// </summary>
/// <param name="ActiveList">The active list's name (matched case-insensitively by the consumers).</param>
/// <param name="Lists">The lists, in stored order.</param>
public sealed record WatchlistDocument(string ActiveList, IReadOnlyList<WatchlistList> Lists);

/// <summary>
/// One named symbol list inside the document: the stored name (the starter
/// rides the section's anchor name) and the symbol texts in stored order —
/// carried raw, parsed nowhere before the feed.
/// </summary>
/// <param name="Name">The list's name.</param>
/// <param name="Symbols">The symbol texts, in stored order.</param>
public sealed record WatchlistList(string Name, IReadOnlyList<string> Symbols);

/// <summary>
/// One decode pass' outcome (T-196): the document the payload holds plus
/// the count of the skipped entries, so the caller can WARN with the exact
/// magnitude instead of pretending every stored list survived.
/// </summary>
/// <param name="Document">The decoded document (the active name exactly as stored).</param>
/// <param name="SkippedCount">How many stored list entries could not be honored exactly.</param>
public sealed record WatchlistDecodingResult(WatchlistDocument Document, int SkippedCount);
