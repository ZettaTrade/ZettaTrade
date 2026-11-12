## [0.1.0-alpha.139] - 2026-11-13

Build of board task T-195 (branch `feat/screener`): the market screener
panel — a sortable, filterable table over the active adapter's ticker
streams.

### Added
- The `ScreenerFeedService`: the adapter's tradable universe fetched once
  through the active-exchange facade and capped to a bounded candidate
  window (64 subscriptions, the heatmap feed's discipline), one ticker
  pump per candidate, and the candidates' latest tick state coalesced
  into ONE table page per 500 ms pulse — a wire ticker storm stays inside
  the service while the panel sees one page. An exchange switch re-fetches
  the universe (the `AdapterChanges` kick with the ticket-based
  `Observable.FromAsync` + epoch guard) and drops the previous exchange's
  ticker state in full. No extra REST polling: the feed rides the shared
  catalog and ticker streams within the existing limiter budget.
- Honest page states (`ScreenerSnapshot`): live rows, the armed-but-unpriced
  waiting state, and a missing-or-failed catalog state whose reason line
  replaces the table — an adapter without a catalog degrades honestly,
  never a broken table.
- The `ScreenerViewModel` + `ScreenerWindow` (Trading menu → "Screener"):
  the preset views (all / top gainers / top losers / top volume), the
  manual column sorts (price, 24h change, 24h volume) with a direction
  flip and an ordinal tie-break, and a row click that routes the
  workspace symbol through the chart's apply path (the heatmap tile
  route's precedent — an unparsable row text is refused, never
  substituted). A preset or sort flip rebuilds the rows immediately from
  the held page. Row formatting reuses the shared watchlist helpers; the
  change column tints by the 24h sign (T-159, the candle state colors).
- The menu item rides the Trading menu after Order History. The stray
  heatmap debug log line (`PROBE comp finally released`) left the feed's
  refresh path.

### Tests
- The screener feed's behavioral pins: the window caps at the budget in
  the adapter's own order, a tick surfaces on the next pulse with its raw
  numbers, the rows keep the adapter's order, unticked pairs own no row,
  an unpriced window ships the waiting state, a faulting (or empty)
  fetch ships the catalog-unavailable state, a refresh swap drops the
  previous state in full, and a stale-epoch composition publishes
  nothing — every wait delivery-ordered via an already-subscribed
  promise (the T-135 lesson), no wall-clock budgets.
- The panel ViewModel's pins: a page and its state move as one, the
  presets filter and sort (gainers keep the positive change descending,
  losers the negative one ascending, volume sorts descending), a column
  press retires the preset to All and flips direction on a repeat press,
  the sort rebuilds from the held page immediately, and the row route
  writes the workspace symbol (or refuses an unparsable text without a
  substitute).
- The composition pins: the feed and its panel ride factory singletons in
  that order, both stay startup-walk candidates. The menu routes through
  its interaction; the numeric-surface token pin adopts the new table's
  three numeric columns.

## [0.1.0-alpha.138] - 2026-11-13

Build of board task T-194 (branch `feat/okx-adapter`): the OKX spot
market data adapter — a third live venue beside Binance and Bybit.

### Added
- The `OkxAdapter`: the OKX v5 public market-data surface on the
  existing transport stack. One multiplexed WebSocket subscribes the
  three channel args per symbol (tickers, books, trades) in a single
  frame; the REST catalog (`/api/v5/public/instruments`, SPOT, one
  page, `state == "live"` filter) and the candles endpoint
  (`/api/v5/market/candles`, newest-first all-string rows, confirmed
  rows only, page walk back through `after=` with the venue's 300-row
  cap and the shipped five-page bound) ride the same named-client +
  rate-limiter discipline. The wire instId (`BTC-USDT`) is mapped
  adapter-locally; Core's Symbol table stays untouched.
- The depth-free `books` channel: OKX maintains one continuously-updated
  book (snapshot-then-update with a monotonic `seq`, quantity `0`
  deletes a level) and carries no depth parameter on the wire — the
  adapter serves the venue-truthful explicit set `{400}`: deeper
  requests reject, shallower resolve up through the T-072 resolver.
  The sequenced book assembler is renamed venue-neutral
  (`SequencedOrderBookAssembler`) and serves both venues.
- The 24 h change is computed from the venue's own last/open24h with a
  zero-open guard; tickers carry the top-of-book (`bidPx`/`askPx`).
- Control frames (`event` acks and errors), the venue's 30 s literal
  pings (answered with the literal `pong`) and the adapter-owned 25 s
  keepalive follow the Bybit discipline.
- The toolbar's venue switcher, the palette and the watchlist pick the
  third venue up with no special-casing: the facade catalog gains the
  OKX entry, the trading-mode lane resolves it to paper automatically,
  and the endpoint pair (`Exchange/OKX`) joins the settings port.

### Tests
- The adapter-kit battery rides a new OKX pack (canned REST catalog +
  two transcript sessions): 8/8 legs; the kit suite is 32/32. The
  battery's third-depth check now skips packs whose book-topic text
  encodes no depth (a depth-free wire cannot carry a third depth).
- The Infrastructure suite adds 29 OKX facts: the REST parsing surface
  (19: envelope code gate, newest-first ascending candles, confirmed
  rows only, page walk, page cap, venue page clamp, timeframe grammar,
  limit guard) and the WS e2e family (10: subscribe-args pin,
  snapshot/update book, gap recovery, reconnect re-subscribe, control
  error logging, literal ping/pong, depth contract) over the in-process
  loopback rig.

## [0.1.0-alpha.137] - 2026-11-12

Build of board task T-193 (branch `feat/binance-trading`): the Binance
spot testnet trading adapter — real (testnet) trading on the Binance
venue beside the existing Bybit surface.

### Added
- The `BinanceTradingAdapter`: signed REST order placement/cancel and
  the open-orders, balances and positions queries on the Binance spot
  testnet (HMAC-SHA256 over the exact query string, the signature member
  appended last, the `X-MBX-APIKEY` header carrying the key), and the
  private user-data stream over a listen-key session (issue → dial
  `{PrivateWsUrl}/{listenKey}` → receive-only fills → the 30-minute
  keepalive loop). Market data delegates to the venue's own
  `BinanceAdapter` — one socket set apiece.
- The strict order families: LIMIT GTC only, with post-only riding the
  venue's `GTX` token (the `LIMIT_MAKER` type reads back onto a GTC
  order carrying the flag); market, OCO, reduce-only, bracket and
  non-GTC requests refuse before the venue with a clear reason. Venue
  statuses map strictly — the transitional ones refuse rather than
  default.
- The arming gate matches the Bybit discipline verbatim: the toolbar
  picks the venue, that venue's endpoint record is flagged testnet, and
  the testnet-trading toggle is armed — all three together, else the
  paper lane stands. The venue order journal now reflects both venues'
  private streams, and the kill-switch sweep covers the new venue.
- Credentials come only from `ISecretStore` (the
  `binance.testnet.api-key`/`binance.testnet.api-secret` pair, fetched
  per operation); no key reaches a log or a command line.
- The adapter-kit conformance battery covers the new adapter through a
  new delegated depth profile (the shell rides a real delegate) and a
  symbol-first book-topic template; the Infrastructure suite adds the
  signed-wire facts (22) and the private-stream e2e facts (4) against
  the in-process loopback rig.

## [0.1.0-alpha.136] - 2026-11-12

Build of board task T-192 (branch `feat/paper-account`): the paper
account reset — wipe the simulation back to a chosen starting balance.

### Added
- The balances panel grows a "Reset paper account…" route: the dialog
  prefills the persisted starting balance, refuses non-positive or
  unparsable input inline, and the confirmed reset wipes the paper
  simulation's balances, open positions and working orders, restarts it
  at the chosen balance, and reloads the panel's figures. A declined
  confirmation stays silent; live and testnet venues are never touched,
  and the demo adapter's canonical balance stays the shipped default.
- One confirmation names the reset's whole scope: the chosen balance and
  the working-order and open-position counts the fresh plan read counted,
  plus the paper venue's journal wipe.
- The chosen starting balance persists (the `Paper` settings section) and
  prefills the next reset.

## [0.1.0-alpha.135] - 2026-11-12

Build of board task T-191 (branch `feat/order-flags`): the order flags —
post-only and reduce-only — across the ticket, the adapters and the
history.

### Added
- `OrderRequest` and `Order` carry the post-only flag beside
  reduce-only: the maker-only contract is validated at the Core (limit
  orders only, everything else refused, never degraded), and the paper
  simulator refuses a crossing post-only limit — standalone or an OCO
  leg — outright instead of filling it as a taker, while an away one
  rests with the flag recorded.
- The Bybit testnet surface now places post-only spot limits with the
  venue's own maker-only time-in-force token and reads the token back
  onto a GTC order carrying the flag; reduce-only stays refused on spot,
  where the venue disagrees.
- The order history journal records both flags (the migration backfills
  the existing rows with `false`), the history window shows them in a
  Flags column, and the trade ticket offers the two switches: post-only
  for a plain limit, reduce-only while an opposite-side position exists.
- The amend pipeline copies both flags into the replacement request —
  a cancel+new amend can no longer silently strip the order's maker-only
  or reduce-only intent.

## [0.1.0-alpha.134] - 2026-11-12

Build of board task T-190 (branch `feat/tif-simulation`): honest TIF
simulation in the paper adapter.

### Added
- IOC and FOK now live at the placement moment in the paper simulator: a
  market or crossing order satisfies them whole (the fill is the
  answer), an IOC resting away records the terminal Cancelled and never
  rests or arms, and a FOK that cannot fill whole refuses outright with
  no order ever existing — the same refusal shape the
  immediately-triggering stop already had.
- GTD rests or arms like a GTC and expires at its own timestamp against
  the adapter's injectable clock: the sweep rides the reads, the ticker
  ticks (before any trigger evaluation) and the placements, and the
  teardown mirrors the cancel path exactly — the conditional state, the
  price stream, the OCO siblings, the parked bracket pair — so expired
  and cancelled-rest entries read truthfully in the order history.
- A bracket entry ended by the TIF immediacy at placement (an IOC-away
  entry cancelled, a pre-passed GTD expired) never parks its exits.

## [0.1.0-alpha.133] - 2026-11-12

Build of board task T-189 (branch `feat/daily-loss-limit`): the daily loss
limit in the risk governor.

### Added
- A configurable daily loss threshold (`Risk.DailyLossLimitEnabled`,
  `Risk.DailyLossLimitAmount` — off by default, 1000 quote units when
  armed) governs the cumulative day: once the current trading lane's net
  realized result for the order's own quote currency crosses the
  threshold, new order placements are blocked with an honest reason
  naming the day loss against the limit. The realized rows are already
  net of their commissions, so the loss is the plain window sum over the
  PnL engine's realized history for the day (the local calendar day as
  UTC bounds), and quote buckets never sum across currencies.
- The block is protective, never a trap: reduce-only requests are exempt
  before any read at all — closing positions, the kill switch's flatten
  and every protective exit always stay allowed — and the block lifts at
  the day rollover (the window re-anchors at the new local midnight) or
  by an explicit reset.
- The order ticket reads the blocked state honestly: a banner (with the
  day loss against the limit) appears while the parsed symbol's quote
  bucket is blocked, refreshed per settled symbol and after every failed
  placement; a Reset button beside it asks ONE confirmation and lifts
  the block for the rest of the day. The settings keys ship through the
  settings port (import/export/reset), en + ru vocabulary included.

## [0.1.0-alpha.132] - 2026-11-12

Build of board task T-188 (branch `feat/kill-switch`): the kill switch —
cancel all working orders + close all open positions.

### Added
- One user action (the Trading menu's leading item + the remappable
  hotkey Ctrl+Shift+X, registered with the hotkey manager so the cheat
  sheet and the settings tab carry it) sweeps every trading venue — the
  paper simulator and the Bybit testnet surface alike — cancelling every
  working order and closing every open position. The fresh per-venue
  reads build the plan, ONE confirmation lists the totals and each
  venue's orders and positions, and the per-item best-effort execution
  commands the plan: cancellations ride the symbol-first cancel path,
  closes ride reduce-only market exits sized by a fresh re-read,
  validated and risk-checked per leg (a rejection never reaches the
  venue). A venue whose read faults reports unreachable and the sweep
  continues; an item's fault produces its own failed report and never
  stops the rest; a vanished position reports already flat.
- The honest report window: per-venue counts (orders cancelled,
  positions closed with their symbols and exit order ids, already flat,
  not attempted), the failed items' reasons in the tail, and the
  unreachable venues named. Nothing anywhere reports the localized
  nothing without a confirmation; a refused confirmation touches
  nothing and reports nothing.

## [0.1.0-alpha.131] - 2026-11-12

Build of board task T-187 (branch `feat/position-sltp`): stop-loss and
take-profit management from the positions panel.

### Added
- The positions panel's positions table carries stop-loss and
  take-profit columns reading the levels the position reports (an
  unprotected cell stays empty) and a secondary SL/TP action per row.
  The protective-levels dialog prefills the levels the position holds,
  and each optional field parses in the invariant culture: an empty
  field clears that level, both empty clear the whole pair.
- The protective rewrite rides the existing conditional machinery as an
  either-or pair of reduce-only opposite-side legs placed through the
  normal order pipeline: the fresh position re-read, the direction
  check against the current price (a level on the wrong side is an
  explicit localized refusal, never a reinterpretation), both legs
  risk-checked atomically before anything moves, ONE confirmation
  naming the rewrite, then cancel-then-place — a refused second leg
  cancels the placed one and reports the unprotected state loudly.
- The protective pair's rewrite and clear work through the same one
  confirmation; a clear with nothing protected is an explicit localized
  nothing without a dialog.
- The paper exchange honors reduce-only fills: a protective fill caps
  at the opposite-side position the venue reports (a fill with nothing
  to close is refused), so a stale protective pair self-cleans on its
  first trigger instead of flipping into fresh exposure. The positions
  surface (`get_positions`) reads the working legs' levels back.

### Changed
- The positions table's column plan widens for the two numeric columns;
  the reverse keeps its single destructive weight in the row-action
  hierarchy.

## [0.1.0-alpha.130] - 2026-11-12

Build of board task T-186 (branch `feat/bracket-orders`): bracket orders
in the order ticket — the entry with its attached take-profit and
stop-loss exits.

### Added
- The bracket mode in the order ticket: an entry (market, or a limit
  priced from the shared price field) carries a take-profit limit leg
  and a stop-loss stop-limit leg as ONE request. The exits ride the
  opposite side with the entry's own quantity under one either-or group
  id; the ticket validates the whole shape, runs all three legs through
  the risk manager atomically (any rejection submits nothing), asks for
  ONE confirmation naming every leg, and sends ONE adapter call.
- The paper exchange assembles the bracket on the entry's fill: a
  market or crossing-limit entry arms its pair synchronously against
  the entry's own fill price, a resting limit entry parks until it
  fills or is cancelled. The arm is all-or-nothing — a refused leg
  takes the working sibling down with it, a half-armed bracket never
  stays working.
- The bracket's confirmation summary (`Trading.ConfirmPlaceBracket*`,
  en and ru) names the entry and both exits with the venue's fee
  economics; the fee estimates price all three legs in their expected
  roles (the entry by its kind, the take-profit as maker, the
  stop-loss as taker).

### Changed
- The Bybit adapter refuses the bracket shape up front with an honest
  reason — the bracket ships paper-first; live venues follow once the
  venue's own bracket API is wired.

## [0.1.0-alpha.129] - 2026-11-12

Build of board task T-184 (branch `feat/settings-rail-cards`): the
settings window's category rail and section cards.

