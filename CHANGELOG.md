# Changelog

All notable changes to ZettaTrade are documented here, newest first
(Keep a Changelog format).

While the app is in alpha, one release == one merged board task: task
`T-0NN` ships as `0.1.0-alpha.NN` and adds its section below. The state
before `alpha.10` was never versioned — what shipped in T-001 — T-009
(app shell, themes + hot reload, settings, hotkeys, resx sync gate, order
dialog, positions panel) is recorded in `docs/TASKS.md`.

## [0.1.0-alpha.48] - 2026-11-07

Build of board task T-076 (branch `feat/order-bulk`).

### Added
- Bulk order management (the orders panel): one Cancel All action on the
  open-orders section cancels every working order on the workspace's
  ACTIVE symbol in a single confirmed pass. The pass re-reads the
  adapter's open orders at click time (the display rows are never the
  target set), filters the symbol by its normalized form and commands
  ONLY the still-working statuses - the exact cancel command path every
  row uses, the order's own symbol always carried, no risk bypass, no
  new adapter capability. ONE confirmation gates the pass with the
  symbol, the count, the orders' remaining notional at their own anchor
  prices and the paper/live tag; a refusal leaves every order
  untouched. A row whose per-row pipeline is already in flight (a
  cancel or an amend) is skipped and reported instead of
  double-commanded; one adapter fault produces that item's failed
  report and never stops the others, a persistence fault after an
  accepted cancel can never fail the pass, and an aborted pass still
  renders an honest not-attempted tail. The summary line counts the
  cancelled, skipped, failed and not-attempted items with the failed
  ones' reasons; per-item best-effort reports carry the adapter's own
  order ids and symbols. Fully localized en+ru (`Positions.Bulk*`).

## [0.1.0-alpha.47] - 2026-11-07

Build of board task T-080 (branch `feat/fees`).

### Added
- Per-adapter fee schedules: maker/taker rates in basis points plus a
  minimum fee, keyed by adapter id in the `Fees` settings section. The
  shipped paper model (10/20 bps, no floor) answers out of the box,
  configured entries re-price live through the reactive settings
  change stream, an invalid entry is dropped with a warning, and an
  adapter without a schedule stays the explicit dash — never a zero
  default that hides the cost.
- The order ticket prices an estimate line per leg: the expected
  maker/taker role (a resting limit is maker, everything else
  crosses) names itself per the leg's shape and the notional rides
  the typed price or, for the price-less types, the throttled
  freshest market reference. An incomplete input or an unknown
  schedule keeps the dash — the estimate is display only and never
  touches a placement decision.
- The paper engine stamps the computed commission (half-away-from-zero
  at 8 decimals, lifted to the minimum fee) on every fill and passes
  it into the T-082 PnL ledger through the ledger's per-fill fee
  parameter.
- Fee surfaces: the order history window and the positions panel's
  recent orders show the Fee column (a recorded commission prints
  invariant-formatted like the other money cells, an absence prints
  the dash), and the fills CSV gains a fee column (an absent
  commission exports as an empty cell). Fully localized en+ru.

## [0.1.0-alpha.46] - 2026-11-07

Build of board task T-077 (branch `feat/position-actions`).

### Added
- Position actions on the positions panel rows: Close, Reduce and
  Reverse ride the mandatory order pipeline - request validation,
  `IRiskManager` (both legs of a reverse), then ONE
  `IConfirmationService` confirmation over the composed orders (a
  refusal leaves the book untouched) and only then the trading
  adapter. Every size is computed against the CURRENT position
  re-read at placement time, so an external fill that lands first
  wins and is reported - no stale-sized order, no double entry, no
  silent drop; a reverse whose exit leg leaves the symbol non-flat
  aborts its re-entry with a localized report. Close on an empty
  position and zero/oversized reductions are rejected explicitly;
  demo/paper market legs fill against the freshest reference close
  the `MarketReference` seam resolves and the in-flight rows are
  disabled (a repeated click is a no-op). Fully localized en+ru.
