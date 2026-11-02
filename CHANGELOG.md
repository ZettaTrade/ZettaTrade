# Changelog

All notable changes to ZettaTrade are documented here, newest first
(Keep a Changelog format).

While the app is in alpha, one release == one merged board task: task
`T-0NN` ships as `0.1.0-alpha.NN` and adds its section below. The state
before `alpha.10` was never versioned — what shipped in T-001 – T-009
(app shell, themes + hot reload, settings, hotkeys, resx sync gate, order
dialog, positions panel) is recorded in `docs/TASKS.md`.

## [0.1.0-alpha.16] - 2026-11-02

Build of task T-021 (cold-start polish: the first symbols apply).

### Changed (T-021 — the symbols catalog source)
- The chart's symbol combo filled only after the multi-megabyte
  `exchangeInfo` document finished streaming and parsing (measured
  1.2–3.7 s inside the app; the raw document is ~17.8 MB and took up to
  7.8 s on a real network). The adapter now draws the symbol catalog from
  the compact priced-symbols list (~160 KB): the combo appears within
  ~0.5–0.9 s of the window showing (probe), the first chart bars ride the
  same speedup, and the whole cold start sits well inside the 3 s envelope.
- Symbol names split by a Binance-local quote table (USDT, FDUSD, USD1, the
  recent AED/KZT fiats, the one-letter "U" quote, …) sorted longest-first
  in the adapter; a pair whose quote is unrecognized (52 delisted remnants
  like PAX/UST/IDRT that still carry a price) is dropped and counted once
  in a warning, never guessed.

## [0.1.0-alpha.15] - 2026-11-01

Build of task T-016 (exchange selector in the toolbar + the Demo adapter).

### Added (T-016 — the exchange selector)
- Status bar exchange picker (visible only when two or more adapters are
  registered): picking an entry switches every market data consumer, the
  paper trading adapter and the symbol autocomplete to that exchange
  atomically; the choice persists (`Workspace:ExchangeId`) and is restored
  at the next start (restore runs inside the startup walk).
- `Exchange · Mode` chip next to the picker (`Binance · Paper`): the trading
  mode stays a separate always-visible concern and is never folded into the
  picker (exchange and mode are two different decisions).
- `DemoMarketDataAdapter` — a deterministic canned-data adapter (no network,
  no keys; six fixed symbols, seeded bars per (symbol, time frame) from an
  explicit anchor, cadenced book/ticker/trades snapshots), always registered
  to give the selector a second entry. It will be replaced by a real
  exchange (Bybit/Kraken) in a dedicated adapter task.
- Loud fallback: a persisted exchange id that is not registered anymore is
  warned in the log and announced in the connection chip for ~5 s
  (localized `Status.ExchangeFallback`), instead of a silent substitution.
- resx keys `Status.ExchangePicker` / `Status.PaperMode` /
  `Status.ExchangeFallback` (en + ru).

