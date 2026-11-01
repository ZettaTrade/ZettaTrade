# Changelog

All notable changes to ZettaTrade are documented here, newest first
(Keep a Changelog format).

While the app is in alpha, one release == one merged board task: task
`T-0NN` ships as `0.1.0-alpha.NN` and adds its section below. The state
before `alpha.10` was never versioned — what shipped in T-001 – T-009
(app shell, themes + hot reload, settings, hotkeys, resx sync gate, order
dialog, positions panel) is recorded in `docs/TASKS.md`.

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
