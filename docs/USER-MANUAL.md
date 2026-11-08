# ZettaTrade - user manual (alpha)

This manual documents what the app ships today. ZettaTrade is a cross-platform desktop
trading terminal for crypto market data with local-first AI integration over the Model
Context Protocol (MCP). It renders live market data (Binance and Bybit public endpoints,
plus an offline demo lane), charts with SkiaSharp, keeps data on your machine, and ships a
**paper trading** backend only - it never touches real funds or exchange keys.

The application moves fast between alpha builds: a control or a settings key can shift
places. This manual walks the shipped code of the current alpha (the status-bar version
chip shows the exact build, `0.1.0-alpha.NN`). It documents no queued or planned features.

- **ZettaTrade is educational software, not investment advice.** Trading involves
  substantial risk of loss; nothing here is a recommendation to buy, sell or hold any
  asset. See the README's [Disclaimer](../README.md#disclaimer).
- Security posture (keys, secret scanning): [`docs/SECURITY.md`](SECURITY.md).
- Screenshots: a dedicated, deliberately open section at the end of this file.

## Contents

1. [Installation](#1-installation)
2. [First run](#2-first-run)
3. [Concepts](#3-concepts)
4. [Keyboard map](#4-keyboard-map)
5. [Paper vs live: safety notes](#5-paper-vs-live-safety-notes)
6. [Screenshots](#6-screenshots)

---

## 1. Installation

### Prerequisites

| What | Notes |
|---|---|
| .NET 10 SDK | The solution builds on `net10.0`; the repo pins SDK `10.0.100` (`global.json`) with `latestFeature` roll-forward. Install [the .NET 10 SDK](https://dotnet.microsoft.com/download) for your platform. |
| A desktop session | The app is an Avalonia UI window: Windows 10 or newer, macOS 12 or newer, or a Linux desktop (XDG-compliant session). Headless servers cannot show the window. |
| A keyring daemon (Linux, optional) | Secrets prefer the Linux Secret Service (libsecret/D-Bus). Without a keyring daemon the app falls back to an encrypted file it creates itself. |
| Internet (optional) | Live market data needs connectivity to the configured venue endpoints. The demo adapter works fully offline. |
| Ollama (optional) | The AI chat answers from a local Ollama server: default address `http://localhost:11434`, default model tag `llama3.2` (both under the `AI` settings section). No cloud provider is wired. |

### Build and run from source

No packaged installers or GitHub Release downloads exist yet, so every install today is a
build-from-source:

```bash
git clone <this repository>
cd ZettaTrade
dotnet build ZettaTrade.slnx
dotnet run --project src/ZettaTrade.App
```

Platform notes:

- **Windows:** run `dotnet run` from any directory the user can write; the process stores
  per-user data under `%LOCALAPPDATA%` and per-user settings under `%APPDATA%` (paths
  below). Secrets go to DPAPI-protected (user-scoped) storage automatically.
- **macOS:** the app runs as a plain .NET console-hosted window. Settings live under
  `~/Library/Application Support/ZettaTrade/`; secrets go to the macOS Keychain.
- **Linux:** run inside a graphical session on Wayland or X11. Settings follow XDG
  (`~/.config/ZettaTrade/`), data under `~/.local/share/ZettaTrade/`, logs under
  `~/.local/state/ZettaTrade/logs/`. With no keyring daemon, the encrypted-file fallback
  stores secrets instead.

### Files the app owns (all per-user, per-machine)

| What | Path |
|---|---|
| Settings file | `%APPDATA%\ZettaTrade\config\appsettings.json` (Windows) - `~/Library/Application Support/ZettaTrade/config/appsettings.json` (macOS) - `~/.config/ZettaTrade/config/appsettings.json` (Linux) |
| Data (database, plugins) | `%LOCALAPPDATA%\ZettaTrade\data\` (Windows) - `~/Library/Application Support/ZettaTrade/data/` (macOS) - `~/.local/share/ZettaTrade/data/` (Linux) |
| Theme files | `config/themes/` inside the settings directory |
| MCP client servers | `mcp.config.json` next to the settings file |
| Logs | under the data directory on Windows and macOS; `~/.local/state/ZettaTrade/logs/` on Linux |

The Serilog file logs never touch stdout: MCP protocol messages are the only thing allowed
on stdout. The settings file carries non-secret settings only - secrets live in the
per-machine secret store (see [3.8 Secrets posture](#38-secrets-posture)).

---

## 2. First run

The first launch shows, in order:

1. **The disclaimer dialog.** The shipped wording states that ZettaTrade is educational
   software, not investment advice, names the alpha posture (alpha software, paper-first
   trading, self-test before real funds, user-scoped key hygiene) and carries an
   **Acknowledge** button. The acknowledgment is persisted per wording version: a future
   wording update re-shows the dialog exactly once per install. A plain dismissal keeps
   the flag unset, so the next launch re-shows it. The flag never gates a trading action;
   the same dialog is reachable later from **Help -> About / Disclaimer**.
2. **The first-run wizard.** Three steps of drafts: the theme pick, the exchange adapter
   pick and the first workspace symbol pick. Nothing applies before the finish button
   succeeds; any exit (finish, skip, window close) marks the announcement flag, so the
   wizard shows exactly once per install. The adapter step serves the demo lane by
   default - a canned-data simulator that never touches real funds - and its note says
   where secrets live: the wizard collects no keys at all. Re-run it any time from
   **Settings -> Appearance -> Run setup wizard...** (a re-run never re-touches the
   announcement flag).
3. **The workspace** lands configured: menu bar, status bar, chart column, order book,
   trade tape and watchlist columns, and the AI assistant column on the right.

Theme and language switch live from **Settings -> Appearance** - no restart needed: the
theme picker lists the built-in palettes plus every valid theme file in your themes
folder (rows carry a preview swatch strip), the language picker lists the shipped
languages (English and Russian ship today), and a chart clock format choice (24-hour by
default, 12-hour optional) lives on the same tab. Theme files hot-reload: save a file in
the themes folder and the app re-reads it; broken files surface as an explicit list on
the same tab instead of disappearing silently.

### The main window at a glance

```text
+--------------------------------------------------------------------------+
| Menu bar:  File  Trading  AI  Help                                       |
+------------------------------------------------------------+-------------+
|  Status bar: ready - exchange picker - exchange + PAPER    |             |
|  connection state - theme - language - version - health    |   AI        |
+------------------------------------------------------------+  assistant  |
|                                        | order book | trade |  (toggle,  |
|  Chart workspace                       |  ladder +  | tape  |   chat,    |
|    symbol picker + favorites star      |  depth     |       |   context  |
|    time frame / series type / overlays |  chart     | watch |   chip)    |
|    candles chart + signal markers      |            | list +|            |
|    signal rail underneath              |            | spark |            |
+----------------------------------------+------------+-------+------------+
^ a toast stack floats top-right; Ctrl+K opens the symbol palette overlay ^
```

The four vertical splitters (assistant, chart/book, book/tape, tape/watchlist) persist
their dragged widths; a hidden assistant column leaves no dead gutter.

---

## 3. Concepts

### 3.1 Workspace and symbol selection

One symbol context feeds the chart and the order book. You change it in two places:

- The **chart's symbol picker** (top of the chart column): the instrument list of the
  active exchange, with favorites wearing a star badge.
- The **global symbol palette** (`Ctrl+K` anywhere in the workspace): one query box over
  every registered adapter's instruments - favorites show a star, the session's recently
  applied pairs carry a circular-arrow mark, each row names its exchange. The keyboard
  drives it (see the [keyboard map](#4-keyboard-map)); below the rows the panel says
  honestly which of the three states it is in: loading, no instruments at all, or no
  matches.

Favorites and hidden pairs are managed in **Settings -> Pairs**. The workspace symbol
persists across restarts, as do the applied panel widths and the assistant's visibility.

**Exchange selection** lives in the status bar's picker (it renders only when two or more
adapters are registered - one exchange means one concept, one control) and in the
first-run wizard. The shipped adapters are the offline **demo** lane, **Binance** and
**Bybit** read-only market data, and the **paper** trading engine. The address hosts they
contact come from the `Exchange` settings section (see the README's
[Testnet first](../README.md#testnet-first) for sandbox overrides).

### 3.2 The panels

**Chart.** Candlestick-first with zoom, pan and older-history paging; the hover shows
the candle's OHLC and the crosshair price; the time axis carries exchange-grade calendar
anchors (labeled day anchors and year lines; zoom never re-buckets the axes), and a
history-limit note appears when the loaded series proved an end. The toolbar across the
chart:

- **Time frame picker:** loads the symbol at the chosen interval; a flip reloads live.
- **Series type:** Candlesticks, Line, Area, OHLC bars, Heikin-Ashi (the HA series is the
  client-side smoothed bar set aligned with the loaded series).
- **VWAP toggle:** the session-anchored volume-weighted-average-price line; sessions
  break at every UTC calendar-day boundary (crypto venues trade around the clock).
- **Volume profile toggle:** a volume-at-price histogram beside the scale, computed from
  the loaded window on every pass.
- **Indicator overlays:** the lines of the toggleable indicator plugins (see
  [3.4](#34-indicators-and-strategies)).
- **Export candles CSV:** writes the displayed series to a user-chosen file; a write
  fault lands on the chart's own error line; the deep-history end note shows when the
  feed's series ends.

**Order book** (its own column, resizable): the ladder of levels (top) and the cumulative
depth chart (bottom) both bind the same published snapshot, so the two always agree on
the resolved depth. Clicking a level prefills the New Order dialog (symbol, side and
price) - nothing is sent from a book click on its own. The book streams the workspace
symbol (BTC/USDT until a selection lands); a snapshot parse failure keeps the previous
book instead of blanking it.

**Trade tape** (next column): the newest trades on top, side-colored by the aggressor;
the rows pause on hover, and the clear button empties the tape (a switched symbol clears
the old rows).

**Watchlist** (the tape's neighbor): a paced feed of the active exchange's majors with
each row's last price, the signed 24-hour change and the compact 24-hour volume, plus a
sparkline mapped from the row's own 32-price ring. The workspace selection's symbol joins
the row set live. Updates coalesce to one snapshot per 500 ms pulse, so a quote storm
never storms the UI; a symbol switch on the exchange re-arms the pumps.

**Strategy signal rail** (under the chart): the newest strategy readings stream in -
a burst inside one coalesce window lands as a single rows replacement, and the ring stays
bounded and newest-first. A row shows the accent dot of the stance, the reading candle's
time, the strategy id, the stance label, the symbol and the reading price; pressing a row
**prefills** the New Order ticket and never sends an order - a signal is a read for the
human, nothing rides it automatically. Long and short stances carry their accent colors;
a neutral reading never enters the rail.

**Notification center** (Trading -> Notifications; toasts float top-right in the
workspace): one publish surface, two read lanes. Toasts stack at most five visible rows
with a count badge, each leaving on its own after its lifetime or on a click; a routed
click opens the matching panel (alerts land on the alerts manager, an adapter-state
notification only dismisses - the health strip is always visible). The history window
shows every record; it carries four mute toggles and a **Do not disturb** flag that
persist in the `Notifications` settings section: a muted event class still lands in
history but skips the popups.

**AI assistant** (right column; toggle with `Ctrl+Shift+A`): the chat is answered by a
local Ollama model. Enter sends (the input accepts no newlines), the send button disables
while a request is in flight or the input is empty, and messages render role-tagged with
timestamps. The **Market snapshot** toggle above the input is the one explicit consent
chip behind which every send carries a compact market snapshot of the workspace moment
(venue and paper/live classification, the last price and 24-hour change, the top three
book levels per side, the enabled indicator setup) - it persists per workspace in the
`UI.ChatMarketContext` key and stays off until you enable it. A failed send leaves an
in-chat error line with the underlying reason instead of a silent failure.

### 3.3 Trading surfaces (Trading menu)

All trading actions ride one pipeline: request validation, then the risk manager, then a
confirmation dialog when required, and only then the adapter. A rejected or refused
request leaves the book untouched and reports explicitly - nothing is silently coerced.

**The New Order dialog** (`Ctrl+N`): a symbol autocomplete row (matches under a
case-insensitive contains filter), the **Buy/Sell** side, and the shipped order types:

| Type | Fields |
|---|---|
| Market | quantity |
| Limit | quantity + limit price |
| Stop-limit | trigger price, quantity + stop-limit price |
| Trailing stop | quantity + trail distance (the price is the ratcheting trigger, never editable) |
| OCO | two linked legs: stop trigger + stop-limit price, plus a limit price and quantity |

The position-sizing helper drafts sizes against the account's fresh balances snapshot:
quarter/half/whole-of-balance buttons and a risk-percent anchored step
(`PositionSizer.DefaultRiskPercent`). The estimated fee names the maker/taker role per
leg (a resting limit is maker, everything else crosses) and rides the typed price or the
freshest market reference for the price-less types - display only, never a placement
decision. **Place** submits; a high-risk shape raises the confirmation dialog first.

**Positions & open orders panel:** position rows and working-order rows on one surface.
Working, amendable rows (a limit, a stop-limit or a trailing stop) carry **Amend**: the
dialog edits quantity and, for the limit-bearing types, the limit price; the amend
re-reads the adapter's fresh order state, re-checks risk and asks for confirmation when
the risk manager demands one, when the order carries fills or when the notional moves
beyond 25% in either direction; a racing fill wins and is reported honestly - the
shipped paper adapter runs the amend as an explicit cancel + replace with an
`Amending...` row state, never as a silent fake replace. Every working order row offers
**Cancel**; the same panel supports **Cancel all** on a symbol (a bulk cancel; a leg that
dies mid-flight is skipped and reported individually).

**Position actions** (on position rows): **Close**, **Reduce** (a partial close) and
**Reverse** ride the same mandatory pipeline over the composed legs, with one
confirmation per action over the current position re-read at placement time - a reverse
whose exit leg leaves the symbol non-flat aborts its re-entry with a localized report;
zero/over-sized reductions and closing an empty position are explicit refusals;
in-flight rows disable themselves so a double-click is a no-op.

**Balances panel:** a read-only surface of every active exchange's served balances, with
per-exchange sections and an aggregated sum-per-asset view. An adapter without balance
data says "not provided" explicitly - a zero is never fabricated - and a faulting query
renders the localized fault. Manual refresh.

**Order history window:** the persisted order journal (SQLite via EF Core) with terminal
statuses, the filled volume and the recorded commissions (the Fee column shows the dash
when a fill carries none). Two export actions write the loaded rows (orders and fills)
to RFC 4180 CSV: invariant decimals, ISO-8601 UTC stamps, UTF-8 without BOM, CRLF rows,
streamed row by row - a six-figure history exports with a one-row memory footprint. A
cancelled save writes nothing; a write fault surfaces on the window's error line.

**Trade statistics window (`Trading -> Trade Statistics...`):** the persisted order
journal scans into the metric tiles - win rate, profit factor (an "infinite" mark when
no losing fill divides; a dash glyph when there is nothing to divide), maximum drawdown
over the equity curve including the zero-seeded start, average winner and loser, trade
count and filled volume - over a Skia equity curve anchored at the first fill with one
point per closing fill. Filters (symbol, exchange id, paper/live, date window) refine
the keyset-paged scan progressively. The read is query-only from journal content; the PnL
accounting core (an average-entry/realized engine persisting its realized history to
SQLite, priced live on the ticker feeds) rides beneath it - there is no separate PnL
widget today.

**Backtest (`Trading -> Backtest...`):** the offline deterministic simulator replays the
local candle cache through the strategy seam; the run form is fresh per open with a
picker over the registered strategies and the run settings. The replay uses paper-parity
accounting, records balance refusals visibly and applies the same commission schedule;
the report comes as an equity summary; a concurrent second start refuses explicitly; the
cancel is the stop and produces no artifact. No adapter is ever contacted - the replay
is offline by construction over the stored candles.

**Price alerts** (`Trading -> Alerts...`): the modal manager of the alert roster -
create, edit (re-arm), snooze and delete; the row columns (symbol, condition, status,
last fired, note) update live for engine events and a language switch alike. Price and
percent-move edges are exact (strictly above/below, exact-at; the percent move compares
the window's oldest in-window reference). A garbage spelling, an unparsable price or an
unseen kind refuse each with its own localized error line - never a silently coerced
alert; the fired alert is persistent and a reloaded roster never fires a second time.
The alert sound degrades to a logged warning when the audio cannot play. Fires surface
as notifications (the notification center's alerts lane).

### 3.4 Indicators and strategies

**Indicator plugins** (each a plugin assembly the scanner loads): SMA, EMA, WMA, RSI,
MACD, ATR and Bollinger ship today. The **Indicators** settings tab renders a row per
actually-loaded plugin with its toggle and its own plugin-defined settings - it never
invents rows for absent assemblies. A toggled indicator streams its line onto the chart
and its value set into the AI chat's market snapshot.

**Strategies** evaluate read-only closed-candle windows and stamp signals. The demo
golden-cross strategy ships. Signals are read-only events for the human: they surface as
chart markers (a small triangle anchored at its own candle's close level, rendered
exactly once on the bar that produced it) and in the signal rail
([3.2](#32-the-panels)). **No signal ever places or cancels an order** - a rail press
only prefills the ticket. There is no shipped auto-trading path; nothing rides a signal
in any lane.

### 3.5 MCP and the AI assistant

ZettaTrade is AI-native over MCP, and local-first: the model interface runs on your
machine.

- **The built-in MCP server, "Zetta"**: this terminal's tools exposed to AI assistants
  over stdio. The shipped tool set: `get_market_data`, `get_positions`, `get_balance` and
  the trading pair `place_order` / `cancel_order` - the trading tools run the same
  validation, risk-management and confirmation flows the UI paths do; a confirmation
  request reaches a human through the app, never a model decision. The **MCP** settings
  tab exposes the server settings; stdout carries MCP protocol messages only - logs stay
  in files (readable from **Help -> Log viewer**, which redacts credential-shaped
  strings before they reach the viewer's ring).
- **The MCP client**: the app connects to your own external MCP servers (stdio or
  Streamable HTTP transports). Configure them in `mcp.config.json` next to the settings
  file, or through the **MCP** settings tab carrying transport, address/port and
  task-level options. Server secrets (tokens for external MCP servers) live in the
  per-machine secret store; when the app launches a server's child process, the secret
  is passed via stdin - never as a command-line argument.
- **The AI chat**: local model only (`AI.OllamaUrl` + `AI.Model` in the settings file;
  defaults `http://localhost:11434` and `llama3.2`). The consent chip behavior is in
  [3.2](#32-the-panels). Cloud provider wiring is not shipped.

### 3.6 Themes and appearance

- Five in-repo palettes plus any theme files you drop into the themes folder
  (`config/themes/` inside the settings directory); rows in the picker carry a preview
  swatch strip sampled from the theme file. **Hot reload:** edit a theme file and the
  loaded look re-inks live; files that failed to parse list explicitly
  (Settings -> Appearance) instead of vanishing silently.
- Theme identity is canonical; switching applies at runtime, live, and the status bar
  names the current theme and language at all times.
- The chart clock format (24h/12h) rides the same tab.

### 3.7 Data freshness (connections, reconnects, cache)

Binance and Bybit serve read-only market data (public REST history + WebSocket streams).
The status bar's **adapter health strip** carries one dot per registered adapter -
connected / reconnecting / down / unsupervised states with a tooltip that names the last
event moment and a visible failure counter; the connection state rides the status bar's
own field. A drop is self-healing: the adapters resubscribe under the reconnect policy
without a manual action. Candle history is cached locally in SQLite with a write-through
path and a deep-history backfill that repairs gaps and extends pages; the chart notes an
end honestly when the loaded history proved a limit.

### 3.8 Secrets posture

- Keys and tokens live **only in the per-machine secret store** (OS-backed): DPAPI
  user-scoped protected data on Windows, the macOS Keychain, the Secret Service
  (libsecret/D-Bus) on Linux with an app-created encrypted file fallback where no keyring
  daemon runs.
- The wizard and every trading surface collect **no keys**: the shipped paper backend
  needs none. An external MCP server's token is stored the same way and passed to its
  child process via **stdin**, never as a command-line argument (process listings are
  readable by anything on the machine).
- Keys never belong in the repository, on a command line, or in the plain-text settings
  file - `appsettings.json` carries non-secret settings only (endpoint hosts, the AI
  section, toggles). Log output (the Serilog files and the in-app viewer's ring) passes
  a redactor gate before anything credential-shaped can land on a surface.
- Nothing in the app phones home; no telemetry exists anywhere.

The repository-side secret scanning posture (CI + history audit + allowlist contract) is
documented in [`docs/SECURITY.md`](SECURITY.md) as a linked reference, not duplicated
here.

---

## 4. Keyboard map

The application-wide bindings that exist today, extracted from the shipped sources:

| Gesture | Action | Where it is advertised |
|---|---|---|
| `Ctrl+,` | Open the settings window | File -> Settings |
| `Ctrl+N` | Open the New Order dialog | Trading -> New Order |
| `Ctrl+K` | Toggle the global symbol palette (open/close) | the palette overlay hint |
| `Ctrl+Shift+A` | Show/hide the AI assistant panel (show focuses the input) | AI -> Chat (checkable) |
| `Alt+F4` | Exit the app (equals File -> Exit) | File -> Exit |

Overlay-local keys (they act only while their overlay holds the focus):

| Gesture | Action | Overlay |
|---|---|---|
| `Up` / `Down` / `PageUp` / `PageDown` | move the keyboard selection | symbol palette |
| `Enter` | apply the highlighted pair | symbol palette |
| `Esc` | close the palette (a press on the backdrop closes it too) | symbol palette |
| `Enter` | send the chat message | the assistant's input box |
| `Alt+F4` | close the focused window | any window |

That is the whole app-scoped map today: no further global bindings are wired, and there
is no user-facing binding editor or cheat-sheet overlay yet - these are the window
manager's standard keys plus the five application bindings above.

---

## 5. Paper vs live: safety notes

- **The shipped trading backend is the paper engine, and only that.** Every order - from
  the dialog, the panels or an MCP tool - fills against the paper simulator: simulated
  fills against live market prices, in-memory balances and positions, a demo starting
  balance of 100,000 USDT, resting limit orders pending until filled or cancelled. No
  code path reaches real funds: no live venue trading exists in this alpha.
- **The status bar keeps the lane visible.** The exchange readout prints the venue plus
  the paper tag at all times; no real-money lane exists, so nothing a paper lane would
  need to be told apart from is on screen. There is no separate trading-mode chip yet -
  the paper tag in the status bar is the one always-present lane indicator.
- **Every order passes risk validation** (`IRiskManager`); rejected orders do not
  execute. **High-risk operations require explicit human confirmation**
  (`IConfirmationService`) - the confirmation dialog shows the pre-submit summary. This
  includes orders an AI assistant triggers through the MCP trading tools: the human
  confirms, the model never decides.
- **A signal is never an order**: strategy readings only prefill the ticket.
- **Public data, user-scoped keys:** market data comes from venues' public endpoints;
  nothing in the shipped set asks you for an account key. If you point the adapters at a
  testnet host (a settings change, see the README's
  [Testnet first](../README.md#testnet-first)), an override is read once at startup -
  restart to apply it - and the settings file still carries no credentials: at this
  alpha, the exchange-facing adapters are read-only, and the trading lane remains paper
  regardless of the host the market data rides.
- **Key hygiene is your responsibility** (the moment you hand keys to any other tool):
  read-only keys for market data, trade-only (withdrawal/transfer off) keys for trading,
  IP allowlists where offered - the README's
  [Trading safety](../README.md#trading-safety) section is the full guidance.
- **This is educational software, not investment advice.** The disclaimer surfaces at
  first run, lives in the README and stays reachable from Help -> About / Disclaimer.
  Nothing here is a recommendation to buy, sell or hold any asset; alpha behavior can
  change between builds without notice.

---

## 6. Screenshots

> **Status: pending real-run captures.** This manual deliberately ships text-only: an
> automated capture of a live Avalonia UI run was not demonstrable in the authoring
> session, and no screenshot appears here without a real application window behind it -
> no stock panel is pasted as an illustration, and no diagram image is invented beyond
> the ASCII sketch in [First run](#2-first-run).

The intended shot list for when captures are made (each taken from a real run, in the
current themes, with demo data only - no real credentials ever on screen):

1. First run: the disclaimer dialog, then the wizard's three steps.
2. The workspace in a dark and a light theme: chart with the VWAP and volume-profile
   overlays plus an indicator line, order book ladder, depth chart, trade tape,
   watchlist, assistant column.
3. Heikin-Ashi and line series from the series-type selector on the same symbol.
4. The symbol palette overlay mid-query (row states visible: favorites and recents).
5. The New Order dialog on a limit ticket with the sizer and fee estimate, then the
   confirmation dialog over it.
6. The positions panel mid-amend (`Amending...` state) and the balances panel's
   aggregated view.
7. Trade statistics tiles plus the equity curve over one filled symbol.
8. The alerts manager with one armed and one fired alert; a toast stack of the same
   event in-session.
9. The log viewer with a filtered slice; the crash dialog only if incidentally captured
   (never staged).
10. The signal rail streaming under the chart with the demo strategy's readings.
