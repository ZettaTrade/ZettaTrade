# ZettaTrade — Task Board

Statuses: `planned` → `in-progress` → `review` → `done`. One task per feature. Agents work only in their own worktree/branch, within the listed scope. Branches are created with `pwsh scripts/new-worktree.ps1 -Name <name> -Base develop` (feature branches base on `develop`; the script's default base is `main`). A task's detail section leaves this file for `docs/TASKS-ARCHIVE.md` once the task is done and merged; the one-line board row stays. Rows and sections reference only portable facts (branches, worktree names like `zt-<name>`, base hashes); machine-local absolute paths never enter repository text (the gate lint `[MACHINE-PATH]` fails on them).

| Task | Branch | State | Blocker |
|------|--------|-------|---------|
| T-001 Load themes from JSON files | develop | done | — |
| T-002 Wire advertised menu hotkeys | feat/hotkeys | done | — |
| T-003 Hot-reload of theme files | feat/theme-reload | done | — |
| T-004 Settings: themes folder + file validation | feat/theme-settings-ui | done | — |
| T-005 Canonical theme identity | fix/theme-identity | done | — |
| T-006 Theme preview swatches in the picker | feat/theme-preview | done | — |
| T-007 resx en/ru sync gate | chore/resx-gate | done | — |
| T-008 New Order dialog | feat/order-dialog | done | — |
| T-009 Positions & open orders panel | feat/positions | done | — |
| T-010 Order history + persistence (EF Core) | feat/order-history | done | — |
| T-011 Order book widget (60 fps target) | feat/order-book | done | — |
| T-012 UX follow-ups: chat focus, reconnect states | feat/chat-focus | done | — |
| T-013 Order dialog fixes: market-order JSON strings + combo clipping | fix/order-dialog | done | — |
| T-014 Instrument autocomplete in the order dialog | feat/symbol-autocomplete | done | — |
| T-015 Configurable exchange endpoints + registry seam | feat/exchange-config | done | — |
| T-016 Exchange selector in the toolbar | feat/exchange-selector | done | — |
| T-017 One symbol context for the workspace (chart+book), fill the activation gap | feat/workspace-symbols | done | — |
| T-018 App version display in the status bar | feat/status-version | done | — |
| T-019 Startup-walk audit after the typed-registration guard fix | feat/startup-walk-audit | done | — |
| T-020 e2e WebSocket transport tests against a loopback server | feat/ws-transport-tests | done | — |
| T-021 Cold-start polish: the first symbols apply lag | feat/cold-start-symbols | done | — |
| T-022 Order-dialog header names the active exchange | feat/order-dialog-exchange | done | — |
| T-023 Real second adapter: Bybit (read-only market data) | feat/bybit-adapter | done | — |
| T-024 Fix the demo seeded-walk underflow (demo bar loads overflow) | fix/demo-walk | done | — |
| T-025 Click a book level to prefill the order dialog | feat/book-click | done | - |
| T-025b Fix the prefilled-dialog defects from the live review (dropdown echo, dangling empty-quantity error) | fix/dialog-prefill | done | — |
| T-026 Working time frame on the chart | feat/timeframe | done | — |
| T-027 Hide/show the AI assistant panel | feat/ai-panel-toggle | done | — |
| T-028 Candle OHLC + crosshair price on chart hover | feat/chart-hover | done | — |
| T-029 Persist the workspace symbol | feat/persist-symbol | done | — |
| T-030 Debt cleanup: LocalizedViewModelBase + Serilog console sink | chore/debt-cleanup | done | - |
| T-031 Cancel a working order from the UI | feat/cancel-order | done | - |
| T-032 The assistant answers: local model first (Ollama) | feat/ai-chat-local | done | - |
| T-033 Chart pan + older-history paging | feat/chart-pan | done | - |
| T-034 Indicator seam MVP (a plugin draws an SMA overlay) | feat/indicators | done | - |
| T-035 Chart axis labels clip at the canvas edges + zoom never re-buckets the axes (user-reported) | fix/chart-axes | done | — |
| T-036 Chart history depth + candle-size cap (user-reported: W1/MN render one full-width candle) | fix/chart-history | done | — |
| T-037 Exchange-grade time axis: density, calendar anchors, year labels (user-reported) | feat/time-grid | done | — |
| T-038 Draggable splitters: resize the chart / depth / assistant columns (user-reported) | feat/layout-splitters | done | - |
| T-039 Symbol favorites (star badge, pinned on top) + hiding pairs (user idea) | feat/symbol-favorites | done | - |
| T-040 MCP server settings in the settings window (user idea) | feat/mcp-settings | done | - |
| T-041 Telegram trade notifications (user idea, post-beta) | feat/telegram-notifier | queued | - |
| T-042 Settings window polish: the time-format label collision + the SMA tooltip (user-reported) | feat/settings-window-polish | done | - |
| T-043 Settings window: split the sections into tabs (user-reported) | feat/settings-tabs | done | - |
| T-044 UI color hygiene: XAML literals and HTML entities out, colors into constants/themes (supervisor-direct) | feat/ui-colors | done | - |
| T-045 Workflow bookkeeping codified: develop-based branches, TASKS.md statuses, app version bump | feat/skill-rules | done | - |
| T-046 [Critical] DPAPI secret store wipes all stored secrets after a silent load failure (BUG-001) | feat/dpapi-store-wipe | done | - |
| T-047 [Critical] MCP server drops every response for numeric JSON-RPC ids (BUG-002) | feat/mcp-numeric-ids | done | - |
| T-048 [Critical] Bybit reconnect resubscribes cancel each other (reconnect churn with 2+ symbols) (BUG-003) | feat/bybit-resubscribe | done | - |
| T-049 Secret stores: silent data loss and rule violations across DPAPI/Keychain/secret-tool/encrypted file | fix/secrets-stores | done | - |
| T-050 MCP pipelines: host lock stall, client id matching, unread stderr, tools enum defaults | fix/mcp-pipelines | done | - |
| T-051 Paper trading ledger: corrupted entry price, stale reference price, phantom fills | fix/paper-ledger | done | - |
| T-052 WebSocket transport: dead feed after reconnect, send races, frame parser, ignored depth | fix/ws-transport | done | - |
| T-053 Settings service: reload races erase just-written values, changes never published | fix/settings-hot-reload | done | - |
| T-054 Indicator plugin scan aborts on a plugin with missing dependencies | fix/indicator-scan | done | - |
| T-055 Chart VM: persisted 12-hour clock lost in the common restore path + symbols bootstrap race | fix/chart-vm-restore | done | - |
| T-056 App VM error paths: unlogged startup crash, silent order failures, off-thread and inconsistent UI | fix/app-vm-error-paths | done | - |
| T-057 Risk: market/stop orders bypass the hard order-value cap | fix/risk-cap | done | - |
| T-058 UI primitives: concurrent async helper vs contract, pan pitch mismatch, undisposed SKPath | feat/ui-primitives | done | - |
| T-059 Themes: burst hot-reload drops files, watcher death, unordered live list, stale order book | feat/theme-hot-reload | done | - |
| T-060 Localization: culture race in the cached lookup + suspected binding lifetime leak | fix/localization-races | done | - |
| T-061 Order history store: terminal-status rewrite + upsert check-then-act race | fix/order-history-store | done | - |
| T-062 XDG path resolver returns CWD-relative paths for empty environment variables | fix/xdg-paths | done | - |
| T-063 The closed-task archive: sections leave the board, the archive rule and the reply language codified (supervisor-direct) | feat/task-archive | done | - |
| T-064 Worktree lifecycle: remove the task folder and branch after merge (supervisor-direct) | feat/worktree-cleanup | done | - |
| T-065 Localization + themes stand alone: a lean Livery library (renamed from ZettaTrade.Appearance at the supervisor's request; supervisor idea) | feat/appearance | done | - |
| T-066 Window chrome + control restyling: styled title bar, buttons, inputs across the shell (supervisor idea) | feat/window-chrome | done | - |
| T-067 Indicator plugin settings render only for plugins that are actually loaded (supervisor idea) | feat/indicator-settings-gate | done | - |
| T-068 MCP client settings: external servers with transport, address/port and task-level options (supervisor idea) | feat/mcp-client-settings | done | - |
| T-069 The appearance contracts move into Livery: Core stops owning IThemeManager/ILocalizationManager (supervisor idea) | feat/appearance-contracts | done | - |
| T-070 The localized Strings catalog returns to ZettaTrade.UI: per-app strings, not library ones (supervisor-direct) | feat/livery-resx | done | - |
| T-071 Window chrome follow-ups: fix the black content pane + caption strip polish (user-reported) | feat/window-chrome-black | done | - |
| T-072 Order book empty under Bybit: the facade resolves the displayed depth against the served one (user-reported) | feat/order-book-depth | done | - |
| T-073 Flaky concurrency test: GetString_AFactoryParkedAcrossASwitch fails intermittently under load (queued from the T-071/T-072 landing) | feat/flaky-localization | done | - |
| T-074 Advanced order types: stop-limit, trailing stop, OCO | feat/order-types | done | - |
| T-075 Amend a working order from the orders panel | feat/order-amend | done | - |
| T-076 Bulk order management: cancel every working order on a symbol | feat/order-bulk | done | - |
| T-077 Position actions: close, reverse, partial close (paper-first) | feat/position-actions | done | - |
| T-078 Order ticket confirmation: the pre-submit summary | feat/order-confirm | done | - |
| T-079 Position sizing helper in the ticket (percent / risk-anchored) | feat/position-sizer | done | - |
| T-080 Fees: maker/taker in the ticket, fills and history | feat/fees | done | - |
| T-081 Balances panel: per-exchange and aggregated | feat/balances | done | - |
| T-082 PnL engine: realized/unrealized accounting core | feat/pnl-engine | done | - |
| T-083 Trade statistics dashboard: win rate, profit factor, equity curve | feat/trade-stats | done | T-082 (soft) |
| T-084 CSV export: order history, fills, candles | feat/csv-export | done | - |
| T-085 Local candle cache in SQLite (write-through, offline start) | feat/candle-cache | done | - |
| T-086 Deep-history backfill and gap repair | feat/backfill | done | T-085 |
| T-087 Trade tape (time & sales) panel | feat/trade-tape | done | - |
| T-088 Depth chart: cumulative bid/ask areas view | feat/depth-chart | done | - |
| T-089 Chart types: line, area, bars, Heikin-Ashi | feat/chart-types | done | - |
| T-090 Y-axis scale modes: log and percent | feat/chart-scales | done | - |
| T-091 Drawing tools: trend line, horizontal level, fib retracement | feat/draw-tools | done | - |
| T-092 Standard indicator pack (EMA, RSI, MACD, Bollinger… as bundled plugins) | feat/indicator-pack | done | - |
| T-093 VWAP + volume profile overlay | feat/vol-vwap | done | - |
| T-094 Chart export: PNG snapshot + CSV candles | feat/chart-export | done | - |
| T-095 Global symbol palette (Ctrl+K) across adapters | feat/symbol-palette | done | - |
| T-096 Price and percent-move alerts with a manager | feat/price-alerts | done | - |
| T-097 Notification center: toasts, history, DND | feat/notif-center | done | - |
| T-098 Adapter health strip in the status bar | feat/health-strip | done | - |
| T-099 First-run wizard (theme, adapter, symbol) | feat/first-run-wizard | done | - |
| T-100 Settings import/export + factory reset | feat/settings-port | done | - |
| T-101 Log viewer window + global crash guard | feat/log-viewer | done | - |
| T-102 Hotkey manager + cheat sheet overlay | feat/hotkey-manager | done | - |
| T-103 Detachable panels: multi-window workspaces | feat/multi-window | done | - |
| T-104 Single instance hand-off + CLI symbol open | feat/single-instance | done | - |
| T-105 Reconnect policy hardening: backoff, jitter, watchdog | feat/reconnect-policy | done | - |
| T-106 Exchange rate limiter with per-endpoint budgets | feat/rate-limiter | done | - |
| T-107 Adapter conformance test kit (shared battery) | feat/adapter-kit | done | - |
| T-108 Performance HUD overlay (fps, memory, WS latency) | feat/perf-hud | done | - |
| T-109 Soak harness and budget gates (cold start < 3 s, memory < 500 MB) | feat/soak | done | - |
| T-110 Accessibility pass (keyboard, automation peers, contrast) | feat/a11y | done | - |
| T-111 Strategy contracts MVP: candle-close signals (no auto-trading) | feat/strategies | done | - |
| T-112 Signal markers on the chart + signal rail | feat/signal-markers | done | - |
| T-113 Backtest harness over stored candles | feat/backtest | done | T-111 + T-085 |
| T-114 AI market-context injection into the chat | feat/ai-context | done | - |
| T-115 User manual + screenshots (docs-only) | docs/user-manual | done | - |
| T-116 Adapter WS E2E tests flake under solution-parallel load: the 10 s EventWait trips on scheduler starvation (queued from the T-073 verification) | feat/ws-e2e-budget | done | - |
| T-117 Secret scanner: gitleaks in CI + one-shot full-history audit | feat/secret-scanner | done | - |
| T-118 Trading-safety guidance in the README: read-only keys, IP restriction, no-withdrawal, testnet-first | feat/safety-readme | done | - |
| T-119 First-run disclaimer in the UI («not investment advice») + the explicit alpha posture | feat/first-run-disclaimer | done | - |
| T-120 Streaming watchlist panel: live quotes, 24h change, sparklines | feat/watchlist | done | - |
| T-121 Market heatmap: top pairs as change-colored tiles | feat/heatmap | done | - |
| T-122 My footprint on the chart: position lines + working-order markers | feat/chart-mine | done | - |
| T-123 Trading-mode chip: paper/testnet/live always visible | feat/trading-mode-chip | done | - |
| T-124 Symbol compare: a second series overlaid on the chart | feat/chart-compare | done | - |
| T-125 Delivery pipeline: CI builds + GitHub Releases (Win/macOS/Linux) | feat/release-pipeline | done | - |
| T-126 Product landing page: screenshots, story, download CTA | feat/landing-page | done | T-125 |
| T-127 Live testnet trading loop: a real limit order end-to-end (Bybit testnet) | feat/testnet-trading | done | - |
| T-128 Demo video: storyboard + shot list (docs/ops) | feat/demo-script | done | - |
| T-129 Alpha program: testers onboarding, feedback triage, public metrics | feat/alpha-program | done | - |
| T-130 Extract Livery into a standalone private repo (MIT kept) and consume it as a package | feat/livery-split | done | - |
| T-131 Plugin SDK seam: versioned abstractions that out-of-tree plugins build against | feat/plugin-sdk | done | - |
| T-132 Closed-source plugin packaging: signed private-repo artifacts + local license-key activation | feat/plugin-packaging | done | - |
| T-133 MCP paper fills enter the journal: tool-placed fills ride the ledger the panels and PnL read | feat/mcp-journal | done | T-082 |
| T-134 Supervisor-mode skill (OpenCode): the standing directives load only on explicit invocation | feat/supervisor-skill | done | - |
| T-135 Stabilize the flaky PositionsPanel in-flight test under suite load | feat/flaky-pospanel-timing | done | - |
| T-136 Repo hygiene: baked mojibake repair, machine-path purge, prevention lints | chore/repo-hygiene | done | - |
| T-137 Fix the app startup crash: the concrete settings store resolves in DI | fix/startup-di | done | - |
| T-138 Stabilize the App.Tests dispatcher tally flake: one serialized dispatcher world | feat/flaky-pump-tally | done | - |
| T-139 Board truthfulness: archive the lingering T-117/T-118 sections, restore the canonical archive order | feat/board-truth | done | - |
| T-140 Review hygiene pass: async-void routes, MCP registry locking, non-English prose cleanup | feat/review-hygiene | done | - |
| T-141 Soak isolation: pin the demo adapter so a run is network-free | feat/soak-isolation | done | - |
| T-142 Book timestamps: one clock discipline across adapters (the WS latency measures transport) | fix/book-timestamps | done | - |
| T-143 Palette suggestion rows carry automation names (not the record's ToString) | fix/palette-a11y-rows | done | - |
| T-144 UI test suite: a headless lane over the real windows (layout, tabs, flows) | feat/ui-tests | done | - |
| T-145 UI layout pass: a wrapping chart toolbar and a roomier settings window | feat/ui-layout | done | - |
| T-146 Plugins into a dedicated solution folder and a physical plugins directory | feat/soln-plugins | done | - |
| T-147 Agent canon split: a lean always-on AGENTS.md, a deduped zettatrade skill and on-demand references | feat/canon-split | done | - |
| T-148 Kill the BrokenBase fixture leak: the T-054 base assembly never reaches the output directories | feat/fixture-leak | done | - |
| T-149 Stabilize the App.Tests heatmap stale-epoch flake: a completion-driven settle pin | feat/app-test-flak | done | - |
| T-150 Design tokens: Fluent system brushes out of the views, palette keys only | feat/design-tokens | done | - |
| T-151 Palette contract lint: WCAG pins over every theme file + the Accent role fix | feat/palette-lint | done | - |
| T-152 Trading-mode chips readable: dark foreground on the state colors | fix/lane-chip-contrast | done | - |
| T-153 Status bar declutter: the message channel + the summary, the settings noise out | feat/status-bar | done | - |
| T-154 Livery chrome kit: shared Chip, StateDot, ToggleButton.on, InfoIcon, IconButton | feat/livery-chrome-kit | done | - |
| T-155 Type ramp + tabular numerals + the spacing scale, as tokens | feat/type-scale | done | - |
| T-156 Order ticket redesign: segmented Buy/Sell, a primary CTA, inline validation | feat/order-ticket-ui | done | T-154 (soft) |
| T-157 Table grammar for positions and order history | feat/table-grammar | done | T-154 (soft) |
| T-158 Assistant chat bubbles: role styling, copy, stop | feat/chat-bubbles | done | - |
| T-159 Watchlist and heatmap color semantics | feat/watchlist-polish | done | - |
| T-160 Chart toolbar icon pass, export flyout, shared PanelHeader | feat/chart-toolbar-icons | done | T-154 (soft) |
| T-161 Small surfaces polish: wizard swatches + step counter, hotkey keycaps, palette groups | feat/small-surfaces | done | - |
| T-162 Product identity: the app icon and the title-bar brand | feat/branding | done | - |
| T-163 The plugins consumption switch: the standard pack rides the package mechanics | feat/plugin-packages | done | - |
| T-164 Dev-side pack provisioning: the plugins directory fills from the package at build time | feat/plugin-provision | done | - |
| T-165 Theme singleton hygiene: the ThemeManager Dispose clears the static Instance (the T-073 shape) | feat/theme-singleton-hygiene | done | - |
| T-166 The demo catalog covers the watchlist's starter majors (the silent first-run failure: 6 of 10 rows never price) | feat/demo-catalog-majors | done | - |
| T-167 The title-bar close button rides its own column (the user-reported overlap with maximize) | feat/titlebar-buttons | done | - |
| T-168 The backtest picker offers the standard pack's first strategy (the strategy seam rides the SDK contract 2.1.0) | feat/strategy-pack | done | - |
| T-169 Settings window polish pass: the pairs transfer buttons ride the icon kit, the indicators rows align on fixed columns, the tab content clears the scrollbar | feat/settings-polish | done | - |
| T-170 Scroll surfaces audit: no window's content sits under the scrollbar (the user reports the overlap beyond the settings window) | feat/scroll-gutter | done | - |
| T-171 The license activation dialog opens again: the EULA surface resolves under its interface (the user-reported live ERR) | feat/license-di | done | - |
| T-172 The layout splitters become discoverable grips: every resizable boundary shows a visible grab handle (the user cannot find the resize affordance) | feat/splitter-grips | in-progress | - |
| T-173 A detached order book frees its main-window column: no dead reserved width rides behind the detached window (the user report) | fix/book-detach-space | planned | - |
| T-174 The standard pack grows the strategy catalog: the classic swing/breakout/reversion set rides the package (the user ask, scoped by the supervisor's analytics) | feat/strategy-pack-expansion | planned | T-168 |
| T-175 The startup binding sweep: the debug-output binding errors go silent and the menu gesture hints render (the user's debug log) | fix/binding-noise | planned | - |
| T-176 The user-action E2E lane: real-window UI tests ride FlaUI (the user's framework proposal, the Windows tier) | feat/e2e-flaui-lane | planned | - |

## Code review 2026-11-04 — findings index (source of T-046…T-062)

Full analysis: `docs/CODE_REVIEW.md` (56 findings: 3 Critical, 23 Major, 30 Minor, all verified
against the code at cited lines; every entry carries a stable `BUG-NNN` id). The findings are
filed as T-046…T-062, one task per code area; the three Critical findings each get their own
task. `docs/CODE_REVIEW.md` is the canon of definitions and per-finding line references; when a
task lands, its fixing agent flips the covered `BUG-NNN` statuses to Fixed there, in the same
commit series as the work. Areas explicitly cleared by the review (no bug found) are listed at
the end of that file, so no effort is duplicated.


---


## T-041 — Telegram trade notifications (post-beta)

- **Branch:** queued 2026-11-04 (user idea, translated from the original: "later I want the app to push trade information to Telegram — not urgent, after reaching beta") — deliberately parked after the beta milestone.
- **Origin:** fills and order events land only in the journal and the logs today; the trader wants them pushed where he actually reads.
- **What:** a notification sink that sends order/fill event messages (symbol, side, price, quantity, exchange, paper/live tag) to a user's Telegram chat through a bot token stored in `ISecretStore` (a token never travels as a command-line argument — stdin/secret-store only, the standing security rules). Opt-in per event type (fills, cancels, rejections), async and BEST-EFFORT: a failed send logs a warning and never blocks, retries or corrupts the trading flow; no third-party middleman services — the token is the user's own bot.
- **Scope:** a new notification service in Infrastructure riding the existing trading/journal event surface, the settings keys (`Notifications.*`), `ISecretStore` token, resx en+ru message templates, tests.
- **Acceptance:** e2e probe against a LOOPBACK Telegram API stub (the T-032 harness pattern): a fill event produces the expected message POST; a stub 500/timeout degrades to a logged warning with the trading flow untouched; the flag off or the token missing means quietly no sink.
- **Tests:** the message composition golden (en/ru templates), the event filtering, the best-effort failure matrix (timeout, 5xx, malformed response).

---

## UI design review 2026-11-10 — findings index (source of T-150…T-162)

A designer's pass over the shipped UI surfaces (the T-066 chrome strip, T-103 panel hosts, T-145
layout fold, T-110 accessibility shapes) walking every window, dialog and theme palette. The
findings are filed as T-150…T-162, one task per fixable area; no separate findings doc ships —
each section below carries its own origin. The deliberate batch shape: the design-token
discipline first (T-150, T-151, T-152 — the two parallel color systems and the contrast holes are
systemic), the Livery shared-kit prep next (T-154, T-155), then the surface redesigns riding it
(T-156, T-157, T-160), with the independent polishes spread alongside. All rows start `queued`;
the human prioritizes the lanes (the filed quick wins were the token swap and the chip contrast).


---

## T-172 - The layout splitters: prove the drags, then make the grips discoverable

- **Branch:** feat/splitter-grips, worktree zt-splitter-grips, based on develop@e1060ec (claimed 2026-11-11; filed from the user's ask - "add a vertical slider to resize the pieces" - extended by the user's follow-up: "they don't seem to work, I never managed to resize anything").
- **Origin:** T-038 shipped four GridSplitters (the assistant/book, chart/book, book/tape, tape/watchlist boundaries) riding dedicated 8px columns, with the persisted column widths bound TwoWay (BookColumnWidth, TapeColumnWidth, WatchlistColumnWidth, AiColumnWidth). The user cannot find them (no visual affordance survives the T-066 restyle) AND reports the drags never worked - the functional question comes first: whether a drag actually resizes anything (the hit area, or the splitter's write fighting the TwoWay-bound column width).
- **What:** (1) a headless drag reproduction first: pointer-press/move/release scripts across each boundary prove the width deltas (or expose the defect - the T-144 harness lane's input injection); (2) the functional fix if a boundary fails the script (the splitter's write must win over or ride the TwoWay binding, the hit area stays honest); (3) the affordance: every splitter shows a visible grab handle - a thumb line discoverable at rest, a hover state on top (the design-tokens canon governs, no chrome noise).
- **Scope:** MainWindow.axaml (+ any ViewModel seam the drag fix needs) + the UI.Tests headless interaction pins; alpha.118 + the CHANGELOG section.
- **Acceptance:** the headless interaction scripts prove each boundary resizes under a simulated drag (width deltas before/after) and the widths persist across a settings round-trip; each splitter renders a discoverable grip with its hover state resolving.
- **Tests:** the UI.Tests headless splitter-lane pins (the drag scripts per boundary, the persistence round-trip, grip presence); the full gate stays green.
- **Status:** in-progress.

---

## T-173 - A detached order book frees its main-window column

- **Branch:** queued 2026-11-11 (filed from the user's ask: "if the order book is opened as a separate window, do not reserve its space in the main window").
- **Origin:** T-103's detach moves the book panel into its own window, but the main window's book column keeps its persisted width - a dead reserved gutter rides behind the detached window and the layout reads wrong. The AI panel already solves the same problem: its splitter track and column collapse with the panel's visibility (`AiSplitterColumnWidth`, the "hidden panel never leaves a dead gutter" precedent).
- **What:** the book column, its splitter track and the column widths collapse when `IsBookDetached` goes true - the workspace reclaims the space for the chart; on re-attach the persisted book width restores (the persistence keys ride untouched); the chart column keeps its star share.
- **Scope:** WorkspaceLayoutViewModel (a book splitter-track sibling of the AI precedent), MainWindow.axaml (the book column + splitter bindings), the UI.Tests pins; alpha.119 + the CHANGELOG section.
- **Acceptance:** headless pins prove the detached state leaves no reserved book gutter and the re-attached state restores the persisted width; the detach/attach cycle round-trips.
- **Tests:** the UI.Tests headless detach-lane pins (column collapse per state, the restore round-trip); the full gate stays green.
- **Status:** planned.

---

## T-174 - The standard pack grows the strategy catalog

- **Branch:** queued 2026-11-11 (filed from the user's ask: "add more strategy variants"; the satellite-first shape per the multi-repo rule).
- **Origin:** T-168 landed the strategy seam and the pack ships one strategy (sma-golden-cross). The user asks for more variants but flags uncertainty about what actually helps trading ("not very good at trading, so this is up in the air") - the supervisor's analytics call: ship a small set of well-understood classics over exotic auto-tuned shapes; every signal stays inert (contract 2.1.0: a pure walk, deterministic, long/flat/neutral) and verifiable in the backtest picker.
- **What:** three new strategies ride the Plugins package (0.2.0 -> 0.3.0), each modeled on the SmaCross template shape (a pure walk, a Neutral warmup, a guarded ctor, a stable id): EmaCross (the fast/slow EMA golden/death cross), RsiReversion (an oversold entry / overbought exit band logic), BollingerBreakout (a close outside the bands with a mid-band exit). The picker auto-discovers the pack (T-163's mechanics) - no consuming-side seam.
- **Scope:** the satellite repo first (the three strategies + their unit facts + the package bump), then the one-line consuming bump in-tree (PluginsVersion) + the CHANGELOG section (alpha.120); platform-specific and trading code never move to the satellite; the Abstractions contract stays 2.1.0.
- **Acceptance:** the satellite package tests prove each strategy's warmup neutrality, its entry/exit transitions and determinism (a second walk replays identical signals); in-tree the picker lists all four pack strategies.
- **Tests:** the satellite xUnit facts per strategy (warmup neutrality, the transition matrix, determinism); in-tree the gate stays green.
- **Status:** planned.

---

## T-175 - The startup binding sweep: the debug noise goes silent, the gesture hints render

- **Branch:** queued 2026-11-11 (filed from the user's debug-output report).
- **Origin:** the user's debug output shows two defect families at startup. (1) Binding noise: the menu detach commands/checkboxes and the chart HUD bind `$parent[Window].DataContext.*` while the window's DataContext is still null - `new MainWindow { DataContext = mainVm }` (App.axaml.cs:79) runs the object initializer AFTER the ctor's InitializeComponent, so the bindings first evaluate against null, log errors and self-heal only on the later assignment. (2) A functional defect: `InputGesture="{Binding Hotkeys.*Gesture}"` binds System.String values ('Ctrl+N', 'Ctrl+Shift+A', 'Ctrl+OemComma') to the KeyGesture-typed property - the conversion fails, so the menu gesture hints never render at all (not just debug noise).
- **What:** the window's DataContext lands before InitializeComponent (the instantiation order restructures around a main-view-model-carrying ctor) so the null-DataContext evaluation phase disappears for the menu/HUD lanes; the hotkey strings convert to KeyGesture for real - a string-to-KeyGesture converter or KeyGesture-typed hotkey properties, with an unparsable string surfacing as a logged warning rather than a silent dead hint (the trading-safety canon: no silent defaults).
- **Scope:** App.axaml.cs (the instantiation order), MainWindow.axaml(.cs) (the gesture bindings), the Hotkeys surface, the UI.Tests pins; alpha.121 + the CHANGELOG section.
- **Acceptance:** a headless startup pin captures the binding-error stream (the debug-output lane) and proves zero binding errors for the detach menu, the HUD and the gesture hints; the menu items render their gesture text; the smoke's file log shows no new ERR/FTL.
- **Tests:** the UI.Tests headless startup-lane pin (the binding-error capture + the gesture text presence); the full gate stays green.
- **Status:** planned.

---

## T-176 - The user-action E2E lane: real-window UI tests (the framework call)

- **Branch:** queued 2026-11-11 (filed from the user's correction: the "UI tests" asks meant tests that imitate user actions on the compiled app - the FlaUI shape - not the headless pins; the framework proposal rides this section).
- **Origin:** the board's UI-task acceptance tests are headless pins (the T-144 harness lane): in-process, input-injected, no real window. The user asked for a separate lane of user-action tests over the real application. The supervisor's framework call (researched 2026-11-11): FlaUI 6.0 (UIA3, MIT, .NET 10 supported since 6.0.0) drives the real process through Windows UI Automation; Avalonia exposes the full accessibility tree on Windows with `AutomationProperties.AutomationId` as the stable, localization-proof test identifier. Appium (Avalonia's own internal choice, per the official testing docs) is the heavier alternative: a server process plus drivers, its Windows engine rides the deprecated-maintenance WinAppDriver stack; it earns its cost only with macOS/Linux E2E ambitions.
- **What:** a separate E2E test project riding FlaUI.UIA3: launches the compiled app, attaches through the automation tree, walks the critical flows as a user (startup, the settings round-trip, a splitter drag, a detach cycle, the order ticket path, hotkeys); Windows-only tests (a trait gates them off elsewhere; the dev box and the smoke lane run Windows); the views gain `AutomationId`s where missing (the accessibility work already seeded many names); the headless interaction scripts (T-172's shape) stay the cheap in-gate tier, FlaUI is the real-process tier on top.
- **Scope:** a new tests/ZettaTrade.E2E.Tests project (xUnit + FluentAssertions, FlaUI.Core + FlaUI.UIA3 packages), AutomationId additions in views, no production behavior change; no alpha bump (tests-only).
- **Acceptance:** the E2E lane launches the real app and passes the critical-flow scripts locally; the gate runs it behind the Windows trait; the headless pins keep their lane.
- **Tests:** the E2E critical-flow scripts; the existing gate stays green.
- **Status:** planned.