### Changed (T-016 — the active-exchange facade)
- `ActiveExchangeService` (app-lifetime singleton, `IAsyncInitializable`) is
  now the single registered `IMarketDataAdapter`: everything forwards to the
  active adapter; the behavioral `AdapterChanges` stream re-switches the
  running market data streams (book/ticker/trades), the chart bars trigger
  (`CombineLatest` with `AdapterChanges`) and the connection poll (a switch
  reports the new adapter's state immediately) — a switch is context-wide by
  construction, no consumer can stay on the previous exchange.
- `SymbolListProvider` drops its cache on every exchange switch (the next
  fetch targets the new exchange; a fetch racing a switch returns its data
  but is not cached); the chart re-bootstraps the symbol list per switch and
  keeps the selected symbol while the new exchange still lists it.
- Registration layout: adapters register by concrete type only; the facade
  owns the `IMarketDataAdapter` entry point; paper trading wraps the facade,
  so trading follows the exchange automatically. The exact-one-`exchangeInfo`
  path per run is preserved (the chart bootstrap joins the startup warm-up
  through the provider's single-flight gate).
- Test suite 176 → 207 (Demo adapter determinism + rejection rules, facade
  restore/switch/stream semantics, picker visibility + fallback UX, chart
  switch-and-cancel, provider flush).

## [0.1.0-alpha.14] - 2026-11-01

Build of task T-015 (configurable exchange endpoints + registry seam).

### Added (T-015 — the registry seam)
- `Exchange:Binance` settings section configures the Binance adapter's
  transport endpoints at startup: rest base URL and WebSocket URL (camelCase
  `restBaseUrl`/`wsUrl` in `appsettings.json`). Testnet, regional mirrors and
  proxies no longer need a recompile; a section change takes effect on the
  next app restart.
- `BinanceEndpoints` — immutable value object (`RestBaseUrl`, `WsUrl`) with
  the shipped `Production` default; `ExchangeEndpointRegistry` (app-lifetime
  singleton) reads the section ONCE during the composition root's startup
  walk and serves the pair synchronously to the adapter factory.
- `BinanceAdapter` constructor accepts the endpoints pair: omitted →
  shipped defaults; a provided pair with blank members fails fast
  (`ArgumentException`) — the tolerant missing-entry case belongs to the
  registry, not the adapter.
- Tolerance rules: missing section → shipped production endpoints silently;
  present-but-blank members → warning in the log + production endpoints.

### Changed (T-015 — startup walk ordering)
- The composition root's startup walk now initializes TYPED registrations
  too: the static guard checks `typeof(IAsyncInitializable).IsAssignableFrom`
  instead of a `type is IAsyncInitializable` pattern that never matched a
  `System.Type`. The live probe caught the endpoint registry never
  initializing (the walk skipped it) and the MCP-tools registration
  constructing the whole adapter stack before the settings file was loaded.
- `RegisterAll` (MCP trading tools) moved after the startup walk: resolving
  `ITradingAdapter` constructs the adapter stack, and the Binance adapter
  reads its endpoints from the registry at construction time — the walk must
  have loaded the settings file first, or every adapter silently ran on the
  shipped production hosts.
- Test suite 159 → 169 (10 new `ExchangeEndpointRegistryTests`: walk
  semantics, ctor validation, a startup replica wiring the configured host
  into the resolved adapter, a real-file end-to-end read).

## [0.1.0-alpha.13] - 2026-11-01

Build of task T-014 (instrument autocomplete in the order dialog).

### Added (T-014 — symbol autocomplete)
- Free-form symbol input in the New Order dialog: case-insensitive VM
  suggestions (prefix matches first, adapter order preserved) via the
  `SymbolSuggestions` list; Enter fills the field. Offline (or while the
  list loads) there are no suggestions and no error UI — free text is
  always accepted; a late-arriving list opens the dropdown over already
  typed text.
- `SymbolListProvider` (app-lifetime singleton): normalized tradable
  symbol names cached with one `exchangeInfo` fetch per application run,
  warmed at startup in a non-blocking subscription (cold-start target
  holds); `SemaphoreSlim` single-flight serves concurrent consumers from
  that one fetch; a failed fetch is never cached, so the next consumer
  retries.
- The dialog's symbol-list fetch runs under its command's execution
  token: closing the order dialog cancels an in-flight download
  silently. `SymbolListProvider.GetSymbolsAsync` takes no default token
  — every consumer passes an explicit one; the only
  `CancellationToken.None` left is the deliberate app-lifetime warm-up.

### Changed (T-014 — speed)
- The chart symbols combo comes from the same provider (the startup used
  to run two competing `exchangeInfo` downloads); `ChartViewModel`
  re-parses the cached names through `Symbol.Parse` and keeps the
  50-symbol dropdown trim.
- The named Binance HTTP client decompresses responses (gzip, deflate,
  brotli) — the uncompressed multi-megabyte `exchangeInfo` was the main
  cold-start blocker. Real-Binance probe: chart symbols ready 2.39 s
  after process start (was ~8.4 s+; gzip ≈ 6× smaller), klines response
  317 ms, `Symbol list cached` fires exactly once per run (1373
  instruments).

### Changed
- Test suite 152 -> 159 (9 new: 5 dialog suggestion tests, 3
  `SymbolListProviderTests`, 1 dialog dispose-cancels-the-fetch test; the
  chart tests thread the provider through the same fake adapter).

## [0.1.0-alpha.12] - 2026-11-01

Build of task T-018 (app version chip); the board's T-017 ships in this
release as well (it merged without its own version bump).

### Added (T-018 — version display)
- App version chip in the status bar: the running `0.1.0-alpha.N` is read
  once from the exe's `AssemblyInformationalVersionAttribute` (baked from
  `Directory.Build.props`; the `+<git-commit>` suffix the SDK adds in
  repositories is trimmed) by the `AppVersion` accessor and shown through
  `StatusBarViewModel.AppVersion` in a new status column.

### Added (T-017 — workspace symbol context)
- One symbol context for the workspace: `ChartViewModel` boots in the
  constructor (the dead `WhenActivated` path for nested-panel VMs is gone)
  — a raw `SymbolSelections` stream plus a hot-shared bars machine (`Switch`
  cancels the in-flight load, `Replay(1).RefCount()` keeps one pipeline);
  the order book follows workspace selections by rewriting its typed symbol
  input (the free-typed input stays the offline override, last user action
  wins). Real-Binance probe: the cold-start preselect and the combo switch
  drive both the chart and the book.

## [0.1.0-alpha.11] - 2026-11-01

Build of task T-011 (order book widget).

### Added (T-011 — order book)
- Live order book panel in the workspace: Skia depth widget
  (`OrderBookWidget`, `ICustomDrawOperation` like the chart) driven by a
  self-fed `OrderBookViewModel` (free-typed symbol, default BTC/USDT) over
  `IMarketDataAdapter.SubscribeOrderBook`; bids/asks plan drawn by the pure
  `OrderBookPageLayout` with cumulative-volume bars, spread and localized
  header/columns in en/ru (`OrderBook.*`).
- Demand-driven stream start: the first subscriber of any feed starts the
  adapter-side streams (`ExchangeAdapterBase` virtual
  `StartSymbolStreamsAsync`; Binance connects once, then SUBSCRIBEs the
  symbol's ticker/book/trade streams under a semaphore) — market data no
  longer stays dark because nothing ever called `ConnectAsync`.

### Fixed
- WS transport survived only until the stream start completed (Rx reclaims
  the start token): `WebSocketClient` now opens on the caller's token and
  runs the receive/reconnect loop on an adapter-owned session token.
- SUBSCRIBE payloads lacked the required `id`, and the raw `/ws` endpoint
  did not emit the `{"stream","data"}` envelope the message handler
  expects — both dropped depth frames silently; now serialized with id and
  connected via the combined `/stream` endpoint.

### Changed
- Test suite 119 -> 137 (18 new: 5 `ExchangeAdapterBaseSubscriptionTests`
  contract tests, 5 `OrderBookViewModelTests` feed/switch tests, 8
  `OrderBookPageLayoutTests` including the 60 fps frame-budget measurement:
  avg 1.9 ms / worst 4.8 ms per frame at 1000 levels per side).

## [0.1.0-alpha.10] - 2026-11-01

First versioned state (build of task T-010; app shell and everything
through T-009 is included per the note above).

### Added (T-010 — order history + persistence)
- SQLite order history: every order the trading adapter accepts is
  persisted (`SqliteOrderHistoryStore`, EF Core migrations) by the New
  Order dialog, and cancellations are recorded by the positions panel.
- Modal `Trading > Order History` window reading the journal newest-first;
  rows reuse the localized Order./Positions. labels in en/ru
  (`History.*`), timestamps show the local wall clock.
- The journal is one `Orders` table keyed by (ExchangeId, OrderId) with
  17 order fields; no secrets ever get a column (schema white-list pinned
  by a test). Restart acceptance verified by a UI probe: order placed,
  app restarted, order still in history.

### Changed
- Direct `Microsoft.Extensions.*` package pins bumped to 10.0.14 across
  AI/App/Infrastructure/UI to line up with EF Core 10.0.14 (NU1605).
- Test suite 98 -> 115 (9 store + 8 history VM/routing tests).
