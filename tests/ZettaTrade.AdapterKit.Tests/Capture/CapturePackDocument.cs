namespace ZettaTrade.AdapterKit.Tests.Capture;

/// <summary>
/// The JSON face of an adapter capture pack (<c>Pack.json</c>): every fact
/// the kit asserts is declared here, so the battery stays pack-generic and a
/// pack is a complete, honest recipe for one adapter. Optional members are
/// meaningful: their absence is as much a declaration as their presence
/// (a missing served-depth set declares a serve-anything profile, an empty
/// canned-REST map declares a REST-less adapter).
/// </summary>
public sealed record CapturePackDocument
{
    /// <summary>Exchange identifier the adapter reports (<see cref="ZettaTrade.Core.Interfaces.IExchangeAdapter.Id"/>).</summary>
    public required string AdapterId { get; init; }

    /// <summary>Display name the adapter reports.</summary>
    public required string AdapterName { get; init; }

    /// <summary>
    /// Capability flags, pipe-separated enum names (e.g.
    /// <c>"MarketData | OrderBook | HistoricalData"</c>).
    /// </summary>
    public required string Capabilities { get; init; }

    /// <summary>Whether the adapter reports itself connected the moment it is constructed.</summary>
    public required bool ConnectsAtConstruction { get; init; }

    /// <summary>The closed catalog's captured symbols, in the order the adapter serves them (closed-catalog packs only).</summary>
    public string[] Symbols { get; init; } = [];

    /// <summary>A symbol outside the adapter's universe (the rejected-not-default probe).</summary>
    public required string StrangerSymbol { get; init; }

    /// <summary>The client-side guards the adapter owns; every named guard is asserted by <see cref="Battery.AdapterConformanceBattery"/>.</summary>
    public required string[] ClientSideGuards { get; init; }

    /// <summary>Depth contract shape: <see cref="CapturePack.ExplicitSet"/> or <see cref="CapturePack.Clamped"/>.</summary>
    public required string DepthProfile { get; init; }

    /// <summary>The order book depths the adapter declares it serves (explicit-set packs only; absent = serve anything).</summary>
    public int[]? ServedBookDepths { get; init; }

    /// <summary>The level count a clamped-profile adapter serves per side at most (clamped packs only).</summary>
    public int? ClampedServedDepth { get; init; }

    /// <summary>The raw depth requests the resolution leg probes through the T-072 resolver.</summary>
    public required int[] DepthProbeRequests { get; init; }

    /// <summary>A raw, unresolvable depth request the kit attaches to prove the rejection (explicit-set packs only).</summary>
    public int? RejectedDepthRequest { get; init; }

    /// <summary>The wire prefix of the adapter's book topics (transcript packs only: transcripts ride this pack's dialect).</summary>
    public string BookTopicPrefix { get; init; } = string.Empty;

    /// <summary>The wire marker that names a subscribe operation in this pack's dialect (transcript packs only).</summary>
    public string SubscribeMarker { get; init; } = string.Empty;

    /// <summary>Reconnect semantics: <see cref="CapturePack.AlwaysOn"/> or <see cref="CapturePack.Transcript"/>.</summary>
    public required string ReconnectMode { get; init; }

    /// <summary>The recorded sessions the restore leg replays, in session order (transcript packs only).</summary>
    public CaptureSessionDocument[] TranscriptSessions { get; init; } = [];

    /// <summary>REST path fragment → captured response file; unmapped paths answer 404 (transcript packs only).</summary>
    public Dictionary<string, string> CannedRest { get; init; } = [];
}

/// <summary>One recorded session of a transcript pack: the raw frame script and the session's expected ticker price.</summary>
public sealed record CaptureSessionDocument
{
    /// <summary>The raw frame array file inside the pack directory.</summary>
    public required string Script { get; init; }

    /// <summary>The last price the session's ticker frame carries (what the restored feed must surface).</summary>
    public required decimal TickerPrice { get; init; }
}