### Changed
- The settings tab strip becomes the category rail: the four tab items
  ride the left placement at a fixed 190 px width, the quiet secondary
  ink at rest, the soft tint under the pointer and the selection, the
  keyboard focus on the Highlight ring, and the selected item holding
  the 3 px Accent tick (the theme's own left-pipe part, re-shaped). The
  arrow keys walk the rail and the selection follows the directional
  focus (the TabItem/TabControl focus-selection contract) — no custom
  keyboard code; the rail item states restyle the theme internals via
  the `/template/` selectors, the template itself stays untouched.
- Every settings page speaks the section-card shape (new
  `Themes/SectionCards.axaml`, included once and consumed by class):
  the Surface panel with the hairline border and the CornerL step
  (the T-183 ramp — the task's original CornerM predates it), the 3 px
  Accent tick and the tracked uppercase caption (the new
  `Settings.Section.*` keys, en and ru) over it. Appearance carries one
  card, Indicators one, Integrations two (the MCP server card and the
  import/export card — the port availability flag moved onto the port
  card), Workspace two (pairs and hotkeys); the inner viewport caps
  (theme errors, MCP clients, the import preview, the hotkeys rows)
  and the body scroll viewers stay as the small machines' safety net.
- The settings window rides the roomier default (880 x 720, min
  720 x 560) so the rail and the cards breathe at the default size,
  and no page scrolls at the default window bounds (the headless
  bounds pin). Settings behavior is untouched — the bindings, the
  commands and the persistence keep their shapes; the rail selection
  is view state only and never persisted.

### Added
- The `SelectedTabIndex` view-state property on the settings view
  model (RaiseAndSetIfChanged, never persisted) and the headless
  no-scroll pins over the four pages' body scroll viewers plus the
  rail's arrow-key selection fact.

## [0.1.0-alpha.128] - 2026-11-12

Build of board task T-183 (branch `feat/button-kit`): the button kit —
quiet resting, a soft-tint hover, one primary per window, the corner
tokens — riding the Livery 0.1.11 satellite release.

### Changed
- The generic button family goes quiet (Livery 0.1.11): the resting
  button is transparent over its hairline Border stroke, the pointer
  answers with the soft Accent tint the theme derivation publishes
  (`ButtonHoverTint`, a 12% Accent step over the theme's own Surface —
  never the solid Accent flash), the press a stronger step of the same
  tint. The rounding rides the new CornerM corner token across the
  button/text/combo family, and the row cards ride CornerL.
- The `.primary` class marks each window's ONE main action (solid Accent,
  the derivation's scored readable foreground, the emphasis ring on
  hover, the strip's dim step on press): the settings Save, the backtest
  Start, the stats Apply, the ticket's Amend, the wizard Finish, the
  disclaimer Acknowledge, the crash dialog Dismiss and the license
  Activate. The confirmation gate keeps its equal-weight buttons by
  design (a safety gate never steers), and the ticket's state-filled CTA
  keeps its own trading vocabulary.
- The toast rows adopt the kit's quiet carrier: the Button is
  chrome-free at every state and the chrome lives in the card — the
  card's own states answer the pointer (the soft tint on hover, the
  stronger step on press) and the keyboard focus ring lands on the card,
  so the hover no longer leaks around the card's corners and the chrome
  never renders twice. The card rounding rides the CornerL token.
- The row-action family declares its filled rest explicitly (the generic
  rest went quiet) and its pressed step stays the planar Border fill.

### Added
- The button-kit pins: at most one `.primary` button per window (the
  adopted set pinned by name, the confirmation gate pinned
  primary-free), the toast rows riding the quiet carrier with the card
  carrying the chrome, the card state lanes present, and no bare corner
  radius literal left in the toast row template (the card rides the
  CornerL token). The theme battery walks the derived button-tint rows
  over every shipped palette (the soft tint's text legs, the primary's
  scored foreground and the tint's state-existence floor).

## [0.1.0-alpha.127] - 2026-11-12

Build of board task T-182 (branch `feat/surface-strays`): the small-surface
strays ride the scale and the palette, filed from the 2026-11-12 UI review
findings batch.

### Fixed
- The off-scale spacing strays map onto the T-155 scale steps: the 6px
  margins, the `Spacing="14"` stacks and the `Margin="20"` window frames
  (BalancesPanelWindow first, then the dialog fleet, the settings tabs,
  the stats tiles, the wizard and the log viewer) now ride the published
  tokens or their nearest step - the small surfaces stop drifting off the
  spacing canon one pixel at a time.
- The bare `Opacity` dimmers ride named keys again: the watchlist row's
  24h volume and the ticket's sizer support texts read through the
  palette's TextSecondary ink (a theme retints its dim text
  deliberately), the chart toolbar's VWAP and VolumeProfile toggles carry
  a named quiet class whose armed state answers in the kit's on-ink (the
  armed state no longer stays equally dimmed), and the palette's and the
  hotkeys sheet's workspace scrims ride a derived ScrimBrush - the
  applied Background's RGB under the veil alpha, republished on every
  theme apply (the T-150 derived-key shape; the palette itself still
  carries no translucent key).

### Added
- The surface-stray source pins: the spacing walk over every shipped
  view (no off-scale margin, padding or spacing literal outside the
  documented overlay-drop anchors), the bare-opacity sweep (no numeric
  Opacity literal in any view), the shipped-theme contrast battery (the
  dim ink reads at or above 4.5:1 over background and surface in every
  theme), and the scrim-veil facts (the derivation keeps the source RGB
  under the veil alpha; every shipped theme serves the veil from its own
  Background on the real apply path).

## [0.1.0-alpha.126] - 2026-11-12

Build of board task T-181 (branch `feat/ticket-cta-states`): the order
ticket's CTA and side segments answer hover and pressed, filed from the
2026-11-12 UI review findings batch.

### Fixed
- The order ticket's primary CTA and the segmented Buy/Sell pair answer
  the pointer again: the window-scoped class styles paint their state
  fills after the app styles, which defeated Livery's generic
  hover/pressed fills - the highest-stakes buttons in the app gave zero
  visual response. The hover now rides the kit's emphasis ring (the row
  grammar's own hover border), the press the strip's dim step; the state
  fills never leave (a trading segment keeps its side under the
  pointer).
- The same hole closed where the fleet-wide source pin reported it: the
  settings star brightens under the pointer (the armed star stays gold),
  and the row grammar's quiet outlined sibling presses alive through an
  explicit family pressed step (the destructive ghost's own pressed
  keeps winning).

### Added
- The interaction-variant source pin: every window-scoped Button class
  style that paints a Background must ship its `:pointerover` and
  `:pressed` siblings - any window reintroducing the hole reports
  itself, naming the family and the missing sibling.

## [0.1.0-alpha.125] - 2026-11-12

Build of board task T-180 (branch `feat/theme-preview-mirror`): the theme
preview swatches mirror the Accent again, filed from the 2026-11-12 UI
review findings batch.

### Fixed
- The dark, light and oled themes' fourth preview swatch mirrors their
  live Accent again: the T-151 Accent re-tune never reached the mirrors
  and the theme picker's swatch strip kept selling colors the themes no
  longer own - dark advertised the retired hover blue `#0f3460` (Accent
  `#2563eb`), light a grey `#dee2e6` (Accent `#3b82f6`), oled a
  near-black `#1a1a1a` (Accent `#2563eb`).
- The palette contract battery grew the mirror lane: every shipped theme
  carrying a fourth preview swatch must mirror its Accent (the A11Y
  audit's own rule, enforced in the gate from now on - a stale mirror
  fails the pin naming the file and the pair), and the auto-created
  sample theme keeps the same promise on load.

## [0.1.0-alpha.124] - 2026-11-12

Build of board task T-179 (branch `feat/tape-header-gap`): the Skia header
bands breathe, filed from the user's ask - add padding between the table
header and the data.

### Fixed
- The trade tape's and the order book's column captions no longer sit
  flush on their first data rows: the caption baseline moved from the
  header band's bottom edge to a named constant that keeps background
  rows of distance from the first row's ink on both widgets (the bar
  tops included on the book).
- Both header bands carry two text lines - the tape's paused hint and
  the book's symbol/spread label ride the top line, the column captions
  the line under it - and the pre-fix band held them squeezed together
  with the second line glued to the band edge (the paused hint's
  descenders crossed the size caption's tops even before this fix). The
  band grew 22 -> 26 px so both lines keep their own air; at some panel
  heights that costs at most one data row.

## [0.1.0-alpha.123] - 2026-11-12

Build of board task T-178 (branch `feat/drawings-delete`): the drawings
delete affordance, filed from the user's ask - how do you delete drawn
lines from the chart, maybe a button should be added.

### Added
- The selected drawing's removal is discoverable: a trash button rides
  the chart header's tool strip (after the Fib tool), armed only while a
  drawing stands selected - with nothing selected it stays disabled, the
  honest answer to "delete what?". Pressing it removes the selected
  drawing through the same route the chart's Delete key names: the
  selection clears and the layer saves. The removal semantics are the
  T-091 ones, untouched.
- The chart's own Delete/Escape keys surface where the user reads them:
  the affordance's tooltip names the Delete key as the removal and Esc
  as the placement-cancel or selection-clear. The hotkey registry keeps
  its four window-level actions - the chart control's keys stay
  control-local.

## [0.1.0-alpha.122] - 2026-11-12

Build of board task T-177 (branch `feat/rail-scroll-splitter`): the
signal rail scrolls its overflow and drags its height, filed from the
user's ask - add a scrollbar to the signal rail and a splitter too.

### Added
- The rail's rows surface scrolls: a busy rail's older readings stay
  reachable - the overflow rides a scroller below the rail head, the
  newest-first order is the rows ring's own and the scroller only
  reveals its tail.
- The rail's boundary drags: a row-wise sibling of the T-172 workspace
  handles rides a thin track between the chart and the rail (the grip
  hairline tints to the Accent on hover, the arrow keys step the
  boundary when focused), the row's min/max clamps hold the drag inside
  the 72-480 px band and the chart row keeps its star - the rail grows
  within its band, never eats the chart.
- The dragged height persists: the TwoWay row binding carries every
  drag into the rail ViewModel, the settled burst writes the settings
  file once (the T-038 drag-settle pipeline, row-wise), the startup
  walk restores the stored height before the first frame, garbage-safe
  out-of-range values fall back to the shipped default, and external
  writers apply live without echoing. The rail ships at its old 172 px
  cap, so a fresh install shows the layout every session saw.

## [0.1.0-alpha.121] - 2026-11-11

Build of board task T-175 (branch `fix/binding-noise`): the startup
binding sweep, filed from the user's report - the app logged binding
errors into the debug output on every launch.

### Fixed
- The startup no longer logs binding errors: the main window now carries
  its view model through the constructor (`MainWindow(MainViewModel)`),
  so the DataContext precedes `InitializeComponent` and the XAML's
  `$parent[Window]` lanes - the View menu's detach toggles, the HUD's
  tap gate - evaluate against the live graph on their first pass (the
  null-DataContext phase that logged "Value is null" warnings and
  self-healed on the later assignment is gone).
- The workspace panels (chart, order book, assistant) are created in the
  window's constructor instead of the XAML: each carries its view model
  as a LocalValue from its own construction, so its subtree's first
  binding pass resolves against the panel's own VM. The XAML-created
  alternative let the loader wire the inherited MainViewModel into the
  still-unattached panel ahead of every visual and logical tree link -
  the panel's subtree cast-errored on every binding before self-healing
  (the second, larger noise family the sweep caught). The LocalValue
  discipline the T-103 detach rides now holds by construction; the
  panels keep their names, and the chart panel's HUD frame-tap gate
  (T-108) rides the panel's own attach hook (a detached chart still
  freezes its HUD readout).
- The menu's gesture hints are typed: the HotkeyViewModel's menu
  properties publish `KeyGesture?` (parsed from the effective map's
  canonical text, a stray unparsable text logs a warning and leaves the
  hint silent instead of fabricating a binding error), so the
  `InputGesture` bindings never carry a string.
- The designer route stays: the parameterless `MainWindow` ctor remains
  (documented as the designer's shape), and the headless startup pins
  hold both faces - the production construction logs zero binding
  warnings at or above Warning while rendering the four effective
  gesture hints and carrying the panels' view models; the designer
  shape's late assignment reproduces the killed noise as the
  counterfactual.

## [0.1.0-alpha.120] - 2026-11-11

Build of board task T-174 (branch `feat/strategy-catalog`): the standard
pack grows the strategy catalog, filed from the user's ask - "add more
strategy variants" - scoped by the supervisor's analytics to a small set
of well-understood classics.

### Added
- The pack pin moves 0.2.0 -> 0.3.0: the Plugins satellite released the
  three new drop-in strategies (the satellite-first rule; the tag
  `v0.3.0` published the package, eleven lib assemblies now) -
  `ZettaTrade.Plugins.EmaCross` (id `ema-cross`, the classic 12/26
  fast/slow EMA cross, each EMA seeded from its own period's simple
  average then walked with the classic `2/(N+1)` smoothing),
  `ZettaTrade.Plugins.RsiReversion` (id `rsi-reversion`, the Wilder RSI
  (14) reversion bands: at or below 30 stands long, at or above 70
  stands flat, between holds) and `ZettaTrade.Plugins.BollingerBreakout`
  (id `bollinger-breakout`, the classic 20/2.0 Bollinger bands as a long
  breakout: a close above the upper band stands long, a close below the
  mid band stands flat, the sigma computed in exact decimal arithmetic).
  All three keep the SmaCross template shape - a pure per-candle walk, a
  guarded constructor, an honest neutral warmup, replay-identical
  windows - and the satellite's 38 new hand-countable facts pin the
  walks.
- The shipped-pack pin grows with the pack: the Strategies.Tests
  pack-staging facts now walk the real loader over all four strategy
  assemblies and pin the catalog's canonical id set (`sma-golden-cross`,
  `ema-cross`, `rsi-reversion`, `bollinger-breakout`) - the backtest
  picker's registration contract, staged from the copy-local lib assets.

## [0.1.0-alpha.119] - 2026-11-11

Build of board task T-173 (branch `feat/book-detach-space`): the detached
order book frees its main-window column, filed from the user's ask - the
book lives in its own window while the workspace keeps the empty hole.

### Fixed
- Detaching the order book now frees its workspace space: the book
  column, both its splitter tracks and its minimum clamp collapse to zero
  so the chart star absorbs the width (the chart/assistant detaches keep
  their reserved space - the ask named only the book). The attach puts
  the width the user actually saw right back: the capture reads the
  displayed truth at the detach beat (a drag may still ride the settle
  debounce), not any persisted cache. A book already detached at
  construction starts with the collapsed geometry from the first frame
  and the stored width stays the attach-back truth.
- The book-side grab handles hide with the panel: with the column gone,
  the chart/book boundary has nothing to serve, and a drag at the old
  book/tape seam could only resurrect a phantom column - both tracks ride
  one `BookSplitterColumnWidth`, both handles bind `!IsBookDetached`.