## [0.1.0-alpha.45] - 2026-11-06

Build of board task T-083 (branch `feat/trade-stats`).

### Added
- Trade statistics dashboard (Trading → «Trade Statistics...»). A
  read-only scan of the persisted order journal builds the stats from raw
  fills only: every closing fill counts as its own trade against the
  average-entry bookkeeping — win rate, profit factor («∞» when no
  losing fills, «—» when nothing to divide), maximum drawdown over the
  equity curve including its zero-seeded start, average winner and
  average loser, trade count and filled volume — all shown as metric
  tiles over a Skia equity curve (zero anchor at the first fill, one
  point per closing fill). Filters (symbol, exchange id, venue
  all/paper/live, inclusive-from / exclusive-to dates) restart a paged
  keyset scan ((CreatedAt, ExchangeId, OrderId) continuation) that
  refines the result progressively; the read query lives in
  Infrastructure Persistence with no write path and the aggregation math
  in the App accumulator service. Fully localized en+ru.

## [0.1.0-alpha.44] - 2026-11-06

Build of board task T-075 (branch `feat/order-amend`).

### Added
- Amend a working order from the orders panel: every working, amendable row (a limit, a stop-limit or a trailing stop; market, stop-market and OCO legs are refused explicitly) carries an Amend button that opens an edit dialog over the panel — quantity, plus the limit price for the limit-bearing types (a trailing stop's price is its ratcheting trigger, never editable). The amend is a full pipeline, not a field rewrite: the adapter's fresh open-order state is re-read (never the stale display row), the amended shape is validated and re-checked through `IRiskManager` (a rejected shape leaves the order untouched), and the confirmation dialog fires when the risk manager demands one, when the order carries any fill (the amend rewrites live exposure), or when the notional moves beyond 25% in either direction. The replace rides the adapter's in-place seam where an adapter offers it; the shipped paper adapter (no such seam) runs an explicit cancel + new order, surfaced to the user as cancel+new — never a silent fake replace. A fill racing the amend wins and is reported («the order is no longer working; the amend was not performed»), the row keeps an «Amending…» state for the whole flow (double-click in flight is a no-op), and a cancel-landed-but-replacement-failed hole is reported loudly. Fully localized en+ru.

## [0.1.0-alpha.43] - 2026-11-06

Build of board task T-082 (branch `feat/pnl-engine`).

### Added
- The headless PnL accounting core (T-082). `PnlAccountingService` keeps
  average-price bookkeeping over the shipped paper/fill ledger (an order
  with a positive `FilledQuantity` carries its execution, sans fee data),
  realizes PnL per closing fill with the position-flip path (a closing
  trade larger than the position re-opens the remainder on the other side
  at the same fill price), prices the unrealized side from ticker feeds,
  exposes state changes over an observable feed — and persists realized
  history to SQLite (EF canon, `AddPnlRealized`) so an application restart
  reconstructs the identical numbers from the persisted rows.
- The App-side `PnlFeedService` wrapper: the engine's feed for future
  panels, ticker pricing through the active-exchange facade, and a slow
  idempotent ledger re-sync (the journal has no change notifications).
- The fee seam, per the task decision: the ledger carries no fee data, so
  commission enters only through an explicit per-fill parameter on the
  domain API (absence ≡ zero; no fee fabricated); T-080 plugs real
  schedule data into it without Core changes.

## [0.1.0-alpha.42] - 2026-11-06

Build of board task T-081 (branch `feat/balances`).

### Added
- The balances panel (Trading menu): one read-only surface next to the
  positions/orders panels showing every active exchange's served balances
  (per-exchange sections plus an aggregated view that sums the amounts per
  asset across the adapters that actually provide balances). The panel
  follows the adapters' honesty contract: an adapter without balance data
  renders the explicit "not provided" state and a faulting query renders
  the localized fault — a zero is never a plausible-but-wrong figure. Data
  rides the shipped trading seam (`ITradingAdapter.GetBalancesAsync`): the
  paper account serves rows live (empty before the first fill), while the
  market-data-only faces (Binance/Bybit/demo) report the not-provided
  state; manual refresh; en+ru localization; aggregation and state-machine
  tests.

## [0.1.0-alpha.41] - 2026-11-06

Build of board task T-084 (branch `feat/csv-export`).

### Added
- CSV export for the app's local data surfaces (order journal, fills, candles): the `ICsvExportService` writer streams RFC 4180-quoted rows with invariant decimals, ISO-8601 UTC timestamps, UTF-8 (no BOM) and CRLF line endings, row by row with cancellation between rows and an honest `DirectoryNotFoundException` on a missing destination — a six-figure history exports with a one-row memory footprint.
- The order history window gains Export Orders CSV and Export Fills CSV actions: the loaded journal rows are the export set, the destination comes through a save-file dialog (cancelled = nothing written, no error), and a write fault surfaces on the window's localized error line.
- The chart panel gains Export Candles CSV: the displayed series is the export set and faults land on the panel's own error line (the status bar stays with connection state); the main window answers the save-file interaction for the app-lifetime display in `OnOpened`.
- Localization: `History.Export*` and `Chart.Export*` keys in both en and ru catalogs.

## [0.1.0-alpha.40] - 2026-11-06

Build of board task T-119 (branch `feat/first-run-disclaimer`).

### Added
- The first-run disclaimer: the first launch shows a modal over the main window holding the shipped wording — «educational software, not investment advice» plus the explicit alpha posture (alpha software, paper-first trading, the own-testing-first real-funds statement, user-scoped key hygiene) — and an «Acknowledge» button persists a versioned acknowledgment flag through the existing settings store, so a future wording update re-shows the dialog exactly once per install. A plain dismissal leaves the flag unset (the next launch re-shows), the flag itself never gates any trading action, and the wording stays discoverable later from Help → About / Disclaimer. Fully localized en+ru (all wording in resx).

## [0.1.0-alpha.39] - 2026-11-06

Build of board task T-074 (branch `feat/order-types`).

### Added
- Advanced order types end-to-end. The order request shapes grow an
  additive `TrailDistance` and `OcoGroupId`, with the stop-limit
  trigger-vs-limit matrix and the trailing-stop parameter requirements in
  `Validate` — an inverted or unparsable shape is a returned error, never
  a default. The paper adapter simulates the conditionals on the wrapped
  adapter's live ticker stream: a stop-limit arms as a working order and
  fills at its limit on the trigger tick; a trailing stop ratchets its
  trigger along the favorable direction and fills at the ratcheted level;
  OCO legs share an either-or group where the first terminal leg takes the
  rest down (a trigger whose fill is refused lands the leg Rejected and
  still tears the pair down; a leg joining a half pair is rejected
  explicitly, and a refused placement leaves no phantom). Immediately
  triggering stops are refused with an explicit error, and the risk hard
  cap values stop orders at their trigger — the notional the order really
  commits to.
- The order ticket carries the conditional inputs: the stop trigger and
  the trailing distance rows, and an OCO mode that places a take-profit
  limit and a stop-loss stop-limit as two legs of a fresh group — each leg
  through the full risk/confirmation gate, a half-placed pair aborted
  best-effort so it never goes half-working. The trailing seed derives
  from the newest reference close (the shared newest-by-bar-time
  selection), and stop triggers and take-profit levels are
  market-direction checked with localized refusals. Resx grows to 167 keys
  on both en and ru.

## [0.1.0-alpha.38] - 2026-11-06

Build of board task T-073 (branch `feat/flaky-localization`).

### Fixed
- The flaky localization parking fact (`GetString_AFactoryParkedAcrossASwitch`, captured twice during the T-071/T-072 landing verification): the race lived in the test harness, not the manager — a class-wide "any lookup parked" witness could be opened by a foreign lookup resolving the same process-wide `LocalizationManager` instance (the order book layout's `Build` path), so under parallel-suite load the switch committed over a lookup that had never reached its snapshot, and that inconsequential call read the fresh culture and returned it — exactly the manager's documented snapshot-consistency promise. The handshake now waits for the fact's own thread id at a per-name register; the static-resolving `OrderBookPageLayoutTests` join the localization singleton collection; and `LocalizationManager.Dispose` clears the stale `Instance` pointer, so a torn-down manager stops claiming process-wide initialization (the same leak let parallel `Build` facts resolve a stamping fake's template — the captured `"en:OrderBook.SpreadFormat"` spread text).

## [0.1.0-alpha.37] - 2026-11-06

Build of board task T-072 (branch `feat/order-book-depth`).

### Fixed
- The order book widget renders live data again on depth-locked exchanges:
  Bybit serves `orderbook.1`/`orderbook.50` only and the BUG-032 guard
  rejected the interface-default request of 20 levels as a contract error,
  emptying the widget on a stream failure (the rejections stood in the daily
  logs since 2026-10-18). The facade now resolves the requested depth
  against the active adapter's served set — the smallest deep-enough depth
  wins, else the deepest served one; undeclared adapters pass through; the
  adapter-side rejection contract stays untouched.

## [0.1.0-alpha.36] - 2026-11-06

Build of board task T-071 (branch `feat/window-chrome-black`).

### Fixed
- The chrome windows rendered a black void where content belongs: Avalonia 12
  presents template content only through explicit bindings and the fresh
  `Window.chrome` templates never bound `PART_ContentPresenter` — `Content`,
  `ContentTemplate`, the `Padding` margin and both alignments ride it now in
  both chrome templates, regression-tested.

### Changed
- Caption strip polish: the title centers on the strip at 13px; the caption
  glyphs center in their 44×32 buttons at a 1.5 stroke; the hover feedback
  rides the icon itself (button opacity states, the Fluent presenter fill
  suppressed), and the close glyph retints to the palette error color.

## [0.1.0-alpha.35] - 2026-11-06

Build of board task T-069 (branch `feat/appearance-contracts`).

### Changed
- The appearance contracts leave `ZettaTrade.Core.Interfaces` for Livery: `IThemeManager` + `ThemeInfo` now live in `Livery.Themes`, `ILocalizationManager` + `LanguageInfo` in `Livery.Localization` and `IAsyncInitializable` in `Livery.Contracts` — the appearance library references nothing ZettaTrade-branded and can package standalone for its second consumer, the `App`/`AI`/`Infrastructure` consumers rehome their usings (AI and Infrastructure gain the Livery project reference), the settings window's XAML datatypes switch to the library prefixes, and the move ships as a clean flip with no backward-compat shims.

## [0.1.0-alpha.34] - 2026-11-06

Build of board task T-068 (branch `feat/mcp-client-settings`).

### Added
- External MCP servers in the settings' Mcp tab: per-entry editing (name, transport Stdio/Streamable HTTP, command+args for stdio, address+port for HTTP, the enabled flag, an auth secret REFERENCE key that resolves through the secret store, and the optional HTTP timeout seconds) persisted under the new `McpClients` settings section, with per-entry honest state rows (Disabled/Connecting/Connected/Failed) fed by the new `McpClientManager` — enabled entries connect on startup and after each save through the same `McpClientService.ConnectAsync` path, unchanged saves keep live connections, removed/changed/disabled entries disconnect, outages pin Failed with the transport's own reason instead of hanging anything, and unparsable persisted rows are skipped loudly.

## [0.1.0-alpha.33] - 2026-11-06

Build of board task T-067 (branch `feat/indicator-settings-gate`).

### Added
- The Indicators settings tab reflects the real plugin catalog: the SMA
  overlay row renders only when the scanned catalog registered the plugin
  (the new `IIndicatorCatalog.LoadedNames` snapshot drives the gate), and a
  catalog with zero indicators shows the honest localized empty state
  naming the app data directory's `plugins` folder.

### Fixed
- A settings save without the loaded plugin never fabricates its config
  row: the indicator block of the save runs only when the session actually
  owns the plugin — the persisted `ChartIndicators` config rides untouched
  (the chart pipeline already skips unknown names on its own seam).

## [0.1.0-alpha.32] - 2026-11-06

Build of board task T-066 (branch `feat/window-chrome`).

### Added
- Styled window chrome across the shell (all six windows): the `Livery`
  `TitleBar` strip — localized title, drag, double-click maximize,
  minimize/maximize/restore/close — mounts through the `Window.chrome` /
  `Window.chrome-compact` templates on Avalonia 12 `BorderOnly` decorations,
  so the native resize borders and AeroSnap survive while the system caption
  disappears; the strip's window-wide key hook restores the Alt+F4 close
  gesture that rode the dropped system menu.
- Shell controls restyled off the palette (buttons, text inputs, combos,
  tabs, menu with hover/pressed/focus states): generic hover keys onto
  Accent, the close button onto Error.

### Changed
- New palette keys `TitleBar` / `TitleBarText` ship in every built-in theme
  (dark, light, midnight, oled, trading-pro), the generated sample theme and
  the theme-key list in `ZettaTrade_SKILL.md`; the chrome retints live on
  theme and language switches.
- The resx catalog gains the `Chrome.*` labels (en + ru) and window
  backgrounds ride the `Background` palette key instead of the default
  Fluent token; the main window title localizes.

## [0.1.0-alpha.31] - 2026-11-06

Build of board task T-059 (branch `feat/theme-hot-reload`).

### Fixed
- BUG-046: the theme hot-reload debounces PER FILE, so a burst saving
  several theme files inside one debounce window reloads every one of
  them (before: only the last-changed file reloaded); a watcher error —
  the internal-buffer-overflow case that permanently stops the current
  watcher's raise loop — logs a warning and re-subscribes a fresh
  `FileSystemWatcher` instead of silently dying.
- BUG-047: `ThemeManager.AvailableThemes` returns an immutable snapshot
  taken under the load lock instead of the live list the reload pipeline
  mutates — no more `InvalidOperationException` racing a consumer that
  enumerates it.
- BUG-050: the order book widget re-renders on theme and language
  changes (the chart's convention), so its colors and column headers
  follow a switch live instead of staling until the next book snapshot.

## [0.1.0-alpha.30] - 2026-11-06

Build of board task T-058 (branch `feat/ui-primitives`).

### Fixed
- BUG-025: the async subscription helper `SubscribeAsync` runs its
  per-notification tasks one at a time in emission order (`Concat`
  instead of `Merge`) — dependent steps (serialized page fetches) no
  longer interleave while an earlier run pends; a queued run starts the
  moment its predecessor completes.
- BUG-048: the chart pan step paces by the pitch the draw pass actually
  painted with (plot width minus the measured price gutter, cached from
  the last draw pass) instead of re-deriving it from the raw window
  bounds — dragging one bar's width moves the window exactly one slot.
- BUG-049: the `SKPath` detached for each indicator line disposes in a
  `using` scope right after the `DrawPath` call — no native wrapper
  parked for the finalizer on every redraw.

## [0.1.0-alpha.29] - 2026-11-05

Build of board task T-070 (branch `feat/livery-resx`).

### Changed
- The localized Strings catalog (`Strings.resx` / `Strings.ru.resx`
  (+ the generated `Strings.Designer.cs`)) leaves Livery and returns to
  `ZettaTrade.UI`: the strings are a per-application artifact, while the
  library only consumes a `ResourceManager` the host supplies. The
  composition root binds `ZettaTrade.UI.Resources.Strings.ResourceManager`
  into `LocalizationManager`; the charting widgets read the catalog
  directly again; `Livery.dll` now ships no application strings.
- Choreography: the closed merged sections T-063–T-065 archive off the
  board (rule 5 catch-up), and the follow-up is recorded as T-070.

## [0.1.0-alpha.28] - 2026-11-05

Build of board task T-065 (branch `feat/appearance`).

### Changed
- Localization and the theme engine leave `ZettaTrade.UI` for the new
  lean `Livery` library: `LocalizationManager` +
  `LocalizedExtension`, `Strings` resx (namespace
  `Livery.Resources`), `ThemeManager` + `ThemeFileWatcher`
  + `PreviewColorsToBrushesConverter`, and the UI-thread marshaller
  `ObserveOnUiThread` (`Livery.Extensions`). No diagram
  change at runtime: theme hot-reload, live language switch and settings
  persistence behave exactly as before.
- `ThemeManager` takes the user themes directory as a REQUIRED
  constructor argument: the library never reaches into
  `Infrastructure.Configuration` for paths; the composition root passes
  `ConfigPathResolver.ThemesDirectory`, and a blank value is rejected
  loudly.
- `ZettaTrade.UI` keeps charting/primitives and now references Livery
  (plus a direct `ZettaTrade.Charting` reference replacing the dropped
  transitive one); the app consumes both libraries.

## [0.1.0-alpha.27] - 2026-11-05

Build of board task T-056 (branch `fix/app-vm-error-paths`).

### Fixed
- Order placement and positions-panel load/cancel failures land as
  localized error lines in every failure class, including network
  faults, instead of escaping into unobserved command exceptions
  (BUG-017, BUG-054).
- The status bar's persisted-exchange fallback notice and its expiry
  timer marshal onto the Avalonia UI thread instead of writing the
  connection chip from their own threads (BUG-018).
- The AI panel's visibility persistence leads its pipeline with the
  latest value and serializes its writes, so a rapid toggle is neither
  lost nor is one failed write able to kill the pipeline (BUG-053).
- A crashed startup logs the error through Serilog before the process
  exits (BUG-019).
- The same order renders the same wall-clock time in the positions
  panel and in the order history window (BUG-055).

## [0.1.0-alpha.26] - 2026-11-05

Build of board task T-060 (branch `fix/localization-races`).

### Fixed
- A concurrent language switch can no longer hand a racing caller a
  wrong-culture localized string: `GetString` snapshots the current
  culture once per call for both the cache key and the resource lookup
  (BUG-051).

### Investigated
- The suspected `{loc:Localized}` binding lifetime leak (BUG-052) is
  closed as a false positive with evidence: Avalonia 12.1.4's
  observable bindings subscribe their observers weakly, so an abandoned
  control drops the chain at the next emission. No code change; the
  full framework-source analysis lives in the task section of
  `docs/TASKS.md`.

## [0.1.0-alpha.25] - 2026-11-05

Build of board task T-057 (branch `fix/risk-cap`).

### Fixed
- Price-less orders can no longer bypass `MaxOrderValueHardCapUsd`
  (BUG-026): a stop order is valued against its own trigger price and
  runs the same cap/confirmation matrix, and an order with no estimable
  value is rejected with an explicit reason instead of being silently
  approved for human confirmation only.

## [0.1.0-alpha.24] - 2026-11-05

Build of board task T-062 (branch `fix/xdg-paths`).

### Fixed
- Empty or whitespace-only `XDG_CONFIG_HOME`, `XDG_DATA_HOME` and
  `XDG_STATE_HOME` values fall back to the platform defaults instead of
  silently resolving CWD-relative paths (BUG-044); a non-empty value
  still wins as before.

## [0.1.0-alpha.23] - 2026-11-05

Build of board task T-061 (branch `fix/order-history-store`).

### Fixed
- The persistent order journal became a true database upsert: concurrent
  placements of the same order insert-or-update atomically instead of
  failing on the unique constraint (BUG-043).
- Terminal statuses are final: a late or duplicate cancel for an order
  already Filled/Cancelled/Rejected/Expired is logged and ignored,
  never rewriting the recorded outcome (BUG-042).

## [0.1.0-alpha.22] - 2026-11-05

Build of board task T-055 (branch `fix/chart-vm-restore`).

### Fixed
- The persisted 12-hour clock setting applies in every restore path of
  the chart view model, including no stored time frame, an unparsable
  stored value and the common default-`H1` restore (BUG-015).
- A stale symbols bootstrap delivery can no longer overwrite a newer
  exchange's symbol list or an explicit user pick: an epoch per fetch
  drops superseded deliveries, and a pick made mid-fetch defers the
  persisted preference (BUG-016).

## [0.1.0-alpha.21] - 2026-11-04

Build of the ui-colors refactor (supervisor-direct task; branch `feat/ui-colors`).

### Changed
- The XAML carries no color literals anymore: the favorite stars consume
  the new `Highlight` theme key, the three error texts (`ErrorMessage`)
  the new `Error` key, the chat timestamps the existing
  `TextSecondary` key.
- The `Highlight` and `Error` keys ship in every theme file
  (`dark`, `light`, `midnight`, `oled`, the repo example `trading-pro`)
  and into the sample custom theme; the theme-key docs lists follow.

### Removed
- HTML entities from source XML docs; the Skia fallback colors became
  named `static readonly` constants (`CandlestickChart`,
  `OrderBookWidget`). No behavior change.

## [0.1.0-alpha.20] - 2026-11-04

Build of task T-039 (symbol favorites + hidden pairs).

### Added (T-039 — symbol marks)
- The symbols combo gained per-symbol marks persisted per workspace exchange
  (`Workspace.FavoriteSymbols` / `Workspace.HiddenSymbols` JSON maps): a
  favorite renders a star badge in every row and pins to the head of the
  list; hidden pairs leave the everyday list.
- The toolbar next to the symbol combo: a star toggle (favorite/unfavorite
  the selected symbol). Hidden pairs never take the chart (the review round
  decided the rebase — below).
- The marks fail safe: a malformed stored map drops to no marks instead of
  crashing the picker, and the bootstrap never rewrites the maps it reads.

### Changed (T-039 review round — the favorite stars move into the pairs tab)
- The star leaves the chart toolbar: the "Pairs" tab's all-pairs list carries
  a star button on every row (next to the name). Pressing it stars the pair
  in the draft: the row pins to the list head at once; the toolbar star
  toggle is gone.
- A starred pair is never hidden: the marks are mutually exclusive by rule,
  so the Hide move refuses a starred pick (the `>` button grays out) and a
  starred row never shows up in the hidden list. A stored document may still
  hold one name in both maps (earlier releases' shape freedom): the settings
  render the favorite and drop the hidden view for it, and the next save
  repairs the document (the star wins until the user unstars).
- The star joins the draft: starring lands in the window's lists at once and
  the map applies and persists only on Save (the same contract the moves
  follow); the chart combo re-applies the lands live through the settings
  changes stream.
- The `★` badge in the combo picker rows and the rebase/keep-one rules stay
  as they were; `Chart.Symbols.FavoriteTip` is gone (en + ru),
  `Settings.Pairs.Star` names the row buttons.

### Changed (T-039 review round — hidden pairs move to Settings)
- The hidden pairs are managed in the settings window's new "Pairs" tab: two
  lists — all pairs of the workspace exchange and the hidden ones. The move
  buttons sit in the center column between the lists, the classic two-list
  dialog shape: `>` hides the picked pair, `<` brings it back (the former
  bottom row of full buttons is gone). A pick plus a move repaints the
  draft lists and carries the pick to the pair that took the moved row's
  slot (the next row heading up, the tail row at the list end); the
  document is written only by the window's Save, and the changes stream
  re-applies the marks in the open chart live.
- The last visible pair can never hide: the Hide move disarms when one
  visible pair remains, so the workspace always keeps a tradable context.
- The settings save that hides the pair whose chart is shown switches the
  chart to the first visible pair and persists the switch — the hidden
  pair never holds the selection (the round-1 invisible pinned row is
  gone), and a restart never re-opens a hidden chart either. A hidden pair
  leaves the picker unconditionally; the tab's reveal is its only way back.
- The toolbar Hide and Show hidden toggles are removed (their job moved to
  the tab); the favorite star stays. `Chart.Symbols.Hide` /
  `ShowHidden` strings are gone (en + ru), the pairs-tab strings are in.
- The marks document is serialized with each exchange's names sorted, so the
  same marks always produce the same bytes.
- The order book header is inset from the divider like the assistant panel's.

### Fixed (T-039 review round)
- The settings window no longer resizes when switching tabs: the dialog is
  pinned to a fixed 560×640, Save/Close ride a pinned bottom row (never
  scrolled out of sight), and the long lists scroll their own viewports
  (the pairs lists, the theme-error list) instead of one dialog-wide
  scrollbar.

## [0.1.0-alpha.19] - 2026-11-02

Build of task T-024 (the demo seeded-walk underflow).

### Fixed (T-024 — demo bar loads overflowed)
- Loading a full chart range of demo bars overflowed: `BarStep` evaluated
  `Next % 2001 - 1000` in unsigned semantics, so ~half the steps wrapped to
  ≈+42 949 % instead of the documented (−1%, +1%) and the compounding prices
  blew past the decimal range with `OverflowException`. The modulo and the
  subtraction now run in `long` semantics (the healthy path is unchanged —
  only the wrapped values were wrong).
- The same unsigned wrap sat in the ticker's 24 h change (`PriceChange24h`):
  four of the six demo symbols permanently displayed a ~+429 496 729 %
  rise. They now carry the documented (−1%, +1%) move.
- Pinned by tests: the full 7 day H1 range loads for every demo symbol with
  every close inside the documented-walk bounds, and every ticker stays in
  the documented band.

## [0.1.0-alpha.18] - 2026-11-02

Build of task T-023 (the second real adapter: Bybit).

### Added (T-023 — the Bybit adapter)
- A second production market data adapter, Bybit v5 spot (read-only
  `IMarketDataAdapter`, transport untouched Trading Safety invariants: no
  keys are read or carried; orders stay paper-only). Measured against
  Kraken's public API the same way T-021 measured REST catalogs: Bybit won
  on payload (282 KB vs 680 KB), single-page catalog and server-served
  `baseCoin`/`quoteCoin`/`status` metadata (no quote-table splitting).
- Symbol catalog from `instruments-info` (`status == "Trading"` pairs,
  cursor-paged and bounded by a five-page cap with ONE aggregated warning;
  malformed entries dropped and counted, never guessed), bars from `kline`
  (newest-first wire order reversed to the adapter contract; all numbers
  arrive as strings and parse to exact decimals), plus live `tickers` (the
  spot payload carries no bid/ask — mapped honestly as zero, no guessed
  substitutes), `orderbook.50` (snapshot + delta state machine with
  monotonic `u`, quantity `"0"` deletes a level, a detected gap discards
  the state and re-subscribes the topic) and `publicTrade` (aggressor side).
- One shared WebSocket with a single multiplexed subscribe frame per
  symbol, re-subscription of every tracked symbol after a reconnect, and an
  adapter-owned 20 s keepalive ping (the infra has no app-level ping).
- Endpoints live in the T-015 settings seam (`Exchange:Bybit`, same
  override semantics as Binance), the adapter joins the T-016 picker
  between Binance and Demo, and the cold-start default stays preserved
  (probe: `Binance | Bybit | Demo`, Demo persisted choice survives).

## [0.1.0-alpha.17] - 2026-11-02

Build of task T-022 (the order dialog names its exchange).

### Added (T-022 — the order dialog header names the active exchange)
- The order dialog opens context-free: the toolbar selector can switch the
  exchange at any moment, and nothing in the dialog said which exchange the
  symbol suggestions and reference prices ride. A header line now shows the
  localized title plus the live active-exchange name (e.g. `New order —
  Binance Spot`), bootstrapped on open and flipped by toolbar switches
  while the dialog stays open.

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
