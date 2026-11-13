# ZettaTrade Adapter Conformance Kit

The standing recipe every exchange adapter must clear before it is an
adapter in this repo. The kit is a self-contained test project — nothing in
`src/` depends on it, nothing in the other test projects is shared — so the
adapter teams never touch anyone else's suite.

## The battery

`Battery/AdapterConformanceBattery.cs` is the abstract battery, eight legs,
each one a sentence of the shared `IMarketDataAdapter` contract:

| Leg | Contract |
| --- | --- |
| `AdapterIdentity_MatchesThePackDeclaration` | `Id`, `Name`, `Capabilities` and the construction-time connectedness equal the pack's captured values. |
| `ConnectAsync_MarksTheAdapterConnected` | A connect flips the adapter connected; on a transport-backed pack the rig sees exactly one session. |
| `TheDeclaredGuards_RejectInsteadOfDefaulting` | Every guard the pack declares holds on the face that owns it (unknown symbols, bars limit, blank endpoints) — invalid input raises, nothing is substituted. |
| `TheCapturedUniverse_ListsExactlyThePackSymbols` | `GetSymbolsAsync` serves the pack's captured list, in the captured order. |
| `TheDepthProfile_MatchesTheAdapterSurface` | An explicit-set adapter surfaces its served depth set (`ServedBookDepths`); a clamped adapter carries no set and the pack is honest about it. |
| `UnsupportedDepths_FollowThePackDeclaredContract` | Explicit-set: an out-of-set request errors per subscription, never starts a stream, never silently serves another depth (BUG-032). Clamped: the served level count is the pack's clamp shape, deterministic. |
| `DepthRequests_ResolveThroughTheT072Resolver` | Every request resolves exactly through `ExchangeAdapterBase.ResolveBookDepth` (the T-072 resolver): the resolved depth is what lands on the wire, the raw unresolvable request never rides it, a no-declaration profile passes through. |
| `SubscribedFeeds_RestoreDataAcrossTheReconnect` | Ticker, book and trade feeds take one event per session across the pack's reconnect semantics; the transcript story joins a fresh subscriber (shared subject), the always-on story survives the adapter's own connect cycle; session count stays pinned (no churn). |

A battery run is `leg × pack`: the kit currently proves the **demo**, the
**Bybit** spot face, the **BybitLinear** perpetual face, the **OKX** face,
the **BinanceTrading** trading shell (delegated) and the **Alpaca**
US-equity face (48 conformance verdicts) and grows for free with every new
capture pack.

## The capture pack

Every pack is a folder under `Captures/`: a `Pack.json` recipe plus the
capture files it names. The pack is a complete, honest declaration of what
the kit asserts — the battery refuses to guess:

- `adapterId` / `adapterName` / `capabilities` — the identity the pack stands for.
- `connectsAtConstruction` — the connectedness the adapter reports the moment it is built.
- `symbols` — the captured universe, in serve order; the first entry is the kit's probe symbol.
- `strangerSymbol` — the outside-the-universe symbol the guards reject.
- `clientSideGuards` — the guards the adapter owns (`unknown-symbol`,
  `bars-limit`, `blank-endpoints`); a guard the adapter does not own stays
  out of the list, so the kit never blesses or punishes behavior the pack
  did not claim.
- `depthProfile` — `explicit-set` (with `servedBookDepths` and the
  `rejectedDepthRequest` the rejection leg attaches) or `clamped` (with the
  `clampedServedDepth` anchor); `depthProbeRequests` are the raw requests
  the T-072 resolution leg probes.
- `bookTopicPrefix` / `bookTopicTemplate` / `subscribeMarker` — the wire
  dialect facts the transcript legs need. A template carries the
  `{symbol}`, `{symbolLower}` and `{depth}` placeholders; a literal
  template (no placeholders, e.g. OKX's depth-free books channel) is
  legitimate, and the battery then skips the third-depth check — such a
  wire cannot carry a third depth.
- `reconnectMode` — `transcript` (recorded sessions, see below) or
  `always-on` (nothing drops).
- `transcriptSessions` — the recorded session files with the expected
  ticker price of each session.
- `cannedRest` — REST path fragment → captured response file, served by the
  kit's offline REST disguise.

The Bybit pack's wire captures (`Session1.json`, `Session2.json`,
`RestCatalog.json`) are real Bybit v5 public-frame shapes, replayed through
the kit's in-process loopback rig — no network, no keys (never real keys:
testnet and canned captures only). The BybitLinear pack (T-197) mirrors
that shape for the linear-perp face: a perp-only universe (the canned spot
catalog answers an empty list), the funding-bearing linear tickers, and a
two-rig session — the linear rig is the session's server, the spot rig an
owned extra. The Alpaca pack (T-198) adds the kit's third wire shape: a
user-keyed venue whose EVERY stream message is a JSON array and whose
stream must authenticate within its window — so the rig carries an
optional **auto-responder** (`KitLoopbackServer.StartAsync`'s responder
argument): the pack scripts the venue's auth ack, and every accepted
session answers the adapter's auth action in-pump, under the session's own
send gate. The user-keyed pair rides the pairs-based `KitSecretStoreStub`
(never a real credential).

## Onboarding a new adapter

1. Capture the offline pack: build `Captures/<YourAdapter>/Pack.json` and
   the files it names. Keep every declaration true — the battery holds the
   pack to its own words.
2. Write `Packs/<YourAdapter>AdapterBattery.cs`: a sealed battery subclass
   implementing `OpenSessionAsync` (the adapter plus its rigs; optionally
   the `RunCtorGuardProbeAsync` override when the pack declares the
   blank-endpoints guard). The ships-tested Demo pack and the transport
   pack (Bybit, via the kit's own `KitLoopbackServer` and canned-REST
   disguise) are the two reference shapes.
3. The gate owns the rest: the kit projects live in `ZettaTrade.slnx`, so
   every `dotnet test` over the solution — including the gate's — runs the
   whole battery against every pack on the board.

The kit does not mock adapters: every leg drives the real adapter stack
(the demo inline, the transcript packs through the real transport) with
recorded capture data. When an adapter breaks the shared contract, the kit
fails the same way it would in production, not the way a mock would cover.