- The book's persistence pipeline guards on the applied display width
  (the assistant collapse's shape): the detach flip, the external echoes
  and the startup restore stay silent, so the collapse never writes a
  zero over the width's persisted truth; an external width while
  detached becomes the attach-back truth only.

### Added
- The book-detach pins: five ViewModel facts (the collapse/restore
  geometry round-trip, the View-menu toggle path, the settings-pipeline
  silence across the quiet window, the detached-at-construction first
  frame, the external-width-while-detached truth) and three headless
  facts on the REAL window (the drag-to-350 detach frees the column for
  the chart and the attach restores it, the collapse never persists, a
  pointer sweep at the hidden book/tape seam cannot resurrect the
  column).

## [0.1.0-alpha.118] - 2026-11-11

Build of board task T-172 (branch `feat/splitter-grips`): the workspace's
four layout boundaries become proven-draggable, discoverable grips, filed
from the user's report - the splitters were invisible and the resize
never worked.

### Fixed
- The workspace's resizable boundaries now follow the pointer. The stock
  `GridSplitter` only ever wrote the FIRST fixed column when its
  neighbors were both fixed (its own SplitBehavior.Resize1), so on the
  book/tape and tape/watchlist seams the grabbed line never moved - the
  chart star silently absorbed the drag instead. The four boundaries ride
  the new `ColumnResizeHandle` (a Thumb in `ZettaTrade.App.Controls`):
  both neighbors trade the delta, the star side absorbs, the standard
  direction holds (a rightward drag grows the column left of the handle),
  the joint minimum clamps stop the shrinking side, and the arrow keys
  step the focused boundary by 10 px. The TwoWay column bindings keep
  carrying every write into the persisted widths (the T-038 pipelines,
  untouched).
- Every boundary shows a visible grip line at rest (the quiet 1 px hair
  the stock splitter never had) and it widens to a 3 px Accent pill on
  hover, with the west-east cursor over the whole 8 px track.

### Added
- The headless splitter-drag pins (the T-144 harness lane): eight facts
  drive the REAL main window through pointer press/move/release scripts -
  each boundary moves with the delta it is given (the boundary follows
  the pointer on the fixed-to-fixed seams), the tape's minimum clamps a
  past-the-minimum sweep, a settled book drag persists through the
  settings pipeline end to end, the four grip lines render at their rest
  width and widen on hover, and the focused handle steps the boundary
  with the arrow keys.


Build of board task T-170 (branch `feat/scroll-gutter`): the scroll
surfaces audit filed from the user's report - no window's content sits
under the overlay scrollbar.

### Fixed
- Every scroll surface outside the settings window clears the Fluent
  overlay scrollbar's strip (the shape T-169 shipped for the settings
  tabs, walked across the rest of the app): the alerts list (a ListBox
  under the outer bar - the audit's direct overlap report), the balances
  and order-history stacks, the notification summary, the
  license-activation terms and the positions panel's three tab stacks
  end a 16 px strip short of the right edge; the assistant chat's
  content carries the strip on top of its own 8 px insets (24 px on the
  right); the wizard's symbol list, the log viewer's rows and the main
  window's watchlist and palette lists carry the same gutter as their
  own internal list padding. View markup only - no ViewModel change.
- The headless scroll-gutter pins (the T-144 harness, the T-169 lane's
  shape): four facts materialize the REAL windows and measure the
  gutters - the five dialog windows' outer scroll contents, the
  positions panel's three tab stacks walked tab by tab, the main
  window's internal-bar lists plus the assistant chat's 24 px inset, and
  the wizard's and log viewer's internal lists. The windows
  materialize without a view model (the gutters are the views' own
  properties; every activation body is null-safe) - the doc comments
  say what the facts measure.
## [0.1.0-alpha.116] - 2026-11-11

Build of board task T-169 (branch `feat/settings-polish`): three
settings-window polish fixes filed from the user's report.

### Fixed
- The pairs tab's transfer buttons ride the chrome kit's icon button
  (T-154 consumed as-is): the plain `>` / `<` text buttons became
  borderless glyph buttons with explicit-stroke chevron Paths (the
  title-bar strip's glyph discipline), the kit's opacity feedback carries
  hover/press. The automation names (`Settings.Pairs.Hide`/`Reveal`),
  the tooltips, the commands and the vertical order survive — the glyph
  alone never carries meaning. No satellite change; a Livery follow-up
  candidate (a shared chevron glyph or an `IconButton` glyph vocabulary)
  is recorded on the task for the kit's own repo.
- The indicators tab's seven rows (SMA + the standard pack) re-lay on one
  shared column set — a fixed name column wide enough for the widest
  canonical name (MACD/BOLL), the info cue, one aligned Period caption
  column and one fixed box width — so the ragged name widths no longer
  walk the captions and boxes per row (the user's "nothing changed
  here after all" verdict on the earlier alignment attempt finally
  gets a structural fix). The section caption moves onto its own header line (rendered
  whatever the catalog holds); the loaded-plugin row gates
  (`Is*PluginLoaded`), the honest empty state and every binding ride
  untouched — no ViewModel change.
- Every settings scroll content (both merged tabs' bodies, the MCP client
  cards, the import preview and the broken-theme list) gains a right
  gutter shaped by the Fluent overlay scrollbar's own strip: the
  ScrollViewer's content presenter spans the scrollbar's 16 px
  ScrollBarSize strip when the bar appears, so the content now ends a
  strip width short of the viewport's right edge — the hotkeys' gesture
  boxes, the verdict messages and the pairs hint never sit under the bar.

### Tests
- Headless settings-lane pins (the T-144 harness): the seven rows'
  Period captions and boxes share x-bounds and one box width across all
  visible rows; the transfer buttons keep their names/commands and
  render the chevron affordance (Path content over the kit's `icon`
  class, no plain-text content); each scroll content's right edge clears
  the 16 px gutter at the shipped 760×720 default.

## [0.1.0-alpha.115] - 2026-11-11

Build of board task T-171 (branch `feat/license-di`), supervisor-solo
(the wave's subagent lane died on the provider quota; the takeover rule
applies).

### Fixed
- The license activation dialog opens again (T-171): the T-132 licensing
  DI factory created the `LicenseEulaService` instance but registered it
  under the concrete type — a factory `AddSingleton(sp => ...)` without
  the generic service type — while the activation ViewModel depends on
  the `ILicenseEulaService` interface, so clicking a license activation
  entry logged `Failed to open the license activation dialog`
  (`InvalidOperationException: unable to resolve ILicenseEulaService`)
  and the dialog never appeared. The registration now answers the
  interface; the new App.Tests registration facts pin the service type,
  the factory shape and the licensing graph activation (the T-137 shape,
  caught by the supervisor's functional probe reading the day log).

## [0.1.0-alpha.114] - 2026-11-11

Build of board task T-168 (branch `feat/strategy-pack`); the Plugins
satellite release rides first in the same series (the strategy ships in
the standard pack, the consumer pin moves with it).

### Added
- The strategy seam rides the plugin SDK contract 2.1.0 (T-168): the
  implementation contract (`IStrategy`, the SDK-owned `StrategyContext`
  over the contract's `IndicatorBar` OHLC shape — app entities never
  reach the surface) and the `StrategyStance` vocabulary move into
  `ZettaTrade.Plugins.Abstractions`, additive, in the seam's canonical
  `ZettaTrade.Strategies` namespace; the golden surface baseline rewrites
  to 2.1.0. The single `Evaluate` call site (`StrategyHost`) gains the
  app→SDK bar adapter — one array per series evaluation serves every
  registered strategy.
- The standard pack's first strategy (T-168, satellite release
  `ZettaTrade.Plugins.Standard` 0.2.0): `ZettaTrade.Plugins.SmaCross`,
  id `sma-golden-cross`, the classic 50/200 two-SMA cross — the host's
  256-bar window carries the warmup. The backtest picker's catalog scan
  finally lists a shipped strategy instead of the honest zero, and the
  satellite's suite walks the cross (golden/death/tie/warmup).
- The `PluginsVersion` consumer pin moves to 0.2.0 in the same series.

## [0.1.0-alpha.113] - 2026-11-11

Build of board task T-167 (branch `feat/titlebar-buttons`); the Livery
0.1.10 satellite release rode first (the fix lands in the package, the
consumer pin moves in the same series).

### Fixed
- The title-bar close button rides its own column (T-167, Livery 0.1.10):
  the logo-slot addition shifted the strip's other buttons one column
  right but left the close button on its old attribute, so the close and
  maximize icons drew on top of each other on every full-chrome window
  (the user-reported overlap). The close moves to the grid's last
  column, the maximize/restore pair keeps its shared state slot by
  design, and the satellite's style pin walks every strip button's
  declared column.
- The Livery consumer pin moves to 0.1.10 in the same series (the one
  `LiveryVersion` line, the five consumers follow it).

## [0.1.0-alpha.112] - 2026-11-11

Build of board task T-166 (branch `feat/demo-catalog-majors`); no
satellite release rides it.

### Fixed
- The demo catalog covers the watchlist's starter majors (T-166): the
  fixed demo universe gains ADA/USDT, DOGE/USDT, BNB/USDT, LINK/USDT,
  AVAX/USDT and LTC/USDT with documented anchors, so on the default
  first-run exchange (Demo) every starter row prices instead of throwing
  an ArgumentException inside the ticker projection at every startup —
  six of the ten starter rows stayed unpriced forever and each logged a
  "ticker stream failed" warning, and under the VS debugger the six
  exception pauses made the launch look broken. The `Validate` throw
  stays (trading safety: unknown symbols are an error, never a default);
  the fix removes the starter-list-vs-catalog mismatch, and the seam is
  pinned from both sides (the adapter's anchor table covers exactly its
  catalog; the watchlist starter set is a subset of the demo catalog).

## [0.1.0-alpha.111] - 2026-11-11

Build of board task T-165 (branch `feat/theme-singleton-hygiene`); the
Livery 0.1.9 satellite release rode first (the dispose contract lands in
the package, the consumer pin moves in the same series).

### Fixed
- The theme manager's dispose stops claiming process-wide initialization
  (T-165, Livery 0.1.9): a disposed `ThemeManager` now clears the static
  `Instance` when it owns it — the localization manager's T-073 shape,
  which the theme twin lacked. The stale pointer served subscribes and
  lookups from the torn-down subjects forever (the T-144 test-suite
  poison's exact route: every later widget attach died on the
  `ThemeChanged` subject's CheckDisposed); with the honest
  `IsInitialized` every widget's attach guard skips the theme
  subscription gracefully instead, the headless lane's per-world heal
  keeps the lane's world app-shaped, and the Livery suite pins the
  dispose-clears-Instance contract with two facts.
- The Livery consumer pin moves to 0.1.9 in the same series (the one
  `LiveryVersion` line, the five consumers follow it).

## [0.1.0-alpha.110] - 2026-11-11

Build of board task T-143 (branch `fix/palette-a11y-rows`); no satellite
release rides it.

### Changed
- Every symbol palette row carries a composed automation name (T-143):
  a screen reader reads a symbol entry — "BTC/USDT on Bybit", qualified
  by ", favorite" and ", recently applied" as the marks hold — instead
  of the row record's `ToString` dump. The name composes in the
  ViewModel through the localization manager (the row template binds
  the record's `AutomationName` on its root, which the list item's
  automation peer falls back to) and every piece rides the
  `Chart.A11yPalette.*` resx keys in both cultures.
- A language switch re-publishes an OPEN palette, so every row's name
  re-lands in the new language; a closed palette composes its names at
  its next open. Each qualifier suffix carries its own separator, so
  every language owns its full qualifier shape.

## [0.1.0-alpha.109] - 2026-11-11

Build of board task T-142 (branch `fix/book-timestamps`); no satellite
release rides it.

### Changed
- One clock discipline across every adapter's book path (T-142):
  `OrderBook.Timestamp` is the snapshot's local receive moment — the
  `DateTimeOffset.UtcNow` the adapter's ingestion point captured when the
  update arrived — never the exchange's own server stamp. The semantic is
  documented once on the entity; the change is behavior-compatible for
  every consumer outside the WS-latency histogram.
- Bybit's WS book path stopped carrying the envelope's server `ts` into
  the rebuilt book: the frame's receive moment is captured once at the
  dispatcher and rides the state machine into the stamp (the ticker keeps
  the exchange `ts` — an event fact). Binance captures the receive moment
  at the frame dispatcher too (its partial depth payloads carry no event
  time at all); the demo adapter's locally generated books already
  conform.
- The WS-latency histogram measures transport (T-142): with every book
  stamped by the same local clock, the HUD's ages are the
  receive-to-render pipeline latency — a skewed wall clock shifts both
  sides equally, so the ~650 ms local-vs-Bybit offset can no longer dump
  the whole book stream into the >500 ms overflow bucket. The histogram's
  doc comments name the metric precisely.

## [0.1.0-alpha.108] - 2026-11-11

Build of board task T-141 (branch `feat/soak-isolation`); no satellite
release rides it.

### Changed
- The app learns the harness/sandbox profile pin (T-141):
  `ConfigPathResolver` honors the `ZETTATRADE_PROFILE_DIR` environment
  variable — when it carries a real directory, every profile root
  (config, data, state) resolves inside it, so the whole profile
  (settings, secrets, databases, logs) lands in the sandbox and the
  user's real profile stays invisible to the process. A bare APPDATA
  override could not serve this: on desktop Windows
  `Environment.GetFolderPath` resolves through `SHGetKnownFolderPath`
  and only falls back to the environment when that call fails, so the
  pin is an application-owned seam.
- The app learns the harness adapter allowlist (T-141):
  `ZETTATRADE_ADAPTERS` keeps the facade's registered catalog to the
  named adapters (unknown ids refuse the launch). The palette warm-up
  and the balances aggregation walk that catalog and fetch every
  entry's symbol catalog at startup, so a demo-only catalog is what
  makes the soak's run network-free.
- The soak driver (T-141) seeds the sandbox with the pinned demo
  adapter, runs it demo-only, censuses the run's own log (the sandbox
  settings load present, no fallback warnings, zero real-exchange
  hosts — with the demo-only catalog a persisted demo restore is a
  silent no-op), and discards the sandbox after the run.
- Fixed in flight (T-141): the single-instance claim read a missing
  profile directory as contention (DirectoryNotFoundException is an
  IOException), so every fresh-profile start — a first-ever launch
  included — redirect-exited with HandoffUnavailable; the claim now
  creates its own lock directory before the exclusive open.

## [0.1.0-alpha.107] - 2026-11-11

Build of board task T-162 (branch `feat/branding`); the Livery 0.1.8
satellite release rode first (the strip's optional logo slot lands in the
package, the consumer adoption follows in the same series).

### Changed
- The product speaks its own mark (T-162): the shipped binaries carry the
  in-house icon set (a rounded brand tile with the stroked Z monogram,
  7 taskbar-ready frames from 16 to 256 px, generated by
  `scripts/make-brand-ico.ps1` from the vector source of truth
  `Assets/zettatrade-mark.svg`), the exe file's icon and the taskbar ride
  it through `<ApplicationIcon>`, every window carries the same asset
  through its `Icon` attribute, and the stock `avalonia-logo.ico` is gone
  from the assets entirely.
- The shell's title-bar strip shows the logomark at its left edge through
  the Livery 0.1.8 kit contract (`livery:WindowChrome.Logo`): a
  palette-tinted pair — the Highlight tile with the Z in the derived
  readable caption ink — so the mark retints with the theme and the
  no-hex-in-axaml discipline holds (the brand colors live only in the ICO
  binary and the SVG). The slot is hit-invisible by the kit's contract:
  the strip keeps its drag surface.

## [0.1.0-alpha.106] - 2026-11-11

Build of board task T-161 (branch `feat/small-surfaces`); no satellite
release rides it (Livery 0.1.7 landed with the previous task).

### Changed
- The first-run wizard's theme picker (T-161) reuses the settings picker's
  own swatch shape — the same preview-colors converter and the same 10x10
  radius-2 chip — so the two pickers cannot disagree about what a theme
  looks like, and the walk grows a localized step counter ("Step {0} of
  {1}", en+ru) that reads the position without the captions.
- The hotkey cheat sheet (T-161) renders its gestures as kbd-style keycap
  chips: one chip per key part (the sheet row exposes the gesture's parts,
  "Ctrl+K" reads as the two keys it is), a recessed palette shape — the
  window's own backdrop fill inside a Border stroke on the Surface card.
  The trailing-text shape is gone (pinned).
- The palette's blank page (T-161) groups the session's recents, the
  user's favorites and the other instruments with localized section
  headers (en+ru): each pool's first landed row carries its group kind and
  prints the caption above itself. The headers are the rows' own visuals —
  they never occupy a selection index, so the T-095 keyboard route walks
  the same flat list it always has; a typed query is one ranked pool, no
  heads there.

Build of board task T-160 (branch `feat/chart-toolbar-icons`); the Livery
0.1.7 satellite release rode first (the two kit additions land in the
package, the consumer pin moves in the same series).

### Changed
- The chart rail's three drawing tools (T-160) trade their text captions
  for the Livery kit's icon button: each rides an explicit-stroke glyph
  path (the strip precedent — inline `Stroke="{DynamicResource
  TextPrimary}"`, thickness 1.5), the armed state lights the stroke to
  Highlight through the kit's `Button.icon.on` classes, and the caption
  becomes an `AutomationProperties.Name` — an icon-only control's name
  never rides a tooltip. The three export actions gather under one
  icon-button flyout (`Chart.Export` string, en+ru); the menu items keep
  their header captions as their own names. Avalonia 12 fact: a
  `MenuFlyout` hosts `MenuItem` elements — the `MenuFlyoutItem` type is
  gone. VWAP, VolumeProfile and Compare stay text (outside the icon
  pass's named scope).
- The shared panel-header scaffold (Livery 0.1.7's `DockPanel.panel-header`
  + the combined title selector) is adopted by the tape head, the book's
  two captions and the chart rail head; the watch/heatmap tab captions
  ride the `panel-title` treatment alone — the tab chrome keeps its own
  geometry. Host container paddings stay the containers' own duties
  (recorded). Kit styles read tokens via DynamicResource (a Styles
  dictionary cannot StaticResource its sibling).

Build of board task T-159 (branch `feat/watchlist-polish`); no satellite
release rides it.

### Changed
- The watchlist rows (T-159) read direction without digits: the 24h change
  text tints by its sign and the sparkline colors by the ring's own
  oldest-to-newest direction (the two truths stay deliberately
  independent), both riding the candle state colors the chart and the book
  speak — the plain text ink and the Highlight line stay the no-signal
  states (unpriced, flat). The rows' hover affordance reads the palette's
  Surface (the stock Fluent hover painted a system brush straight onto the
  template part), and the inert selection stops painting the accent fill;
  the churn discipline holds — a re-pulsed row with unchanged signs raises
  nothing.
- The light theme's CandleDown retunes to the text-leg strength (#b91c1c —
  #ef4444 read 3.76:1 on white, below the WCAG AA text minimum the new pin
  carries; the dark CandleUp got the symmetric treatment in the light
  palette long ago). Every shipped theme now reads both candle inks at
  4.5:1 over its background (UI.Tests' palette battery pins the leg).
- The heatmap tiles stay untouched (the shared color-scale helper the
  scope's conditional named did not land; recorded in the canon).

## [0.1.0-alpha.103] - 2026-11-11

Build of board task T-158 (branch `feat/chat-bubbles`); no satellite
release rides it.

### Changed
- The assistant chat (T-158) speaks bubbles: the trader's turn rides right
  on the Highlight fill with the derived ChipForeground caption (the
  T-152-scored pairing), the assistant's answer left on a Surface card
  with the explicit TextPrimary ink, and an in-chat failure line renders
  bare in the Error color — authorship reads from position + tint, no
  labels. Each bubble carries its own header (role, moment, a quiet copy
  pin whose clipboard write is view-side and best-effort) and long
  answers wrap inside a bounded bubble; the messages stack replaces the
  old selection ListBox.
- Stop (T-158) joins the input row: it cancels the in-flight reply
  through the engine's existing cancellation seam, offers itself only
  while a reply runs (never a dead button), a raced click after the
  settle is a no-op, and a stopped request leaves the accepted turn
  unanswered with no error line (`AI.Chat.Stop`, `AI.Chat.Copy`, en + ru).

## [0.1.0-alpha.102] - 2026-11-11

Build of board task T-157 (branch `feat/table-grammar`); no satellite
release rides it — the row grammar stays app-local over Livery 0.1.6.

### Changed
- The table grammar (T-157): the positions panel's three sections split by
  tabs (Positions / Open orders / This session — `Positions.Tab*`, en + ru,
  the three redundant in-tab section headers dropped), and every table —
  the three panel sections plus the order-history window — speaks one
  shared row scaffold (`Themes/RowGrammar.axaml`, included once per
  window): an emphasized header row over a hairline, hairline-separated
  rows, side chips (LONG/SHORT reading) in the same state colors the
  ticket's segmented side speaks (CandleUp/Error, the keys the T-152
  contrast publisher scores, captions through the derived readable
  foreground), numeric columns right-aligned on the shared NumericFont
  stack, and a three-weight row-action hierarchy — the row's main move at
  the stock filled look, the quiet outlined secondary, and the subdued
  destructive ghost (danger reads in the text tint on hover only; the
  focus ring stays reachable over its transparent rest border) that only
  the two-step gated Reverse carries. Row state rides the view models
  (`IsBuySide` required + the `IsSellSide` negation on all three row
  types, filled by the parents from the order side itself). The command
  wiring is untouched: close/reduce/reverse, amend/cancel, bulk cancel
  and every confirmation gate stay exactly as they were.

## [0.1.0-alpha.101] - 2026-11-11

Build of board task T-156 (branch `feat/order-ticket-ui`); no satellite
release rides it — the vocabulary stays app-local over Livery 0.1.6.

### Changed
- The order ticket redesign (T-156): the side reads as two large segmented
  Buy/Sell buttons in the state colors (the CandleUp/Error fills the T-152
  contrast publisher scores, the captions through the derived readable
  foreground — the ComboBox is gone), the CTA is the primary filled action
  whose tint follows the picked side, the sizer presets are chips, and the
  parse refusals render inline next to their fields (each line hides with
  its own row) while the summary line is demoted to the flow lane — the
  risk rejections, the market-relative sanity readings and the adapter
  failures. `ErrorMessage` keeps its full-report contract (the
  behavior-parity battery reads it verbatim); the routing is display-side.
  The quantity/price captions grow unit hints from the parsed symbol's
  BASE/QUOTE (`NewOrder.UnitHintFormat`, en + ru). The trading logic is
  untouched: the parse → risk → confirmation path and every message stay
  exactly as they were.

## [0.1.0-alpha.100] - 2026-11-11

Build of board task T-155 (branch `feat/type-scale`); the tokens
themselves ship as the Livery package 0.1.6 (satellite repo).

### Changed
- The type-token consumption (T-155): every view reads its text sizes
  from the Livery ramp (Caption 11 / Body 13 / SubtitleStrong 14 /
  Title 16 — the platform default of 12 stays implicit, so all 35 bare
  12-literals are gone), the numeric surfaces set digits in the shared
  `NumericFont` monospaced stack (the ten stats tiles, the positions and
  order-history table columns, the ticket's quantity/price/trigger/trail/
  OCO-cap/risk fields, the HUD readout and the backtest report — the two
  hand-typed font-family copies dedupe onto the token), and the spacing
  scale (4/8/12/16/24) adopts its steps wherever the value already
  matched (a no-change vocabulary adoption; the between-step drift is
  the surface redesigns' lane). Layout is untouched: the ramp remap
  moves only 10/11.5 captions a half-pixel up to 11 and the scale
  adoption is a rename.

## [0.1.0-alpha.99] - 2026-11-11

Build of board task T-154 (branch `feat/livery-chrome-kit`); the shared
vocabulary itself ships as the Livery package 0.1.5 (satellite repo).

### Changed
- The chrome-kit consumption (T-154): the views ride the Livery package's
  shared classes and control — the chart toolbar's five armed toggles
  (the per-button `.on` style copies are gone), the lane chip and the
  three testnet badges (the chip class: shape, state fills, readable
  caption), the health-strip dots (the dot class) and the settings
  window's seven "i" cues (the InfoIcon control: focusable with a
  keyboard-focus ring, automation-named — an a11y step up over the
  pointer-only Border+TextBlock copies; the names ride seven new
  Chart.Indicators.A11y* keys in both cultures).

## [0.1.0-alpha.98] - 2026-11-11

Build of board task T-153 (branch `feat/status-bar`).

### Changed
- The status strip declutter (T-153): the strip carries exactly five
  groups — the message channel, the exchange picker, the active exchange
  + lane chip, the connection state and the health dots. The theme and
  language names left for the settings (both already switch there) and
  the version left for Help/About: every removed label stays reachable in
  one action.
- The message channel: the strip's first cell is now a real transient
  channel — each notification center event surfaces its localized
  one-line summary over the Ready idle text for a bounded window
  (five seconds), a later event replaces the displayed one and the cell
  reverts to the idle text after the quiet period. The idle text follows
  the language; a displayed message keeps its arrival-composed text.

## [0.1.0-alpha.97] - 2026-11-11

Build of board task T-152 (branch `fix/lane-chip-contrast`).

### Added
- The shared readable-chip treatment (T-152): every state-colored chip
  text rides a per-theme derived `ChipForeground` brush published into
  the application's resource world — the derivation scores the two
  achromatic candidates (black, white) against the applied CandleUp/
  Highlight/Error fills through Livery's public `Contrast.Ratio` and
  keeps the one whose worst pair reads best (the dark themes' bright
  fills read with near-black text — worst-case 5.58:1 over Error; the
  light theme's darker fills with near-white text — worst-case 4.83:1).
  No fixed foreground served both worlds: the inherited theme
  foreground measured ~2.3:1 white over #22c55e and ~3.8:1 black over
  the light theme's dark gold.

### Changed
- The chip surfaces riding the derived foreground: the status-bar lane
  chip (the T-123 PAPER/TESTNET/LIVE pill), the ticket's TESTNET badge
  and the panels' badges (the four T-127 copies), and the notification
  count badge — which dropped its previous walk-around (the badge's
  Background key as its text color) for the honest equal. The
  `ChipForegroundPublisher` (an `IAsyncInitializable` the startup walk
  owns) subscribes to the theme stream, re-derives on every apply (theme
  switches, hot-reload applies, the first-run default) and stays silent
  while the resource world carries no fills yet. Tests: the derivation
  facts pin both worlds' fills and a mixed best-effort edge, the
  acceptance walk applies all five shipped themes through the real
  manager and measures every chip pair at or above the 4.5:1 pill bar,
  and the source pins freeze the markers, the fill keys and the dropped
  walk-around across the four windows.

## [0.1.0-alpha.96] - 2026-11-11

Build of board task T-151 (branch `feat/palette-lint`).

### Added
- The palette role contract (T-151): `PaletteContract` in the Livery
  package 0.1.4 (the satellite's release, the tag `v0.1.4`) documents the
  role shape — Background/Surface/Border planar fills, Accent the
  interactive token (the hover fill and the focus stroke), Highlight the
  on-state token, TextPrimary the reading token, Buy/Sell the chip fills —
  and walks every palette through the WCAG battery: the hover fill and
  the focus stroke at 3.0:1 (WCAG 1.4.11), text over the interactive fill
  at 4.5:1, the chip fills at 3.0:1. A broken palette fails naming the
  exact pair (the satellite's own facts pin the diagnostic), a palette
  that cannot read its roles throws — never silently conforming.

### Changed
- The Accent role fix (T-151): the dark (#0f3460 → #2563eb), light
  (#dee2e6 → #3b82f6) and OLED (#1a1a1a → #2563eb) palettes carry the
  interactive-strength accent now — the hover fill and the focus stroke
  the styles paint with it are visible (#0f3460 read ~1.4:1 on the dark
  surface, #dee2e6 ~1.2:1 on the light background, #1a1a1a ~1.1:1 on the
  OLED surface). midnight and trading-pro already answered the contract
  and stay untouched; the auto-created sample theme drops the brand-red
  accent (its white-on-red text read 3.8:1) for the interactive blue.
  The battery pins the five shipped files and the generated sample — a
  rolled-back value fails the gate naming the pair and the golden.

## [0.1.0-alpha.95] - 2026-11-11

Build of board task T-150 (branch `feat/design-tokens`).

### Added
- The derived Fluent system keys ride the palette (T-150): ThemeManager
  0.1.3 (the satellite's release, the tag `v0.1.3`) publishes the stock
  control templates' own families (`SystemAccentColor*`, the
  `SystemControl*Brush` ramps, the chrome and highlight steps) from the
  applied theme's palette — a user JSON theme recolors the stock paths
  together with the palette roles, and a palette missing a foundation key
  publishes nothing (the palette keys' own omission semantics). The
  brush-family members publish as brushes, not Colors: Avalonia 12's
  property store enforces value-type identity per effectively bound
  entry (0.1.3's fix — a raw Color over a stock brush key threw the
  effective-value store wide).

### Changed
- The second parallel color system left the views (T-150): the status bar
  paints Surface over a top Border stroke (it kept the Fluent accent tint
  under every theme), the panel separators ride Border, and the settings
  cue glyphs ride Border/TextSecondary — a fresh theme JSON touching only
  Background/Surface/Border retints all three live; source pins forbid
  the Fluent `SystemAccentColor*`/`SystemControl*` keys in the app
  sources, and the headless publish pins walk the app's resource world.

## [0.1.0-alpha.94] - 2026-11-11

Build of board task T-163 (branch `feat/plugin-packages`).

### Added
- The standard indicator pack left the tree for its own feed package
  (T-163): the seven `src/ZettaTrade.Plugins/*` plugin sources moved to
  the standalone `ZettaTrade/Plugins` repo (a subtree-split, their
  history reaches back to the T-092 originals) and ship as the
  `ZettaTrade.Plugins.Standard` umbrella package — the seven plugin
  DLLs in `lib/net10.0`, version 0.1.0, pushed by the pack's `publish`
  workflow from the `v0.1.0` tag. The one version fact is the
  `PluginsVersion` property in `Directory.Build.props`; App.Tests and
  Infrastructure.Tests ride `PackageReference`s pinned by it, the slnx
  drops the Plugins folder, and the loader facts keep walking the
  staged assemblies — the package's lib assets copy-local where the
  ProjectReferences used to.
- The plugin SDK contract ships as the feed package (T-163):
  `IIndicator` moved from Charting into `ZettaTrade.Plugins.Abstractions`
  and computes over the SDK-owned `IndicatorBar` OHLC record (the
  Charting pipeline adapts the app `Bar` to it once per compute) — the
  golden contract baseline pins 2.0.0 and the out-of-tree packs build
  against that package alone.

### Changed
- The pack's standalone CI rides the contract package's Actions access
  grant: the Plugins repository's `GITHUB_TOKEN` restores the private
  `ZettaTrade.Plugins.Abstractions` package now that the package allows
  the Plugins Actions (before the grant every pack CI run died with a
  403 on the restore).

## [0.1.0-alpha.93] - 2026-11-10

Build of board task T-130 (branch `feat/livery-split`).

### Changed
- The appearance library became a private-feed package (T-130): the
  in-tree `src/Livery` sources are gone and App, Infrastructure, AI,
  UI and UI.Tests ride a `PackageReference` pinned by the single
  `LiveryVersion` property — the standalone `ZettaTrade/Livery` repo
  publishes the private `Livery` package to the GitHub Packages feed
  (the MIT license stays; the sources stay hidden and the package is
  the public surface). A future package bump is a one-line change
  here.
- The package feed is wired everywhere (T-130): the root
  `nuget.config` pins nuget.org plus the private `zettatrade` source
  with no committed credentials — developers add the source once per
  machine, CI injects the automatic GITHUB_TOKEN (the release and
  soak workflows gained `packages: read` and the feed-auth step).
- The chrome pins ride the compiled package surface (T-130): the
  window-chrome facts walk the object graph the `StyleInclude`
  serves — a bad publish fails in tests, not in a window — while the
  source-text pins (title-bar automation names, focus indicators,
  template content bindings) moved home to the Livery repo's own
  test suite.
- Rides Livery 0.1.1 (T-130): the two-repo rehearsal — a
  version-only release in the Livery repo, then the one-line
  consuming bump here — exercised the cross-repo path end to end.
  No product behavior changes.

## [0.1.0-alpha.92] - 2026-11-10

Build of board task T-145 (branch `feat/ui-layout`).

### Changed
- The chart toolbar no longer paints over the neighbors (T-145): the
  one-line 13-column header (a star symbol column plus twelve auto
  columns) overflowed the chart column without clipping and drew the
  spillover on top of the book, tape and watchlist panels — the chart
  Border being the workspace grid's last child. The header now splits:
  the symbol picker owns a stretch row on top and the controls ride a
  finite-width WrapPanel that folds onto further lines when the column
  narrows, so resizing finally agrees with the window.
- The settings dialog grew and freed its tab strip (T-145): a resizable
  760×720 window (Min 640×560) replaces the fixed 560×640 frame, and the
  six tabs fold to four — Appearance and Indicators stand, MCP plus
  Import/Export plus the factory reset ride Integrations, Pairs plus the
  hotkey editor ride Workspace; the retired tab headers survive as the
  sections' captions inside the merged tabs, the port section keeps its
  own availability flag (a hidden port never hides MCP), and every
  migrated control keeps its binding, handler and en+ru localization.

### Infrastructure
- Two new resx tab keys (`Settings.Tab.Integrations` and
  `Settings.Tab.Workspace`, both en+ru) and the `WindowLayoutPinsTests`
  source pins (8): the stale 13-column header absent, the wrap band and
  every wired toolbar action present, the resize attributes, the
  four-tab count with the folded keys resolving in both cultures; the
  chrome, accessibility and indicator-gate pins stay green.

## [0.1.0-alpha.91] - 2026-11-10

Build of board task T-132 (branch `feat/plugin-packaging`).

### Added
- The closed-source plugin packaging seam (T-132): the consumer side of the
  open-core split — a local, offline license-key activation flow plus the
  entitlement gate the plugin loader consults before every load. The key is
  the compact `zt1.<payload>.<signature>` token: an RSA-SHA256 signature the
  validator checks against the installation's configured public PEM (in
  settings), the claims binding (the product, the plugin id, the issuer's
  subject and the validity window) and the expiry — validation reads
  settings and computes only, nothing phones home, and every failure shape
  maps to an explicit status (malformed, tampered, expired, wrong product or
  plugin id, missing, unconfigured) that REFUSES: activation stores nothing
  and the loader keeps the plugin out — refused-not-defaulted on every path,
  including an unparsable protected-plugins list (a malformed list protects
  everything, not nothing). The license keys ride the settings document the
  rest of the configuration lives on. The activation dialog (Help menu) is
  local-first: a module carrying `<pluginId>.EULA.txt` next to its package
  surfaces its agreement at the first activation and waits for the accept
  tick before anything is stored; the acceptance persists per module.
  The private-repo conventions for the closed modules (repo naming, the
  proprietary LICENSE+EULA pair, the signing CI recipe, the key issuance
  shape) live in `docs/PLUGIN-PACKAGING.md`, and the open-core split in
  one glance lands as `docs/LICENSES.md`.

## [0.1.0-alpha.90] - 2026-11-10

Build of board task T-103 (branch `feat/multi-window`).

### Added
- Detachable panels (T-103): the chart, the order book and the AI assistant
  each open into their own window from the View menu's checkboxes — the
  panel's ONE control instance physically moves cross-window (no double
  subscriptions, no re-created state), the inline slot carries a note while
  the panel lives elsewhere, and closing the detached window (or flipping
  the menu checkbox) brings it back exactly where it was. The placement is
  stored as fractions of the display's working area under
  `UI.DetachedPanels`, so a DPI or resolution shift between sessions
  recomposes the same relative box (an exact display match wins, a monitor
  swap remaps by the stored absolute point, nothing matches centers), the
  persisted record parses fail-safe (blank, malformed, unknown-panel or
  unusable entries restore nothing and never throw), and the app shutdown
  never rewrites what the user left detached. A hidden assistant that still
  lives in its own window attaches back first — the collapsed column and
  the inline state stay one truth.

## [0.1.0-alpha.89] - 2026-11-10

Build of board task T-131 (branch `feat/plugin-sdk`).

### Added
- The plugin SDK seam (T-131): a versioned contract assembly,
  `ZettaTrade.Plugins.Abstractions`, that out-of-tree modules compile
  against — the per-class manifest with a stable id, a SemVer 2 version
  and declared dependencies; the one-time async lifecycle; a bounded
  services window (the plugin's own DI layer first, the host's
  capabilities behind it, missing ones answer null); the panel, toolbar
  and settings-section descriptors every plugin registers into its own
  layer. The blessed loader walks the plugins directory once at startup:
  duplicate ids, missing dependencies, cycles, unparsable versions and
  manifest-less entries refuse loudly (every refusal lands in the load
  report, sorted, with a reason), the survivors start dependency-first,
  garbage dlls warn and skip, and an absent plugins directory costs
  nothing. The public surface is pinned by a contract-baseline test — a
  breaking release is a major bump or a refusal; `docs/PLUGINS.md` is the
  author guide.

## [0.1.0-alpha.88] - 2026-11-10

Build of board task T-091 (branch `feat/draw-tools`).

### Added
- Chart drawing tools (T-091): the annotations palette arms a trend line,
  a horizontal price level and a Fibonacci retracement over the candle
  chart; placements anchor to (bar-open time, price) pairs — a pan, a
  zoom, a prepend of older history and a symbol/interval rebase keep every
  corner glued to its own bar. Placement gestures, snapping to bars and
  prices, anchor-body drags, Escape to cancel, Delete to remove the
  selection and per-key (exchange, symbol, interval) persistence in the
  local SQLite store — nothing leaves the process. The drawings' own
  theme colors (`DrawingLine`, `DrawingSelected`) ride all five shipped
  themes; the persisted layer reloads on every rebase and drops honestly
  on a corrupted row (an unknown kind or a degenerate geometry rebuilds
  nothing).

## [0.1.0-alpha.87] - 2026-11-10

Build of board task T-110 (branch `feat/a11y`).

### Added
- The canvas widgets' accessibility pass (T-110): every Skia-drawn surface
  (candles, order book, depth, tape, heatmap, equity curve) now serves a
  real automation peer deriving from `ControlAutomationPeer` instead of the
  automation-none default, named from the same state the drawing reads —
  empty panes keep their localized titles, a loaded chart composes its
  symbol and compare layers, a fed book or tape composes its symbol, and
  the equity curves ride the window-attached name (`Backtest.A11yEquity`,
  `Stats.A11yEquity`). Name composition lives in the pure
  `WidgetAccessibilityNames` compositors; all peers serve the Custom role.

### Changed
- Icon-only controls are now announced: the title-bar's minimize/maximize/
  restore/close buttons, the T-095 palette's query box and the AI chat
  input carry automation names from the localization catalog; the equity
  windows attach curve names in the views.
- Shared Livery themes carry visible keyboard-focus styles for Button,
  ToggleButton, TabItem, MenuItem and the title-bar strip icons, borrowed
  from the palette's on-state accent.
- Theme contrast conformance (pins walk the real theme files): light
  `TextSecondary` → `#5f6672`, light `Buy`/`CandleUp` → `#15803d`, light
  `Highlight` → `#8a6100`, trading-pro `Accent` → `#047857`; the WCAG AA
  text pairs read at 4.5:1 and the signal family at 3.0:1 across every
  shipped theme. The audit record lives in `docs/A11Y-AUDIT.md`.

### Infrastructure
- 10 new `A11y` resx keys in en + ru; live peer facts (7), source pins (5),
  pure name-composition facts (8) and WCAG conformance facts (4) certify
  the acceptance — every advertised control is named without a mouse.

## [0.1.0-alpha.86] - 2026-11-09

Build of board task T-124 (branch `feat/chart-compare`).

### Added
- The symbol-compare overlay (T-124): a toolbar action adds a second series
  of the standing interval from another symbol on the same adapter, drawn
  over the volume profile and under the indicator/VWAP overlays — one
  polyline per layer, each percent-rebased to ITS OWN first aligned close
  (the (100→110) ≡ (1000→1100) = +10% equivalence), with 4 theme-resolved
  legend colors (`Compare1`..`Compare4` slots, fixed distinct fallbacks).
  The pick rides the T-095 global palette's new compare mode — the rows
  rank the current exchange only, Enter completes the pick, Esc closes it
  null — and the chart's legend chip strip pinned top-right names the
  main series first plus one swatch-and-detach chip per layer, whose ×
  press zones route the removals. Layers are chart-level transient state:
  an interval or an exchange change drops the lot, a same-interval symbol
  switch keeps them, a stale fetch (the interval/adapter stamped at its
  start) drops its own result, and a workspace symbol switch never
  persists the layers. Removing every layer restores the single-series
  rendering bit-exact (any empty layer list short-circuits the pipeline);
  removals land nothing outside the chart panel, and every refusal (no
  loaded series, the chart's own symbol, a duplicate, the fifth pick, an
  empty fetch) surfaces on the error line, en + ru.

## [0.1.0-alpha.85] - 2026-11-09

Build of board task T-102 (branch `feat/hotkey-manager`).

### Added
- The hotkey manager (T-102): the four registry actions (open settings, new
  order, toggle assistant, symbol palette) ride one live map — the shipped
  gestures carried by persisted user overrides. The old static
  `Window.KeyBindings` are gone: the main window translates every pressed
  combination through the registry's single seam (`TryMatch` → `RouteHotkey`),
  so a binding and its advertisement can never diverge. The settings window
  gains a Hotkeys tab: one editable row per action, conflicts/invalid
  forms/plain gestures diagnosed at edit time and refused (never
  substituted), a valid edit commits at once and rebinds EVERY consumer —
  the window route, the menu's advertised gestures, the Help-menu "hotkey
  cheat sheet" overlay — without a restart. Per-row reset returns to the
  shipped default. The editor refuses modifier-less gestures (a bare key
  would fire while typing); a hand-edited settings file that holds one
  keeps it in the editor but out of the key routes. Registry defaults,
  stored-document restore, live reload, persist, route matching, conflict
  goldens, the editor's rows/commit/reset/sheet mirror and the DI
  registration shape carry their own facts; en + ru strings.

## [0.1.0-alpha.84] - 2026-11-09

Build of board task T-108 (branch `feat/perf-hud`).

### Added
- The performance HUD overlay (T-108): a Help-menu checkbox toggles a
  top-right dev-mode readout of the AGENTS performance targets — the
  chart's frame rate from a zero-cost frame tap in the chart's render
  pass, the order-book stream's delivery rate with its WS-latency
  histogram (snapshot age at the consumer edge, five bounded buckets
  plus overflow), the working memory figure and the market-data
  channel's backpressure as bird gauges. The overlay ships off, the
  choice persists through the `Diagnostics.*` settings keys, and the
  off state costs nothing: no subscriptions, no timers, one property
  read in the render pass. The gauge math (fps, histogram bucketing,
  gauge bars, memory formatting) is pure and golden-tested; the
  off-state zero-cost, the sampler lifecycle, the persistence walk and
  the localized readouts carry their own facts (en + ru labels).

## [0.1.0-alpha.83] - 2026-11-09

Build of board task T-140 (branch `feat/review-hygiene`).

### Changed
- The settings window's four click handlers (save/export/import/factory
  reset) ride task-based routes subscribed through `Observable.FromAsync`
  with the errors landing in the Subscribe's onError logging - zero
  `async void` outside the documented BUG-019 app startup leg. The save
  route keeps its close-despite-failure semantics, the other three keep
  their quiet-failure semantics - behavior-preserving by construction.
- The board/archive prose names no language outside the sanctioned
  localization surface: the eight user-voice quotes render in English
  marked "translated from the original", residual mentions in docs,
  skills and test assertion texts reword into the neutral shape, and the
  sanctioned facts stay untouched (the language catalog's display names,
  the en/Russian feature rows, rule 9 and its byte-identical twins, the
  locale codes).

### Fixed
- The MCP server host's three registrar methods no longer take the
  semaphore's synchronous `Wait()`: the tool/resource/prompt registries
  are concurrent-collection upserts, so a registration can no longer
  stall behind `StopAsync` while it holds the semaphore across its
  bounded run-loop wait. The register-before-start contract and every
  request-handler read keep riding the semaphore exactly as before.

## [0.1.0-alpha.82] - 2026-11-09

Build of board task T-094 (branch `feat/chart-export`).

### Added
- The chart's PNG snapshot export (T-094): the toolbar's PNG button renders
  the CURRENT frame - the exact snapshot the screen paints, same lens and
  annotations state - at a fixed 2x supersampling scale through the SAME
  Skia draw operation the on-screen render runs (one renderer, one
  snapshot: the acceptance holds by construction), stamps a translucent
  watermark (symbol, time frame) over the bottom-right corner and saves to
  the user-picked path. The suggested file name carries the file-safe
  symbol, the time frame and a UTC timestamp; the render fault or write
  fault surfaces on the chart panel's own error line, and a cancelled
  dialog exports nothing.
- The visible-range candles CSV: the second CSV button exports EXACTLY the
  bars the chart's frame paints - the control's new `VisibleBarsSlice`
  (the pan window resolved over the loaded series), handed to the
  ViewModel through the view and streamed through the shared
  `ICsvExportService` writer contract - never the whole loaded series by
  accident.
- The view-side save dialog generalized to one `SaveFilePicker` (the CSV
  flows keep their shape; the PNG flow rides the same discipline).
## [0.1.0-alpha.81] - 2026-11-09

Build of board task T-090 (branch `feat/chart-scales`).

### Added
- The Y-axis scale modes (T-090): the price chart's vertical axis now reads
  linear prices (the standing default), their base-10 logarithm, or the
  percent change over the window's first visible close. The pixel pipeline
  stays linear and maps transformed axis values - the mode is one decision
  at the spread and a wrap at every mapping site (`ChartPriceTransform`),
  never a parallel rendering path: candles, wicks, OHLC bars, the close
  line, indicator lines, the VWAP bands, signal and fill markers, the
  trading position and orders, the volume-at-price histogram, the grid and
  the axis labels all re-map in place, and the wheel-zoom window survives
  every mode switch untouched (the transforms are stateless functions of
  the window's data).
- The strict positive-domain guard: a price outside the mode's domain (a
  zero or negative candle in log mode, a corrupt anchor close in percent
  mode) clamps VISIBLY onto the drawn geometry's positive floor - the
  candle keeps drawing at the clamped position, the mode never silently
  falls back to linear, nothing throws and nothing drops.
- Mode-aware axis labels: the log mode prints prices (the log-domain tick
  unwinds to its price, with the raw-price decimals - log-unit digits would
  be nonsense on a price axis); the percent mode prints the signed relative
  read (`+3.42%`); the hover pill follows the axis semantics and the OHLC
  readout keeps quoting raw prices with raw-price decimals.
- The mode selector rides the chart toolbar next to the rendering type
  (three static localized items bound by index - the chart-type shim
  precedent), and the choice persists PER SYMBOL (`UI.ChartSymbolScales`,
  the `SymbolScaleModeMap` document - the fail-safe parse, the strict
  enum-name restore and the whole-map writes the chart types keep).
## [0.1.0-alpha.80] - 2026-11-09

Build of board task T-104 (branch `feat/single-instance`).

### Added
- The single-instance hand-off (T-104): the process start rides a decision
  layer before anything boots. An OS-enforced exclusive open on one
  well-known lock file decides ownership - crash-safe and portable by the
  framework's own semantics: a killed instance never wedges the next
  launch, and the lock file is never deleted (the claim lives in the
  exclusive open, not in the file's existence).
- A second start never touches Avalonia. Its argument vector walks the
  allowlist grammar first - the two flags in both value forms plus the
  `zetta://open` protocol strings - and every position outside the
  allowlist (every order-shaped token included) is a hard rejection with
  its own reason, never a defaulted substitute. A well-shaped vector
  forwards through the bounded hand-off pipe (a framed 64 KB JSON
  exchange on one wire naming policy, one connected peer at a time, a
  bounded framing read) and the process exits promptly with the mapped
  code: 0 forwarded, 1 rejected, 2 the hand-off never served within the
  bounded window. The dispatch pipe's first server instance is created
  synchronously in `Start` - the moment it returns, the pipe exists, so a
  second start's first connect attempt never loses a race with the
  pump's warm-up.
- The running instance re-validates the whole vector on the receiver
  side: the exchange id resolves against the live adapter catalog and the
  symbol text parses through the real symbol parsers (the exchange's
  format when an exchange rides along, the normalized format otherwise) -
  a malformed value is a refusal receipt, never a substituted symbol. The
  application routes through the palette precedent: a same-exchange
  selection writes the chart's workspace symbol, a cross-exchange one
  writes the workspace symbol preference first and only then switches
  the active exchange, so the pending bootstrap preselects the pair. All
  applies serialize behind one gate.
- The dispatch receipts live in a dedicated rolling file log
  (`zettatrade-handoff-*.log`); nothing in the lane writes to stdout. The
  task ships zero user-facing strings, so no resx keys exist by design.
## [0.1.0-alpha.79] - 2026-11-09

Build of board task T-127 (branch `feat/testnet-trading`).

### Added
- The venue trading surface (T-127): the workspace's one `ITradingAdapter`
  delegates between the paper simulator and the Bybit testnet venue. The
  shipped default stays the paper lane bit-identical to before; the venue
  lane requires all three of the operator's deliberate choices TOGETHER —
  the toolbar's Bybit pick, the endpoint record's testnet flag and the
  `Exchange/TestnetTradingEnabled` toggle (default off) — so nothing ever
  trades a real venue without all three gates arming at once. A lane flip
  re-resolves live (the status-bar chip, the ticket and the panels follow
  without a restart).
- The Bybit v5 testnet trading adapter: signed REST (the v5 HMAC-SHA256
  header set) and the private order stream with the lazy connect (an idle
  surface holds no sockets), the 20 s heartbeat, and the auth+subscribe
  session flow re-run after every reconnect. LIMIT-GTC spot orders only —
  market, OCO, reduce-only and non-GTC families are refused BEFORE the
  venue (an error, never a degraded execution); a venue reject surfaces as
  an error under the rejected-not-default contract, and an unmappable row
  or status is refused with a logged reason, never defaulted.
- The full lifecycle on the journal: the strict v5 status map (submit →
  working → partial → fill/cancel/reject) and the venue order ledger —
  every private-stream transition upserts into the persistent order
  history in order, so the venue lifecycle lands in the history panel
  next to the paper rows. Credentials come ONLY from the secret store,
  fetched per operation — never a command line, never a config file,
  never a log.
- The TESTNET badge on the order ticket, the order history and the
  positions panel: it appears exactly while the traded surface rides the
  testnet lane (mirroring the status-bar chip), with the `Exchange.*`
  label + tooltip keys in BOTH languages.


## [0.1.0-alpha.78] - 2026-11-09

Build of board task T-121 (branch `feat/heatmap`).

### Added
- The market heatmap panel (T-121): the whole-venue view as a second right-rail
  tab. The panel's feed fetches the adapter's tradable universe once through
  the active-exchange facade, caps it to a bounded candidate window (48
  one-ticker pumps — a large venue's catalog never blows the subscription
  count), and coalesces the candidates' latest tick state into ONE ranked
  snapshot per panel pulse: a wire ticker storm stays inside the service
  while the panel sees one settled page.
- The squarified treemap drawn straight onto the Skia canvas (the
  order-book widget's pattern — no intermediate bitmaps, no per-tile
  controls): tile area maps to the rank weight (the quote volume default:
  the last price times the 24h quote turnover), tile color maps to the 24h
  change through the buy/sell palette — a small gain shades barely toward
  growth, the saturation ends at five percent so the palette never invents
  a swing the number does not carry, and it never flips a sign. The same
  (tiles, plot) input returns the same output on every call, so a re-plan
  behind every storm stays deterministic. Unpriced pairs own no area — an
  empty page renders the honest empty state, nothing fabricated for missing
  data.
- The metric switch: the `Heatmap.*` localized labels (title, empty-state
  message, the two metric items) in BOTH languages; the switch appears only
  when ranked data exists (a switch over nothing is fabricated chrome).
- A tile press routes its pair into the workspace symbol — the same write
  the palette's same-exchange apply maintains; an unparsable tile text is
  refused without a substitute.


## [0.1.0-alpha.77] - 2026-11-09

Build of board task T-122 (branch `feat/chart-mine`).

### Added
- The chart's trading footprint (T-122): my own trading activity draws ON
  the candles - the position's average-entry line with its direction
  arrow (the stance the signed quantity carries), the floating unrealized
  PnL tag snapped to the latest observed price (absent until a price has
  been observed - never invented), the working limit orders as
  side-colored dashed price lines (a stop or trailing leg carries no
  standing level and owns no line - an honest absence) and one diamond
  per executed fill anchored at the candle the fill time fell into
  ([open, next open) containment, the time axis' own conventions;
  behind the last displayed interval nothing anchors - the candle is
  forming). Every placement rides the candles' own shared price scale
  and bar-slot mapping - an overlay never re-scales the axis and never
  paints outside the plot (a level that left the window resolves to no
  line).
- The `ChartFootprintService` feed: the trading lane's ONE keyed
  snapshot of its own activity - the entries, the dashed working lines
  and the journal-of-record fills - published whole on every change so
  the chart's workspace symbol picks its own shapes; read-only
  bookkeeping end to end (nothing routes back toward an order); the
  refresh economy: a price-only update recomposes from the caches, a
  shaped change or a ledger/placement trigger pulls the adapter once
  through the one drain loop.
- The chart legend's footprint rows: exactly the shapes the current
  frame drew (a hidden level explains nothing), the entry row naming
  the direction it drew, the five `Chart.Footprint.Legend.*` keys in
  BOTH languages.


## [0.1.0-alpha.76] - 2026-11-09

Build of board task T-123 (branch `feat/trading-mode-chip`).

### Added
- The trading-mode chip (T-123): the status bar always shows which lane
  the workspace's traded surface occupies — PAPER/TESTNET/LIVE — as a
  pill next to the active-exchange name. The lane resolves over the
  T-015 endpoint identity: the shipped paper simulator parks the chip
  on the paper lane, a venue surface classifies by the configured
  endpoint record's testnet flag, and an identity outside the endpoint
  registry snapshot is an error, never a substituted lane. The pill
  re-tints live on theme switches (the palette keys the T-098 health
  dots already speak) and its caption/tooltip re-compose on language
  switches.
- The `TradingModeService`: the lane resolver and the standing
  `IObservable<TradingLane>` stream future per-window chips attach to.
  The go-live invariant lives here: a flip INTO the live lane must ride
  an `IConfirmationService` prompt (`Trading.ConfirmGoLive` in both
  languages) — the runtime flip call site arrives with the
  venue-trading surface, so today the gate is the invariant's keeper.

## [0.1.0-alpha.74] - 2026-11-08

Build of board task T-120 (branch `feat/watchlist`).

### Added
- The streaming watchlist panel (T-120): a tape-neighbor column listing
  the majors of the active exchange with each row's adaptive-precision
  last price, the signed 24h change and the compact 24h volume, plus a
  sparkline mapped from the row's own 32-price ring. The workspace
  selection's symbol joins the row set live and the previous
  selection-only row retires; rows keep stable identities, so each pulse
  snapshot churns fields in place and never rebuilds the collection for
  data.
- The `WatchlistFeedService`: an `IObservable` wrapper over the
  active-exchange facade's ticker streams — the per-row price pumps stay
  inside the service while ONE coalesced snapshot ships at the panel
  cadence over the 500 ms pulse, so a wire quote storm cannot storm the
  UI. An exchange switch re-arms the pumps (the facade's switch), the
  first pulse ships the row set before any tick with unpriced rows
  binding empty texts, and the persisted column width rides the
  book/tape splitter discipline (`UI.WatchlistPanelWidth`; the debounced
  drag persistence, live external writers, `Watch.Title` in both
  languages). The pulse seam keeps the feed facts deterministic; the
  panel activation itself rides the pre-merge launch smoke.


## [0.1.0-alpha.73] - 2026-11-08

Build of board task T-137 (branch `fix/startup-di`).

### Fixed
- The startup crash (T-137): the app died before any window existed
  since the alpha.68 landing — the T-100 settings port activates the
  concrete store's batch/snapshot members while the composition root
  registered the settings store only as the `ISettingsService`
  interface, so resolving `SettingsPortService` threw
  `InvalidOperationException` and the process exited with the CLR
  unhandled-exception exit code before the main window appeared.
- The store now registers ONCE by its concrete type and forwards the
  same instance as `ISettingsService` (the new
  `AddSettingsStoreServices` module extension), so interface consumers
  (the app walk, chat, workspace layout) and the port's concrete
  consumers share one store with one state; the registration facts pin
  the shared identity and the port's activation over the real graph
  shell, and an app-launch check proves the composition walk completes.


## [0.1.0-alpha.72] - 2026-11-08

Build of board task T-101 (branch `feat/log-viewer`).

### Added
- The in-app log viewer (T-101): the window reads the existing Serilog
  file sinks the way a user does — every byte chunk rides the secret
  redactor gate before the parser, so no raw credential can reach the
  ring, the surface or the export — and a bounded history ring with a
  drop-oldest backpressure channel keeps a log flood's memory footprint
  fixed while the live tail stays smooth.
- The viewer's attach/seed discipline: activation attaches the live
  slice lane first and seeds from the ring's history snapshot behind it
  with value-identity dedupe, so slices crossing the open render exactly
  once; the surface is a fixed-capacity virtualized list applying one
  multi-element collection event per drained slice (tens of thousands of
  lines never storm the list), and eviction mirrors onto the head.
- Level and source filters rebuild the surface synchronously out of the
  ring's filtered projection (never a disk read), the line counter keeps
  the honest tally, and the bounded excerpt export writes the matching
  slice to a user-picked text file (a cancelled pick exports nothing; a
  failed write surfaces on the error line).
- The global crash guard (T-101): the runtime's
  `AppDomain.UnhandledException` leg and the Avalonia UI-thread
  `Dispatcher.UnhandledException` leg both log FIRST, then present the
  one crash dialog — a last-inform hook pair that never suppresses, never
  exits and composes nothing but the exception's type, its bounded
  single-line summary and the raw log path the user acts on without the
  app running.
- Localization for the viewer, the crash dialog and their states
  (`Log.*` keys, both English and Russian catalogs kept at full parity).

## [0.1.0-alpha.71] - 2026-11-08

Build of board task T-096 (branch `feat/price-alerts`).

### Added
- The price-alerts engine (T-096): a session-singleton roster owner over
  the reactive settings service — create, edit re-arm, snooze and delete
  are the roster's only writes, serialized behind one single-flight gate;
  the evaluation is purely stream-driven (one ticker subscription per
  evaluatable symbol, per-observation serialization through a channel);
  Price edges are exact (strictly-above/below, exact-at, no epsilon), the
  percent move compares the window's oldest in-window reference by
  exact-or-greater, the fire is persistent with the tick's own stamp, the
  snooze re-arms on the next arriving tick (no wake timers), a failed beep
  degrades into a logged warning, and a reloaded fired roster never fires
  again. Edits are validated drafts (a garbage spelling, an unparsable
  price or an unseen kind refuse each with their own error, never a
  silently coerced alert).
- The alerts manager window (T-096): the modal over the menu route (the
  interaction pattern like the stats window), a transient per-open view
  model rendering the engine's roster (symbol, condition, status, last
  fired, note), the editor (kind/edge/target or percent/window/note), the
  refusal line, and live re-renders for engine events and language
  switches (the attach handle dies with the window).
- Localized `Alert.*` strings en+ru (33 keys + the `Menu.Trading.Alerts`
  item), validated by the resx invariant gate.
- Tests: the edge matrix (strict above/below, exact-at, the percent
  boundary), the codec's round-trip and exact-or-skip persistence facts,
  the engine's end-to-end stream facts (no-fire then fire pairs, snooze
  re-arms stream-side, reload never re-fires, the sound degrade, refused
  garbage), and the manager view model (refusals, valid drafts, selection,
  the language re-render, the attach handle's dispose).

## [0.1.0-alpha.70] - 2026-11-08

Build of board task T-113 (branch `feat/backtest`).

### Added
- The offline deterministic backtest harness (T-113): a run pages the
  newest cached candles for its series and replays them through the real
  T-111 strategy seam; stance changes execute as paper-parity fills
  (weighted-average entries, BUG-034 flip semantics, fee-registry
  commissions) and the run folds an equity report with the fill/trade
  statistics and the byte-stable canonical report text. Entirely offline
  - no adapters, no rate limiter - the same seed reproduces the curve
  byte-for-byte, and a cancelled run produces no report.
- The Backtest window: a fresh run form per open (strategy picker over
  the catalog, symbol, interval, bars, seed, slippage, long sizing
  fraction - every input refused-not-defaulted), live progress over the
  Started/Progress/Completed events, cooperative cancel, and status
  lines that re-resolve live on language switch.

## [0.1.0-alpha.69] - 2026-11-08

Build of board task T-112 (branch `feat/signal-markers`).

### Added
- Strategy-signal markers on the chart (T-112): every stamped reading of
  the T-111 seam anchors a small triangle at its own candle's close level
  (long above, flat below) through the render pass's shared slot and
  price mapping — placement is exact by construction, a marker whose
  candle left the series disappears with it, and two readings on one
  candle stack on the same slot.
- The signal rail panel under the chart: the newest readings stream live
  as a bounded, coalesced ring — one rows replacement per coalesce
  window, newest first, the oldest evicted past the shipped count — and
  a row press prefills the order ticket (symbol, side, the reading
  candle's close) through the same transient dialog path the order book
  level presses share. A signal never sends an order; the human decides.
- The seam's deterministic hosting leg: the chart's delivered full
  series loads feed the strategy host through a composition-owned
  relay (the chart and the service never hold each other), candles
  enter the pump exactly once per series and only when provably closed
  (the in-flight candle is never judged), and the per-series stance
  registry prunes to the delivered window's floor.

### Tests
- The compose and resolve placement goldens (the exact per-candle anchor,
  the off-window drop, the same-slot stack), the hosting leg's
  closed-candle gate, exactly-once re-coverage drop and floor prune facts
  with the demo strategy, and the rail's coalescing, bounded ring,
  neutral-absence and prefill facts.

## [0.1.0-alpha.68] - 2026-11-08

Build of board task T-100 (branch `feat/settings-port`).

### Added
- The settings port (T-100): export of the user-adjustable settings subset
  as a versioned JSON file — registry-owned keys only, both levels sorted
  and a fixed root shape, so two exports of one state are byte-identical
  and a restore round-trips byte-stable. Secrets are structurally absent:
  the port takes no secret-store dependency, so no credential can ever
  reach an exported file.
- The import lane: schema validation (the port's own marker, the exactly-
  shipped version, an allowlist of known settings keys and a stored-shape
  kind per key) rejects a foreign file whole — nothing coerced, nothing
  defaulted — and a passed draft becomes the dry-run diff preview the user
  looks at before the apply unlocks.
- The apply rides one locked, single-write batch on the settings service:
  snapshot plus keyed mutations publish exactly one reactive change per
  real difference, and every subscriber that already reacts to settings
  changes re-applies live — no restart needed after an import.
- The factory reset stamps every registry-owned key back to its shipped
  fresh-install default (or removes the absence-natural ones) behind the
  typed human confirmation; a decline writes nothing, and the secrets lane
  plus foreign tuning keys always survive.

## [0.1.0-alpha.67] - 2026-11-08

Build of board task T-111 (branch `feat/strategies`).

### Added
- The strategy plugin seam (T-111): a minimal contracts kit — an
  IStrategy evaluates a read-only closed-candle window per (symbol,
  interval) and answers one neutral/long/flat stance at the last closed
  candle, the StrategyContext carries the series identity, and the host
  stamps every evaluation into a StrategySignal naming the strategy,
  symbol, interval and the exact candle it judged.
- The host + loader in the new ZettaTrade.Strategies project: one
  candle-close evaluation loop consuming final candles and emitting one
  stamped signal per (strategy, series) per accepted candle into an
  inert signals channel — the output is read-only events for the human
  and the UI tasks ahead; nothing rides a signal to an order, to the
  risk lane or to any trading adapter, the human decides. The loader
  scans the strategies plugins directory once per session reusing the
  indicator scanner's T-054 tolerance canon: a broken, missing-
  dependency or empty-id plugin is skipped with a loud warning while the
  healthy neighbors keep registering.
- The demo golden-cross strategy plus its seeded-candle golden, the
  conformance battery every implementation must clear, and the loader
  matrix pinning the T-054 regression: a broken/unloadable plugin
  degrades the scan, never the app.

## [0.1.0-alpha.66] - 2026-11-08

Build of board task T-099 (branch `feat/first-run-wizard`).

### Added
- The first-run wizard (T-099): a one-announcement, three-step onboarding
  over the same reactive settings lanes the app already applies — the theme
  pick, the exchange adapter pick and the first workspace symbol pick. The
  launch shows it once per install: a start whose announcement flag is
  unset (a fresh install, or an externally reset settings file) opens the
  wizard after the first-run disclaimer wording, and ANY exit — finishing,
  skipping or a plain window close — marks the persisted flag, so the next
  launch lands on the configured workspace without a repeat announcement. A
  failure to show the wizard is logged and non-fatal: the flag stays unset
  and the next launch shows it instead (never a half-configured boot).
- The step walk: one body area, the visible step swaps and the
  back/next/finish row stays in place, clamped at both ends. Every pick is
  a draft — the drafts seed from the applied state (the applied theme, the
  active adapter; an unresolvable seed stays null rather than substituting
  a fabricated default) and survive walking back and forth. Nothing applies
  before the finish button's task lands, and the finish validates ALL
  three picks BEFORE the first write, so an invalid pick is a refusal with
  the pre-wizard state untouched — a failed finish keeps the wizard open
  with the drafts intact (a retry or the skip affordance stays honest), and
  the window closes only on a successful finish.
- The demo lane as the shipped default: the active-exchange facade's
  registration now leads with the canned-data demo adapter, so a fresh
  install (the wizard's exact audience) works on a simulator that never
  touches real funds, and existing installs restore their own persisted
  exchange regardless of the order. Skipping (the button and the system
  close share one path) is a plain window close — no write at all, so the
  skipped wizard leaves that demo lane standing and a fully working
  workspace; the announcement flag still marks (the wizard announced
  itself exactly once).
- The re-run entry in the settings window's appearance tab ("Run setup
  wizard…"): the settings window closes itself and the wizard shows
  modally over the main window, a re-run never touching the announcement
  flag. Secrets never ride the wizard — it collects no keys at all, the
  adapters reach theirs exclusively through the per-machine secret store
  (never a command-line argument), and the adapter step's note says as
  much in the wizard itself. Wizard strings ship in en and ru.

## [0.1.0-alpha.65] - 2026-11-08

Build of board task T-114 (branch `feat/ai-context`).

### Added
- Market context injection into the chat (T-114): the assistant sees the
  now. One localized consent chip above the chat input (`Market snapshot`),
  persisted per workspace in the `UI.ChatMarketContext` key and restored
  by the startup walk; with consent off the chat ships exactly the prompt
  it shipped before, and every enabled send appends the compact market
  snapshot right behind the workspace head, so the model reads the live
  facts as instruction context.
- The snapshot is compaction, not a dump. `MarketMoment` (ZettaTrade.AI)
  carries the venue's paper/live classification handed over as a decided
  fact, the latest ticker price and 24h change, the top three book levels
  per side (best first) and the enabled indicator setup;
  `MarketContextPreambleComposer` renders the moment deterministically
  (the same snapshot byte for byte, InvariantCulture spellings) into at
  most 220 tokens under a fixed priority: the venue tag survives any
  budget, an over-budget draft is shaved by dropping whole low-priority
  parts (indicators, then the book sides, then the 24h change) and never
  by re-wording or inventing a value, and an all-absent workspace renders
  the honest "no live market data received yet" line.
- `ChatContextService` follows the workspace as before and now also binds
  the order book feed on every symbol switch (through the active-exchange
  facade, so an exchange switch re-attaches it) and tracks the enabled
  indicator setup from the persisted settings (read once at
  initialization, re-applied live from the changes stream); a part the
  streams never carried stays absent instead of being substituted.


## [0.1.0-alpha.64] - 2026-11-08

Build of board task T-089 (branch `feat/chart-types`).

### Added
- The chart type selector (T-089) on the chart toolbar: five localized
  choices (Candles, Line, Area, OHLC bars, Heikin-Ashi) all riding the same
  candle pipeline — renderer variants off the existing candle source, never
  a second data path. The line and area variants draw the close polyline
  (the area fills translucently under it), the OHLC bars draw the high–low
  stem with the open/close ticks, and Heikin-Ashi renders the client-side
  smoothed series computed once per delivered series (never per draw
  pass), aligned with the loaded series by index: the bar slots, the
  volume-at-price histogram, the indicator and VWAP overlays and the hover
  readout keep their semantics, and the overlays always consume the raw
  window, never the renderer variant.
- The price scale follows the drawn series per type (the line and area
  variants spread over closes only — the wicks are not drawn geometry), so
  nothing a frame paints can clip the plot; the axis-bucket, pan and
  paging invariants stand across every variant.
- The choice persists per workspace symbol in the `UI.ChartSymbolTypes`
  map: every flip serializes the whole map under the addressed symbol,
  every symbol switch and the startup walk restore the stored choice
  without rewriting it (a restore is a read, never an edit), a symbol
  without an entry keeps the standing type, a flip with no symbol
  addressed stays transient until a context exists, and external
  settings-file writes apply live without a restart.

## [0.1.0-alpha.63] - 2026-11-08

Build of board task T-097 (branch `feat/notif-center`).

### Added
- The notification center (T-097): one publish surface, two read lanes.
  Every journaled terminal order outcome (fills, accepted cancellations,
  rejections) rides the center through the journal tap exactly once per
  journal write, the adapter health fold's transport flips (reconnecting,
  down, recovered) announce through their own tap, and the raw lane streams
  every event while the display lane coalesces bursts of it into one digest
  per source per 2-second window: the digest carries the burst size as a
  count badge and the newest event's facts as the headline.
- The toast host: a top-right stack of chrome-styled cards over the
  workspace, newest on top, at most five visible, every card leaving on its
  own after five seconds or on a click. All timing lives on the publishing
  threads; the host only marshals the writes onto the user-interface thread
  (the overlay never blocks it). A click dismisses the toast and routes
  through the tested route map: fills open the trade statistics window,
  cancellations and rejections the order journal, the Do-Not-Disturb
  summary the notification itself; an adapter state keeps no deeper panel
  (the health strip is always visible, so the click only dismisses).
- Do Not Disturb and per-source mutes ride the reactive settings service:
  four mute toggles plus the DND flag persist in the Notifications
  settings section, subscribe-time seeds plus live re-application mean an
  external settings-file edit lands without a restart. While DND runs,
  toastable events skip the popups but keep their history rows, and the
  flip back to off emits exactly one system summary counting the span's
  missed events. A muted source is silenced for popups only — its events
  keep reaching history.
- The notification history window (the Trading menu's "Notification
  center" entry): the session ring (200 entries, newest first) as live
  rows, the Do-Not-Disturb checkbox and the four mute checkboxes mirroring
  the reactive state read-only (their toggles write), a persist fault
  surfaces on the footer's error line without losing the flagged state, a
  Clear-history button and the empty state; window chrome per the T-066
  canon, strings in en and ru.
- Source taps keep the journal honest: a cancellation's symbol recovers
  from the journal of record, an outcome the journal never saw (a
  pre-acceptance venue refusal) publishes nothing, and a persistence fault
  announces nothing (nothing is fabricated). The demo lane's unsupervised
  adapters stay silent (no transport fact exists to report).

## [0.1.0-alpha.62] - 2026-11-08

Build of board task T-093 (branch `feat/vol-vwap`).

### Added
- The session-anchored VWAP overlay (T-093) over the candles: the center is
  the volume-weighted average of the session's typical prices (H+L+C)/3
  with the bar volumes as weights, flanked by the ±1σ bands (the
  volume-weighted standard deviation of the same typical price).
- The profile overlay with the volume-at-price histogram (T-093) in the
  chart's right margin: an even bucket ladder over the visible window's
  price spread, every bar's volume riding the bucket that holds its
  typical price (the same weighted price the VWAP weights). The bars hug
  the plot's right edge, translated from the max-volume bucket.

### Changed
- The VWAP sessions break at every UTC calendar-day boundary
  (`VwapSessionAnchor`): crypto venues trade around the clock, so the UTC
  day is the only venue-honest anchor their series carries; the
  stock-style venues' weekday sessions break on the same rule because
  their quiet days carry no bars at all, and a series starting
  mid-session anchors the accumulation at its own first bar.
- Both overlays carry a config section (`UI.ChartShowVwap` /
  `UI.ChartShowVolumeProfile`, both default on) and a toolbar toggle next
  to the time-frame picker with localized tooltips (`Chart.Vwap.Tip` /
  `Chart.VolumeProfile.Tip`, en+ru); a toggle persists through the
  reactive settings pipeline and survives a restart.
- The honest no-volume state: when the loaded candles carry no volume at
  all the overlays draw nothing and the chart prints the localized
  `Chart.NoVolumeData` note (en+ru) instead of fabricating zeros or
  spread shapes.

## [0.1.0-alpha.61] - 2026-11-08

Build of board task T-095 (branch `feat/symbol-palette`).

### Added
- The global symbol palette (T-095): Ctrl+K everywhere opens a
  workspace-wide instrument picker over ALL registered exchange
  adapters at once, not just the active one - a few keystrokes reach
  any configured adapter's symbol. The picker is keyboard-first:
  Ctrl+K toggles the overlay, the query box takes the focus, the
  arrow/PageUp/PageDown keys move the keyboard selection, Enter
  applies the highlighted pair, Esc closes, and a press on the
  overlay's backdrop closes it for mouse users.
- The palette's fuzzy matcher (`FuzzySymbolMatcher`) ranks the
  adapters' symbol catalogs through one pure, culture-free scorer:
  an exact (leading) prefix beats a word-start contiguous hit, then
  an anywhere-contiguous hit, an in-order subsequence and a
  one-typo-tolerant subsequence (one skipped query character at
  most). Inside a tier the ranking prefers query coverage, a
  first match on a word boundary and fewer skipped characters
  between matches; ties keep the source order. A blank query pages
  the session's recents first, then the favorites, then the
  adapters' natural registration order - every page capped at 24
  rows.
- The instrument source (`SymbolPaletteSource`) keeps ONE behavioral
  snapshot over all registered adapters: the warm-up rides the
  startup walk (`IAsyncInitializable`, non-blocking), every adapter
  change kicks an epoch-guarded recomposition, and a faulting
  adapter contributes zero rows and logs - the healthy adapters'
  rows still serve, nothing is fabricated (the no-defaults rule).
- The palette's rows carry the per-pair marks: the star mirrors the
  user's favorites (read-only; the marks are made from the chart's
  toolbar, the palette never writes them) and the recency marker
  rides the palette's own in-memory session recents (deduped,
  bounded at 8, never persisted).
- A same-exchange apply writes `ChartViewModel.SelectedSymbol`
  directly; a cross-exchange apply carries the applied pair into the
  SAME workspace symbol preference the chart's setter maintains
  BEFORE switching the active exchange, so the pending symbol
  bootstrap preselects the applied pair - the delivered selection
  raises while its exchange is already the current one and no
  intermediate rebase can replace the applied choice with the
  previous exchange's head (the T-017 selected-during-fetch
  discipline).
- The overlay rides the chrome canon (Surface border + Border stroke
  on the theme), carries three honest rowless states (loading /
  no adapter serves a symbol list / no match for the query) and new
  localized keys, en and ru (`Palette.*`).

 Changelog

All notable changes to ZettaTrade are documented here, newest first
(Keep a Changelog format).

While the app is in alpha, one release == one merged board task: task
`T-0NN` ships as `0.1.0-alpha.NN` and adds its section below. The state
before `alpha.10` was never versioned — what shipped in T-001 — T-009
(app shell, themes + hot reload, settings, hotkeys, resx sync gate, order
dialog, positions panel) is recorded in `docs/TASKS.md`.

## [0.1.0-alpha.60] - 2026-11-07

Build of board task T-092 (branch `feat/indicator-pack`).

### Added
- The classic indicator family ships as separate bundled plugin assemblies
  (T-092): EMA, WMA, RSI, MACD, ATR and Bollinger join the SMA sample in
  the plugins-directory scan - every one is its own assembly implementing
  the same single-series `IIndicator` seam (T-034), resolved from the
  plugins folder at runtime while the application carries no
  compile-time reference to any of them. The cold-start charter is a
  pinned fact: the production graph references zero plugin assemblies,
  an unloaded pack costs nothing, and the tolerant scan keeps skipping
  broken or non-plugin neighbors without tainting the healthy ones.
- The formulas follow the reference conventions, each pinned by
  hand-computed golden vectors over fixed series: EMA seeds with the
  plain mean of the first N closes and walks alpha = 2/(N+1) per bar;
  WMA weights the newest close heaviest (the ladder N..1 over the weight
  sum N*(N+1)/2); RSI runs Wilder's smoothing with the zero-loss
  shortcut; MACD renders the fast/slow EMA spread through the classic
  seeded convention (the row's period knob drives the fast length while
  the trio pins the canonical 26/9); ATR seeds Wilder's smoothed true
  ranges (the opening bar's plain range stands in for the missing prior
  close); Bollinger renders the upper band - the middle SMA plus k
  population-deviation sigmas, the deviation count pinned at the classic
  2. A construction an implementation rejects throws (the catalog skips
  the overlay row, then nothing defaults silently): an out-of-range
  period anywhere, a MACD fast length at or beyond the slow one, a
  non-positive Bollinger deviation count.
- The settings surface registers the whole pack under its own module rule
  (T-092): the indicators tab renders every pack row through the same
  loaded-plugins gate the SMA row established (T-067) - a row appears
  only when the scanned catalog actually registers its canonical name,
  the single period knob saves through the same draft contract (a real
  difference patches its own row into the persisted array, every other
  row rides verbatim, an unparsable or unchanged draft stays silent, a
  plugin the session does not have never gains a config row) and the tab
  still lands as one settings write. A workspace now picks indicators,
  the pack registers cleanly.
- New localized keys, en and ru (`Chart.Indicators.*`): one honest
  tooltip per pack row summarizing its formula - the checkbox labels stay
  the plugins' canonical names, product names rather than localized
  strings.
## [0.1.0-alpha.59] - 2026-11-07

Build of board task T-106 (branch `feat/rate-limiter`).

### Added
- The REST rate limiter budgets the exchange lanes (T-106): every
  outbound REST call of the Binance and Bybit adapters acquires its
  budget before the send through a per-endpoint token bucket
  (`IRestRateLimiter`), so the T-086 deep-history backfill borrows only
  a capped slice of the venue flow while a user submit is served from
  the main pool — a background burst can never starve or delay a user
  action. The lanes are classified adapter-side from the request shape
  (`RestLaneResolver`): a bar range ending at (or within the two-minute
  grace of) the present rides the user lane, a past-ended range — the
  gap walk — rides the background lane, and ambiguity always defaults
  to the user lane so background never gains priority by error.
- Each adapter ships its budget manifest as a static default: Binance
  meters weight-per-minute (klines 3600 + symbol pricing 2400 of the
  venue's 6000-per-minute allowance, the kline weight following the
  venue's tier schedule by limit window — heavier slices over-estimate
  upward, the safe metering direction), Bybit meters each public market
  endpoint family at ten requests per second. Both are rescalable live
  through the `Limiter.*` settings keys (`Enabled`, `BackgroundShare`,
  `Binance.RequestWeightMinute`, `Bybit.RequestsPerSecond`: the keys
  hydrate once before the first acquire and are then followed
  reactively, so a settings edit reshapes the buckets without a
  restart; absent keys leave the shipped defaults standing).
- The 429 path is honest end to end: a rate-limited response reports
  the venue's `Retry-After` into the limiter before the failure throws
  exactly as any transport failure did, every lane parks for the span
  (the venue's own accountant restarts the window — the expiry refills
  both pools to full), a missing or nonsensical guidance applies the
  limiter's short default block span (the block is real, only the
  guidance is missing), and a park or token deficit longer than the
  ten-minute bounded-wait ceiling surfaces as an error — the REST wait
  is always bounded, never an infinite stall.
- The demo and paper adapters stay exempt by design: they are synthetic
  surfaces with no venue and no IP budget, so there is nothing for a
  limiter to police — the throttling is a production posture applied by
  the DI seam (`AddRestRateLimiters`: one keyed singleton limiter per
  adapter under the adapter's own HttpClient identity), and unwired
  adapter constructions (tests, plugin hosts) run through a
  pass-through limiter instead of failing.

## [0.1.0-alpha.58] - 2026-11-07

Build of board task T-098 (branch `feat/health-strip`).

### Added
- The status bar shows adapter health (T-098): a dot per registered
  exchange adapter rides the status row — green
  (`CandleUp`) when the adapter's supervised transport streams, gold
  (`Highlight`) while it cycles through a reconnect (the initial connect
  loop, the backoff wait or the T-105 watchdog force-cycle), red
  (`Error`) when no session is open and a neutral gray (`TextSecondary`)
  when the adapter carries no supervised transport at all (the demo's
  canned-data lane). The fills are palette-key `DynamicResource`s, so a
  theme switch or hot reload retints the dots live; hovering any dot
  shows its adapter's localized health prose — name, state, the moment
  of the last transport event and the consecutive-failure counter.
- The health fold is a pure observable composition (`AdapterHealthService`):
  the active-exchange facade's adapter catalog folds with the reconnect
  supervisor's per-adapter state stream (no polling loop, no timer — the
  supervisor's already-pumped stream is the only input, and a late
  subscriber bootstraps with the supervisor's current snapshot). A state
  out of the strip's vocabulary errors the fold instead of a substituted
  default, and the definition's market-closed state waits for the
  calendar work — the T-086 timeframe cadence carries no per-adapter
  venue-session query, so nothing renders it yet.
- New localized keys, en and ru (`Status.Health.*`): the reconnecting
  label (the connected/disconnected pair reuses the existing
  `Status.Connected` / `Status.Disconnected` keys), the
  no-transport-supervision label and the two tooltip templates.

## [0.1.0-alpha.57] - 2026-11-07

Build of board task T-088 (branch `feat/depth-chart`).

### Added
- The cumulative shape of the book lands beside the ladder (T-088): a
  `DepthChartWidget` Skia renderer stacks into the book column — the bid
  side's area grows downward from the shared mid line band by band, the
  ask side mirrors upward, and each band's fill width carries its
  level's cumulative share of the side's visible total (exactly the
  fractions the ladder's cumulative bars draw: the band-for-band
  agreement is a pinned fact). The mid price prints on the line, the
  sides tint with the candle palette at the ladder bar's alpha, and an
  empty served book falls back to the localized no-data note. No new
  transport: the widget binds the same `OrderBookViewModel.Book`
  snapshot property the ladder binds; no new services, no composition
  edits.
- The served-vs-displayed depth discipline is the T-072 contract reused:
  the pure `DepthChartLayout` consumes only the book it is handed and
  cuts the displayed depth to the panel's pixels — one band per served
  level until the band budget the height can show runs out, never a
  separate re-request (a deeper served book never reaches the plan; a
  book shallower than the budget ends mid-panel, never stretched to fill
  it). The ladder and the depth chart planning over the same snapshot
  agree on the same resolved depth, pinned by a cross-component fact
  that plans both against one book and compares the cumulative
  fractions level for level.
- Frame discipline per the house charts (the T-011 pattern and the
  T-087 refinements): theme and resource colors snapshot per render with
  dark fallbacks, theme and language switches re-render live through
  subscriptions disposed on detach, the per-frame work is bounded by the
  visible bands, and the fills' geometry is precomputed in the plan so
  the draw operation allocates paints and the two staircase paths once
  per frame. The 60-frame burst fact pins the median and the mean
  inside the 16 ms budget with 1000+ levels per side (the worst frame
  stays observation-only output). New localized key (`OrderBook.DepthTitle`)
  in en and ru: the depth panel's caption row, styled like its sibling
  titles.

## [0.1.0-alpha.56] - 2026-11-07

Build of board task T-087 (branch `feat/trade-tape`).

### Added
- The trade tape (time & sales) lands beside the order book (T-087):
  the newest trades of the workspace symbol stream through a
  `TradeTapeWidget` Skia renderer — time / price / size columns, the
  aggressor side colored with the candle palette (buys bullish, sells
  bearish), the newest trade on top. The trades ride the existing
  `IMarketDataAdapter.SubscribeTrades` seam (Binance, Bybit and the
  demo adapter already publish it; Core is untouched). The panel
  ViewModel mirrors the book discipline — an application-lifetime
  singleton with a free-typed offline symbol input owned by the
  workspace combo, Rx Switch cancelling the previous symbol stream and
  a per-symbol failure guard (logged, never a substituted state).
- The tape's 60 fps discipline is ring-buffered: a fixed 4096-trade
  `TradeTapeRingBuffer` evicts the oldest trade on overflow, so memory
  stays bounded however long the burst runs; streaming trades queue
  through a `ConcurrentQueue` the render path drains on the
  user-interface thread (the only cross-thread seam), invalidations
  coalesce behind an `Interlocked` flag (one post per frame at any
  burst rate) and the pure `TradeTapePageLayout` plans, copies and
  formats only the rows the panel height can show — the 60-frame
  median and mean stay inside the 16 ms budget with 1000 trades
  buffered (pinned facts for the layout and for one ring-frame at the
  1000 trades/s load). The bounding invariant is also pinned as a bug
  guard: a partial copy serves the newest end of the ring, never the
  stale end.
- Tape UX: the pointer pauses the tape on hover (the page freezes, the
  localized hint replaces the streaming silence, new trades stay
  buffered out of the visible rows), the panel's clear button empties
  it, and a switched symbol clears the previous symbol's rows — an
  old tape never lingers above the new symbol. The workspace gains a
  resizable tape column with the T-038 splitter persistence: the
  `UI.TapePanelWidth` restore lands before any window opens, a settled
  drag persists once per burst and external writers apply live.
- New localized keys, en and ru (`Tape.*`): the panel title, the
  no-trades state, the paused hint, the clear label and the time /
  price / size column headers.

## [0.1.0-alpha.55] - 2026-11-07

Build of board task T-105 (branch `feat/reconnect-policy`).

### Added
- The reconnect policy goes explicit and observable (T-105): a
  `ReconnectPolicy` value object carries every timing knob the
  previously implicit fixed five-second `WebSocketClient` delay hid —
  the initial delay (the shipped first retry keeps the T-052 spacing so
  an existing burst behaves identically on attempt one), an exponential
  backoff multiplier, a bounded ±25 % jitter against thundering-herd
  reconnects (samples drawn at the compute site, the computation itself
  pure and deterministic), a hard max-delay cap the jitter can never
  push past (the re-clamp pins it), and a per-session failed-handshake
  attempt budget the fresh `ConnectAsync` resets (BUG-029 keeps its
  semantics; the background reconnect loop stays unbounded). The
  constructor guards fail loudly — a bad knob is an error, never a
  substituted default. The central `ReconnectSupervisor` binds one
  adapter's policy via `For(id, policy)` and folds the transports'
  reported transitions into per-adapter `AdapterConnectionState`
  snapshots (phase, the consecutive-failure streak, the last
  transition, the live policy) plus a hot `StateStream` — the surface
  T-098's adapter health strip will consume; reports ride an unbounded
  `Channel` applied by ONE sequential pump task (a single-threaded
  fold, no concurrent-notification hazards; a throwing subscriber
  never kills the pump), and `IAsyncDisposable` reaps it. The transport
  reports Connecting/Connected/Reconnecting/Disconnected/ForceCycling
  transitions through an optional `IReconnectStateSink` — direct
  constructions without a supervisor keep reporting nothing (zero
  behavior drift on the happy path). The half-open socket heals at
  last: a per-session `SocketSilenceWatchdog` fires when an OPEN
  session that already exchanged at least one frame (the arming rule —
  a bare socket that never talks is never condemned) goes inbound-
  silent past the window; outbound frames only ever arm it (a
  half-open socket happily accepts sends into its TCP buffer, so
  outbound traffic can never refresh the silence), inbound frames both
  arm and refresh; liveness timestamps ride `Interlocked`/`Volatile`
  over bare longs (no lock, no semaphore). The violation action aborts
  the dying socket — the parked `ReceiveAsync` throws and the usual
  policy-computed reconnect path takes over (one watchdog per session,
  disposed before the reconnect step, the probe never races a socket
  swap). Wired end to end: `AddExchangeAdapters` registers the
  supervisor singleton and both exchange adapters hand their policy to
  the transport and register it with the supervisor when resolved.
  No resx (no user-facing surface yet; T-098 renders later).
## [0.1.0-alpha.54] - 2026-11-07

Build of board task T-086 (branch `feat/backfill`).

### Added
- The chart heals its own history (T-086): every delivered page starts
  a bounded repair chain — the pass probes the cached coverage strictly
  behind the window's head (the T-085 write-through continues behind
  the view), walks the venue calendar (`TimeFrameCalendar`: weekday
  sessions for the class, crypto trades both weekend days), detects
  the runs of expected-but-absent opens and fetches the fillable ones
  through the backfill fetcher: bounded 500-candle segments walked
  backwards, stitched by open time (no overlap, no duplicate), each
  raw page persisted write-before-emit, the served batches published
  through a bounded `Channel<T>` so a slow consumer backpressures the
  producer mid-pass. The detection bound
  (`CandleGapDetector.MaxMissingBarsPerGap`, 1200 opens) caps one
  pass's enumeration — a hole exactly at the bound is one whole
  bounded fetch; a wider one reports truncated and stays the paging
  machinery's own subject (a bounded repair never chews an open-ended
  hole; the batching discipline against the T-106 rate limiter). The
  chain's boundary is one `Interlocked`-claimed epoch per delivery (a
  rebase cancels and owns the living chain's epoch, a finishing chain
  can never destroy a newer one's), and contiguous cached bars only
  grow the detection context behind the head — the displayed series
  stays the delivered page's (the cache is context, never depth). The
  additions merge strictly additively (`BarSeriesMerge.UnionAscending`:
  every displayed bar survives, an equal open keeps the displayed
  side); a repair racing a rebase carries the stale page's stamp and
  drops. The feed's own depth edge surfaces honestly: an empty answer
  or a floor strictly above the window's lower edge ends the chain
  with the localized `Chart.HistoryLimit` note anchored at the merged
  series' oldest open (cleared with the rebase) — never a fabricated
  continuation. The older-than probe rides the candle cache seam
  (an additive `ICandleCache.GetOlderThanAsync` — bounded, oldest
  first, an empty answer is the honest no-coverage case). Fully
  localized en+ru (`Chart.HistoryLimit`).
## [0.1.0-alpha.53] - 2026-11-07

Build of board task T-078 (branch `feat/order-confirm`).

### Added
- The order ticket consults before every submit: the confirmation
  dialog (`IConfirmationService`) leads with the venue tag and the
  order essentials, then the honest economics — the estimated notional
  against the configured hard-cap headroom and the scheduled fee
  estimate (an unknown is a localized "unknown" line, never a
  fabricated zero). The decision is ONE `OrderConfirmPolicy` over the
  settings, read at submit time (no per-keystroke re-valuation, no
  subscription drift): a live place always asks even when the master
  switch is off, market orders always ask, paper defaults to no gate
  (`OrderConfirm.EnabledInPaper`), the notional threshold
  (`OrderConfirm.SkipBelowNotionalUsd`, a negative clamps to
  no-skip) skips small orders, an unknown notional fails to the ask,
  and a risk-mandated confirmation is never lowered by policy (the
  caller ORs it in). The notional anchor follows the stop family —
  stop-limit and trailing stop anchor on the stop price, everything
  else on the ticket price; the captured reference close substitutes
  only where no price exists across the OCO pair. Double-Enter
  protection moved under the whole pipeline: the submit handler
  (build → risk → confirmation → placement) rides one `SemaphoreSlim`
  gate acquired at the top and released in `finally`, so an Enter
  while the dialog stands open can neither re-confirm nor re-place
  (the parked-confirmation harness pins it). Fully localized en+ru
  (`Trading.Confirm*`).

## [0.1.0-alpha.52] - 2026-11-07

Build of board task T-079 (branch `feat/position-sizer`).

### Added
- Quick position sizing in the order ticket: ONE quick-size row under the
  quantity field proposes sizes and never touches an order - the proposal
  lands ONLY in the ticket's own quantity input, so the submit pipeline
  (invariant-culture parsing, validation, `IRiskManager`, confirmation)
  stays the one and only order path and no sizing shortcut exists. The
  presets spend the captured balance: 25/50/100 % of the base custody for
  sells (no price needed), of the quote custody converted at the entry
  estimate for buys (the ticket's own price when the order type carries
  one - limit/stop-limit/OCO take-profit - the captured market reference
  close otherwise, market/trailing stop). The risk-percent mode anchors
  on the ticket's stop context: a stop-limit's |limit − trigger|, a
  trailing stop's trail distance; market and plain limit carry no stop
  price and the OCO pair has two conflicting legs, so the mode stays
  hidden exactly there; the stored default percent is a reactive
  settings key (`PositionSizer.DefaultRiskPercent`, applied only onto
  the untouched field, live on change). Every proposal is floored onto
  the instrument's round lot step - a sizing can not carry more
  precision than the venue trades, and never overshoots the balance or
  the risk budget; a flooring below one step surfaces a localized state
  note instead of a plausible-but-wrong zero (rounding goldens tested).
  Zero network calls at sizing time: the balance snapshot and the
  reference close are captured at ticket open/symbol commit or via the
  row's deliberate refresh button only, sizing rides the captured
  moment, and the snapshot line displays its capture time - staleness
  shown plainly, never claimed fresh. The row hides itself entirely for
  instruments without round-size metadata: the paper surface declares
  its demo lot steps through an App-owned metadata seam
  (`IInstrumentMetadataProvider`); exchange-backed adapters stay hidden
  until a Core adapter-face contract (supervisor-gated follow-up) lands.
  Fully localized en+ru (`NewOrder.Sizer*`).

## [0.1.0-alpha.51] - 2026-11-07

Build of board task T-085 (branch `feat/candle-cache`).

### Added
- The local candle cache: adapter-served bars persist into SQLite
  (`candles.db`) keyed by (adapter, symbol, interval) and upserted by
  open time, so every fetch rides WRITE-THROUGH and the next start
  renders the chart instantly from the cache (the network refresh then
  lands behind it). Overlaps conflate — one TRUE database upsert decides
  insert-or-update per bar (the T-061 lesson), a restart replay or a
  racing surface merges on the composite key instead of colliding, and
  the served page commits as one transaction. The per-series eviction
  cap keeps only the newest `Caching.MaxBarsPerSeries` bars (default
  100k, the chart target's depth); the whole cache is switchable with
  `Caching.Enabled`, both keys re-read live on settings changes. A cache
  fault is bookkeeping noise: reads serve nothing and writes log — the
  chart's refresh page still delivers, the honest-view and paging
  machinery stay untouched. No Core entity or interface changed (the
  `ICandleCache` seam lives in Infrastructure next to the other
  persistence services); no resx changes.

## [0.1.0-alpha.50] - 2026-11-07

Build of board task T-133 (branch `feat/mcp-journal`).

### Added
- The MCP trading lane enters the same journals every panel reads: the
  tools' execution path now runs against a journaling wrapper around the
  paper adapter, so a tool-placed order lands in the session journal
  (the positions panel's recent orders) AND in the persistent SQLite
  ledger (order history, CSV exports, trade statistics and the T-082
  PnL realized history) exactly like a fill placed from the UI. The
  wrapper only records: every accepted placement is journaled with the
  T-080 commission flowing through untouched, every accepted
  cancellation marks its row cancelled under the wrapped adapter's own
  exchange identity, and both persistence arms are best effort - a
  journal fault is logged and never turns an accepted trade action
  into a tool error. The risk-check order in the tool handlers is
  untouched (every order passes `IRiskManager` before any placement),
  and no Core entity or interface changed. Determinism holds across
  restarts: a persisted fill reaches the PnL engine's ledger replay
  within the same 5-second sync cadence the UI lane rides, each fill
  ingested exactly once - the goldens pin a recomputed engine state
  with the realized commission repeated, never doubled. The wrapper is
  the only new construction unit: the composition root registers it
  with one line, the tools' registrar resolves it, no resx changes.

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
