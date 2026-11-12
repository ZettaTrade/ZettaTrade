namespace ZettaTrade.AdapterKit.Tests.Capture;

using System.Text.Json;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// The hydrated, validated capture pack one adapter runs the conformance
/// battery under: a <see cref="CapturePackDocument"/> plus the parsed
/// symbols, capability flags, guard set and transcript frame scripts it
/// declares. Loading is failing-fast on purpose: a malformed pack is a kit
/// configuration error, never a silently weakened battery.
/// </summary>
/// <param name="AdapterId">The adapter identity the pack stands for.</param>
/// <param name="AdapterName">The display name the pack stands for.</param>
/// <param name="Capabilities">The capability flags the pack stands for.</param>
/// <param name="ConnectsAtConstruction">Whether the pack declares the adapter connected straight after construction.</param>
/// <param name="Symbols">The captured universe, in the order the adapter serves it.</param>
/// <param name="StrangerSymbol">The outside-the-universe probe.</param>
/// <param name="ClientSideGuards">The guard names the pack owns (the battery asserts each one).</param>
/// <param name="DepthProfile">The depth contract declaration.</param>
/// <param name="ServedBookDepths">The served depths the pack declares (null = the adapter does not declare a set).</param>
/// <param name="ClampedServedDepth">The clamp anchor the pack declares (clamped packs).</param>
/// <param name="DepthProbeRequests">The raw requests the resolution leg probes.</param>
/// <param name="RejectedDepthRequest">The raw request the rejection leg attaches (explicit-set packs).</param>
/// <param name="DelegatedServedDepths">The delegate's served depths the pack judges against (delegated packs).</param>
/// <param name="BookTopicTemplate">The topic template for symbol-first dialects (transcript packs; alternative to the prefix).</param>
/// <param name="BookTopicPrefix">The wire prefix of this pack's book topics.</param>
/// <param name="SubscribeMarker">The wire marker naming a subscribe operation in this pack's dialect.</param>
/// <param name="ReconnectMode">The restore semantics declaration.</param>
/// <param name="TranscriptSessions">The recorded sessions, in session order.</param>
/// <param name="RestCannedRoutes">The canned REST routes (path fragment → response body) the harness binds.</param>
public sealed record AdapterCapturePack(
    string AdapterId,
    string AdapterName,
    ExchangeCapabilities Capabilities,
    bool ConnectsAtConstruction,
    IReadOnlyList<Symbol> Symbols,
    Symbol StrangerSymbol,
    IReadOnlyCollection<string> ClientSideGuards,
    CaptureDepthProfile DepthProfile,
    IReadOnlySet<int>? ServedBookDepths,
    int? ClampedServedDepth,
    IReadOnlyList<int> DepthProbeRequests,
    int? RejectedDepthRequest,
    IReadOnlySet<int>? DelegatedServedDepths,
    string? BookTopicTemplate,
    string BookTopicPrefix,
    string SubscribeMarker,
    CaptureReconnectMode ReconnectMode,
    IReadOnlyList<AdapterCaptureSession> TranscriptSessions,
    IReadOnlyDictionary<string, string> RestCannedRoutes)
{
    /// <summary>The depth contract is an explicit served set: outside the set the request is rejected (BUG-032), never defaulted.</summary>
    public const string ExplicitSet = "explicit-set";

    /// <summary>The depth contract accepts any request and shapes the answer to its own capacity.</summary>
    public const string Clamped = "clamped";

    /// <summary>
    /// The book face delegates to another adapter (a trading shell over a
    /// market-data adapter): no served-set surface of its own; the delegate's
    /// set is the pack's declaration.
    /// </summary>
    public const string Delegated = "delegated";

    /// <summary>The stream surface never drops, so nothing needs a reconnect protocol.</summary>
    public const string AlwaysOn = "always-on";

    /// <summary>The adapter reconnects on its own and re-serves its tracked topics from the recorded sessions.</summary>
    public const string Transcript = "transcript";

    /// <summary>Unknown symbols are a client-side error on every subscribe face (rejected, not defaulted).</summary>
    public const string GuardUnknownSymbol = "unknown-symbol";

    /// <summary>A non-positive bar limit is a client-side error.</summary>
    public const string GuardBarsLimit = "bars-limit";

    /// <summary>Blank endpoint configuration is a constructor error.</summary>
    public const string GuardBlankEndpoints = "blank-endpoints";

    /// <summary>The JSON options the pack documents read through.</summary>
    private static readonly JsonSerializerOptions PackOptions = new(JsonSerializerDefaults.Web);

    /// <summary>The session-local capability text separator (spaces around the pipe read as declaration prose, not data).</summary>
    private const string CapabilitySeparator = " | ";

    #region Methods: Loader

    /// <summary>
    /// Load the pack named <paramref name="packName"/> from the kit's
    /// <c>Captures</c> folder: <c>Pack.json</c> plus every session script it
    /// declares. Cross-checked against the folder on disk: a declared file
    /// that is missing fails the load.
    /// </summary>
    /// <param name="packName">The capture folder name under <c>Captures</c>.</param>
    public static AdapterCapturePack FromDirectory(string packName)
    {
        var packDirectory = Path.Combine(AppContext.BaseDirectory, "Captures", packName);
        var document = JsonSerializer.Deserialize<CapturePackDocument>(
            File.ReadAllText(Path.Combine(packDirectory, "Pack.json")), PackOptions)
            ?? throw new InvalidOperationException($"The {packName} capture pack is empty.");

        var sessions = document.TranscriptSessions.Select(session =>
        {
            var frames = JsonSerializer.Deserialize<IReadOnlyList<string>>(
                File.ReadAllText(Path.Combine(packDirectory, session.Script)), PackOptions)
                ?? throw new InvalidOperationException($"The {session.Script} frame script of the {packName} pack is empty.");
            return new AdapterCaptureSession(frames, session.TickerPrice);
        }).ToList();

        var restRoutes = document.CannedRest.ToDictionary(
            route => route.Key,
            route => File.ReadAllText(Path.Combine(packDirectory, route.Value)));

        return New(document, sessions, restRoutes, packName);
    }

    #endregion

    #region Methods: Validation

    private static AdapterCapturePack New(
        CapturePackDocument document,
        IReadOnlyList<AdapterCaptureSession> sessions,
        IReadOnlyDictionary<string, string> restRoutes,
        string packName)
    {
        AssertMode(document, packName);
        AssertDepthProfile(document, packName);
        AssertTranscript(document, sessions, packName);

        var unknownGuards = document.ClientSideGuards
            .Where(guard => guard is not (GuardUnknownSymbol or GuardBarsLimit or GuardBlankEndpoints))
            .ToList();
        if (unknownGuards.Count > 0)
        {
            throw new InvalidOperationException(
                $"The {packName} pack declares unknown guards: {string.Join(", ", unknownGuards)}.");
        }

        var symbols = document.Symbols.Select(Symbol.Parse).ToList();

        return new AdapterCapturePack(
            document.AdapterId,
            document.AdapterName,
            ParseCapabilities(document.Capabilities, packName),
            document.ConnectsAtConstruction,
            symbols,
            Symbol.Parse(document.StrangerSymbol),
            document.ClientSideGuards,
            MapDepthProfile(document.DepthProfile, packName),
            document.ServedBookDepths is null ? null : new HashSet<int>(document.ServedBookDepths),
            document.ClampedServedDepth,
            document.DepthProbeRequests,
            document.RejectedDepthRequest,
            document.DelegatedServedDepths is null ? null : new HashSet<int>(document.DelegatedServedDepths),
            document.BookTopicTemplate,
            document.BookTopicPrefix,
            document.SubscribeMarker,
            MapReconnectMode(document.ReconnectMode, packName),
            sessions,
            restRoutes);
    }

    private static void AssertMode(CapturePackDocument document, string packName)
    {
        if (document.ReconnectMode is not (AlwaysOn or Transcript))
        {
            throw new InvalidOperationException(
                $"The {packName} pack declares '{document.ReconnectMode}' reconnect: neither is a kit mode.");
        }

        if (document.Symbols.Length == 0)
        {
            throw new InvalidOperationException(
                $"The {packName} pack must enumerate the captured symbols its universe legs assert.");
        }
    }

    private static void AssertDepthProfile(CapturePackDocument document, string packName)
    {
        if (document.DepthProfile is not (ExplicitSet or Clamped or Delegated))
        {
            throw new InvalidOperationException(
                $"The {packName} pack declares '{document.DepthProfile}' depth profile: none of them is a kit profile.");
        }

        if (document.DepthProfile == ExplicitSet &&
            (document.ServedBookDepths is null || document.ServedBookDepths.Length == 0))
        {
            throw new InvalidOperationException($"The explicit-set {packName} pack must enumerate its served book depths.");
        }

        if (document.DepthProfile == Clamped && document.ClampedServedDepth is not int)
        {
            throw new InvalidOperationException($"The clamped {packName} pack must declare its clamp anchor.");
        }

        if (document.DepthProfile == Clamped && document.ServedBookDepths is not null)
        {
            throw new InvalidOperationException($"The clamped {packName} pack must not enumerate a served set.");
        }

        if (document.DepthProfile == Delegated &&
            (document.DelegatedServedDepths is null || document.DelegatedServedDepths.Length == 0))
        {
            throw new InvalidOperationException($"The delegated {packName} pack must enumerate its delegate's served book depths.");
        }

        if (document.DepthProfile == Delegated && document.ServedBookDepths is not null)
        {
            throw new InvalidOperationException(
                $"The delegated {packName} pack must not enumerate an adapter-surface served set (the shell carries none).");
        }

        if (document.DepthProfile == Delegated && document.ClampedServedDepth is not null)
        {
            throw new InvalidOperationException($"The delegated {packName} pack must not declare a clamp anchor.");
        }
    }

    private static void AssertTranscript(
        CapturePackDocument document,
        IReadOnlyList<AdapterCaptureSession> sessions,
        string packName)
    {
        if (document.ReconnectMode == Transcript &&
            (sessions.Count == 0 || string.IsNullOrEmpty(document.SubscribeMarker) ||
             (string.IsNullOrEmpty(document.BookTopicPrefix) && string.IsNullOrEmpty(document.BookTopicTemplate))))
        {
            throw new InvalidOperationException(
                $"The transcript {packName} pack must record at least one session and declare its book topic text.");
        }

        if (document.ReconnectMode == AlwaysOn && sessions.Count > 0)
        {
            throw new InvalidOperationException($"The always-on {packName} pack must not record sessions.");
        }
    }

    private static ExchangeCapabilities ParseCapabilities(string text, string packName) =>
        Enum.TryParse<ExchangeCapabilities>(
            text.Replace(CapabilitySeparator, ",", StringComparison.Ordinal),
            ignoreCase: true,
            out var parsed)
            && parsed is not ExchangeCapabilities.None
            ? parsed
            : throw new InvalidOperationException($"The {packName} pack carries unparsable capabilities: '{text}'.");

    private static CaptureDepthProfile MapDepthProfile(string profile, string packName) => profile switch
    {
        ExplicitSet => CaptureDepthProfile.ExplicitSet,
        Clamped => CaptureDepthProfile.Clamped,
        Delegated => CaptureDepthProfile.Delegated,
        _ => throw new InvalidOperationException($"The {packName} pack carries an unparsable depth profile '{profile}'."),
    };

    private static CaptureReconnectMode MapReconnectMode(string mode, string packName) => mode switch
    {
        AlwaysOn => CaptureReconnectMode.AlwaysOn,
        Transcript => CaptureReconnectMode.Transcript,
        _ => throw new InvalidOperationException($"The {packName} pack carries an unparsable reconnect mode '{mode}'."),
    };

    #endregion
}

/// <summary>One recorded wire session of a transcript pack.</summary>
/// <param name="Frames">The raw frames exactly as captured, in arrival order.</param>
/// <param name="TickerPrice">The last price the session's ticker frame carries.</param>
public sealed record AdapterCaptureSession(
    IReadOnlyList<string> Frames,
    decimal TickerPrice);

/// <summary>The order book depth contract declaration of a capture pack.</summary>
public enum CaptureDepthProfile
{
    /// <summary>An explicit served set; outside the set the request is rejected, never defaulted.</summary>
    ExplicitSet,

    /// <summary>Any depth is accepted and shaped to the adapter's capacity.</summary>
    Clamped,

    /// <summary>The book face delegates to another adapter; the delegate's served set is the declaration.</summary>
    Delegated,
}

/// <summary>The reconnect semantics declaration of a capture pack.</summary>
public enum CaptureReconnectMode
{
    /// <summary>The stream surface never drops, so nothing needs a reconnect protocol.</summary>
    AlwaysOn,

    /// <summary>The adapter reconnects on its own and re-serves its tracked topics.</summary>
    Transcript,
}
