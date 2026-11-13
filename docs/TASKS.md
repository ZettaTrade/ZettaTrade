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
| T-172 The workspace's resize handles: every boundary drags for real (the grabbed line follows the pointer) and shows a visible grip | feat/splitter-grips | done | - |
| T-173 A detached order book frees its main-window column: no dead reserved width rides behind the detached window (the user report) | feat/book-detach-space | done | - |
| T-174 The standard pack grows the strategy catalog: the classic swing/breakout/reversion set rides the package (the user ask, scoped by the supervisor's analytics) | feat/strategy-catalog | done | T-168 |
| T-175 The startup binding sweep: the debug-output binding errors go silent and the menu gesture hints render (the user's debug log) | fix/binding-noise | done | - |
| T-176 The user-action E2E lane: real-window UI tests ride FlaUI (the user's framework proposal, the Windows tier) | feat/e2e-flaui-lane | done | - |
| T-177 The signal rail scrolls its overflow and drags its height (the user ask: a scrollbar and a splitter for the rail) | feat/rail-scroll-splitter | done | T-112, T-172 |
| T-178 The drawings delete affordance: the selected drawing's removal is discoverable (the user ask: how do you delete a drawn line - a button) | feat/drawings-delete | done | T-091 |
| T-179 The Skia header bands breathe: the tape's and the book's column headers stop sitting flush on their first rows (the user ask: padding between the table header and the data) | feat/tape-header-gap | done | T-103 |
| T-180 The theme preview swatches mirror the Accent again: dark, light and oled stop advertising the retired colors (the review finding) | feat/theme-preview-mirror | done | - |
| T-181 The order ticket's CTA and side segments answer hover and pressed: the window's class styles grow the interaction variants (the review finding) | feat/ticket-cta-states | done | - |
| T-182 The small-surface strays ride the scale and the palette: the off-scale margins and the bare Opacity dimmers move onto named keys (the review findings) | feat/surface-strays | done | - |
| T-183 The button kit: quiet resting, a soft-tint hover, one primary per window, the corner tokens (the user-approved design pass) | feat/button-kit | done | - |
| T-184 The settings window rides the category rail and the section cards: no whole-window scroll, the headers read as panels (the user-approved design pass) | feat/settings-rail-cards | done | - |
| T-185 The MainWindow's XAML re-indents: the 410-line DockPanel block stops sitting flush with its parent (the review finding) | refactor/main-xaml-indent | done | - |
| T-186 Bracket orders: entry + attached TP/SL (backlog sweep 2026-11-12) | feat/bracket-orders | done | - |
| T-187 SL/TP management from the positions panel (backlog sweep 2026-11-12) | feat/position-sltp | done | - |
| T-188 Kill switch: cancel all + flatten all (backlog sweep 2026-11-12) | feat/kill-switch | done | - |
| T-189 Daily loss limit in the risk governor (backlog sweep 2026-11-12) | feat/daily-loss-limit | done | - |
| T-190 Honest TIF simulation in the paper adapter (backlog sweep 2026-11-12) | feat/tif-simulation | done | - |
| T-191 Order flags: post-only, reduce-only (backlog sweep 2026-11-12) | feat/order-flags | done | - |
| T-192 Paper account management: reset, starting balance (backlog sweep 2026-11-12) | feat/paper-account | done | - |
| T-193 Binance spot testnet trading adapter (backlog sweep 2026-11-12) | feat/binance-trading | done | - |
| T-194 Third venue: OKX market data (backlog sweep 2026-11-12) | feat/okx-adapter | done | - |
| T-195 Market screener panel (backlog sweep 2026-11-12) | feat/screener | done | - |
| T-196 User-managed watchlists (backlog sweep 2026-11-12) | feat/watchlist-edit | done | - |
| T-197 Futures contour MVP: Bybit linear perps (backlog sweep 2026-11-12) | feat/futures-mvp | done | - |
| T-198 Stocks MVP: a market-data adapter (backlog sweep 2026-11-12) | feat/stocks-mvp | done | - |
| T-199 Indicator panes: oscillator sub-charts (backlog sweep 2026-11-12) | feat/indicator-panes | done | - |
| T-200 Drawing tools wave 2: zone, text, measure ruler (backlog sweep 2026-11-12) | feat/drawing-tools-2 | done | - |
| T-201 Multi-chart grid layout (backlog sweep 2026-11-12) | feat/multi-chart-grid | done | - |
| T-202 Chart templates (backlog sweep 2026-11-12) | feat/chart-templates | in-progress | - |
| T-203 Indicator-based alerts (backlog sweep 2026-11-12) | feat/indicator-alerts | planned | - |
| T-204 Alert mechanics 2.0: toasts, re-arm, sound (backlog sweep 2026-11-12) | feat/alert-mechanics | planned | - |
| T-205 Streaming AI chat (backlog sweep 2026-11-12) | feat/ai-streaming | planned | - |
| T-206 Persistent chat history (backlog sweep 2026-11-12) | feat/ai-chat-history | planned | - |
| T-207 Second AI provider: OpenAI-compatible (backlog sweep 2026-11-12) | feat/ai-providers | planned | - |
| T-208 MCP server expansion: resources, prompts, tools (backlog sweep 2026-11-12) | feat/mcp-server-tools | planned | - |
| T-209 Portfolio value history (backlog sweep 2026-11-12) | feat/portfolio-history | planned | - |
| T-210 Export parity: positions CSV + trade statement (backlog sweep 2026-11-12) | feat/export-parity | planned | - |
| T-211 Trade notes and tags (backlog sweep 2026-11-12) | feat/trade-notes | planned | - |
| T-212 Backtest parameter optimization (backlog sweep 2026-11-12) | feat/backtest-optimize | planned | - |
| T-213 Update checker (backlog sweep 2026-11-12) | feat/update-check | planned | - |
| T-214 Tray icon (backlog sweep 2026-11-12) | feat/tray-icon | planned | - |
| T-215 Profile backup and restore (backlog sweep 2026-11-12) | feat/profile-backup | planned | - |
| T-216 Stabilize the dispatcher-pump drain flake: the theme-pin family's DrainTimeout misses (queued from the T-193/T-194 gate fights) | feat/flaky-theme-drain | done | - |

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

## UI review 2026-11-12 — findings index (source of T-180…T-182)

The read-only review pass that followed the 2026-11-10 design batch's landing (T-150…T-162 all
implemented, the Livery and Plugins satellites split out, the board at T-179): a re-walk of the
shipped palettes, the order ticket, the kit consumers and the small windows against the landed
canon, plus the full test suite (2492 green). Three findings survive — two verified defects (the
stale preview swatches in three theme files, the ticket's dead hover/pressed states) and one
cosmetic strays sweep — filed as T-180…T-182; all rows start `planned`.

## T-181 - The order ticket's CTA and side segments answer hover and pressed

- **Branch:** feat/ticket-cta-states, worktree zt-ticket-cta-states, based on develop@686567d
  (claimed 2026-11-12 per standing directive 6 — the review findings batch, T-180 landed;
  the queued branch name fix/ticket-hover-response replaced by the real one).
- **Origin:** NewOrderDialogWindow carries no `:pointerover`/`:pressed` styles at all, and its
  window-scoped `Button.cta`/`Button.side` class styles set Background — applied after the app
  styles they override Livery's generic `Button:pointerover`/`Button:pressed`
  (Livery Controls.axaml:29/33). The primary CTA and the segmented Buy/Sell pair — the
  highest-stakes buttons in the app — give zero visual response under the pointer; Livery's own
  classed buttons (`.strip-icon`, `.icon`) set the pattern with explicit variants.
- **What:** the ticket's window styles grow the interaction variants — `Button.cta:pointerover`
  and `:pressed`, `Button.side:pointerover` and `:pressed` (+ the disabled treatment if the base
  lacks one) — riding palette keys per the kit's strip-icon precedent; a fleet-wide source pin
  enforces the shape (every window-scoped Button class style ships its interaction siblings),
  so any other window with the same hole reports itself.
- **Scope:** NewOrderDialogWindow.axaml styles (+ whatever windows the fleet pin reports);
  alpha.126 + the CHANGELOG section. The ticket's trading path untouched (the safety canon
  applies verbatim).
- **Acceptance:** hovering and pressing the CTA and both side segments visibly shifts state in
  dark and light; the hover deltas' contrast stays inside the T-151 battery's rules; behavior
  parity — the existing ticket tests stay green untouched.
- **Tests:** the fleet-wide source pin (class style → interaction siblings); the existing
  ticket goldens.
- **Status:** done. Canon: the claim `f5ad169`, the feat `2bb65a1`, this close commit follows;
  ships as 0.1.0-alpha.126 with the matching CHANGELOG section. The window-scoped class styles
  paint their state fills after the app styles, so Livery's generic `Button:pointerover`
  (Accent) / `Button:pressed` (Border) never reach a classed button — the defect's mechanism,
  confirmed by the kit's own precedents (the strip-icon/copy-pin discipline, the row grammar's
  explicit variants, the review's own verification). The shipped shift language: hover = the
  kit's emphasis ring (`BorderBrush = Highlight` — the row grammar's own hover border and the
  T-110 focus language, identical rings), pressed = the strip's dim step (`Opacity = 0.8`).
  The state fills NEVER leave — Buy == CandleUp and Sell == Error in every shipped theme (no
  second state shade exists to shift to, so the variants ride BorderBrush/Opacity only; the
  scored chip pairs stay exactly where the T-152 battery pinned them, the T-151 hover rules
  hold by construction). The four ticket blocks: `Button.side:pointerover`/`:pressed` and
  `Button.cta:pointerover`/`:pressed`, ordered after their family's state blocks. The disabled
  treatment needed nothing (the task's conditional: no `:disabled` override exists anywhere in
  the app or the kit — the Fluent base carries the disabled look, and the variants set no rest
  Opacity to shadow it). The fleet pin reported exactly two more holes and both closed:
  SettingsWindow's `Button.pair-star` (Background=Transparent defeated the generic hover —
  the star now brightens TextSecondary → TextPrimary on hover and holds through the press,
  the copy-pin's quiet-control language; the `.on` block rides AFTER the variants so the
  armed star stays gold) and RowGrammar's `Button.row-action.secondary` (its Transparent
  rest defeated the generic pressed — the family grew an explicit `Button.row-action:pressed`
  = the generic Border fill, ordered after the secondary's rest and before the destructive
  blocks so the ghost's own pressed keeps winning). The pin
  (`InteractionVariantPinsTests`, 4 facts): the fleet walk over every axaml under
  src/ZettaTrade.App (floor >20 files — a shrunk walk is a false green), the rule = a
  classed Button style with no pseudo-class whose block paints Background demands the
  family's (first class token) `:pointerover` and `:pressed` selectors somewhere in the file,
  the negative fixture replays the ticket's pre-fix shape and the rule fails naming the
  family and both missing siblings, the ticket's four variants pinned to the exact setters
  (ring + 0.8 dim, never a Background setter), and the fleet fixes' ordering pinned (the
  star's `.on` last, the row-action pressed after the secondary and before the destructive).
  No inline Button Background exists in the fleet (the sweep — the class-style shape is the
  only hole shape). Acceptance evidence: GATE PASSED on the feat content (`-AllowProtected`;
  the known warnings trio, NO test retry; the HIGH-RISK flag on NewOrderDialogWindow.axaml
  is the gate's Order-adjacent routing — styles only, the trading path untouched); the launch
  smoke: `ProductVersion 0.1.0-alpha.126+2bb65a1...`, alive 15 s, closed cleanly, the day-log
  slice (70 lines) carries ZERO ERR/FTL/Exception entries; the pins lane 64/64, App.Tests
  1571/1571 first-try. Hazards: none new; the T-183 button-kit redesign (the satellite's
  generic hover re-tune) will restyle the GENERIC language later — these window-scoped
  variants stay local and palette-keyed, restyling with it.

## T-182 - The small-surface strays ride the scale and the palette

- **Branch:** feat/surface-strays, worktree zt-surface-strays, based on develop@197300a
  (claimed 2026-11-12 per standing directive 6 — the review findings batch, T-181 landed;
  the queued branch name fix/surface-strays replaced by the real one).
- **Origin:** the cosmetic strays the review swept up: off-scale spacing (the 6px margins and a
  `Spacing="14"` in the small windows, BalancesPanelWindow first) outside the T-155 scale
  (4/8/12/16/24), and bare `Opacity="0.65"` dimmers riding no palette key or state class — the
  watchlist row's 24h volume (MainWindow.axaml), the chart toolbar's VWAP and VolumeProfile
  toggles (ChartPanelView.axaml) whose on-state stays equally dimmed.
- **What:** the off-scale margins map onto the nearest scale step (no layout ripple beyond the
  stray's own pixel); each dimmer moves onto a named state class or palette-derived key
  (the T-150 derived-key shape) so themes retint the rest state deliberately and the toggles'
  on-state reads strong.
- **Scope:** the named stray sites + whatever the grep sweep of off-scale margins and bare
  opacity literals reports across the App views; Livery Tokens if a derived key lands there;
  alpha.127 + the CHANGELOG section.
- **Acceptance:** no off-scale margin or bare Opacity literal remains in the touched windows
  (the source pin); the dimmed elements read in dark, light and oled (the contrast pins);
  visual parity otherwise.
- **Tests:** the source pins over the spacing literals and bare opacity values; the existing
  headless bounds pins stay green.
- **Status:** done. Canon: the claim `dee72cb` (the dead session's pre-death commit, adopted
  by the takeover), the feat `77450f6`, this close commit follows; ships as 0.1.0-alpha.127
  with the matching CHANGELOG section. The takeover adopted the dead session's uncommitted
  draft (19 views + CompositionRoot + two new files) and reconciled it: the draft carried
  CP-1251 mojibake (em-dashes, arrows, the star misread — the recorded PowerShell route) in
  8 views, repaired byte-exactly by reversing the round trip (chars → CP-1251 bytes → UTF-8
  decode; LogViewerWindow and PositionsPanelWindow were double-layered and needed a second
  pass); every repaired line matches HEAD byte-for-byte. The mapping: off-scale margins
  onto the nearest T-155 step (the overlay-drop anchors, the log viewer's clearance and the
  pair-star's glyph nudge documented as position, not spacing); the dimmers onto named keys
  — the palette's TextSecondary ink (the watchlist volume, the ticket's sizer support
  texts), the chart toolbar's quiet `overlay-toggle` class with its own armed `.on` sibling
  (the T-181 mechanism; no Background setter, so the generic hover/focus stay alive), and
  the workspace scrims onto the derived `ScrimBrush` (ScrimBrushPublisher, IAsyncInitializable —
  the applied Background's RGB under the veil alpha 191 = the 0.75 wash, republished on
  every theme apply, the T-150 derived-key shape; the palette still carries no translucent
  key). The pin battery (SurfaceStrayPinsTests, 7 facts, first-try green): the spacing walk
  over every shipped view (the anchors documented by role, a shrunk walk is a false green),
  the bare-opacity sweep, the dimmer-site pins (the toggles' rest/armed blocks and their
  ordering), the scrim-site pins, the shipped-theme contrast battery (TextSecondary ≥ 4.5:1
  over background and surface in all five themes — pre-checked 5.49–7.60:1), the veil
  derivation math, and the real-apply-path fact (every theme serves the veil from its own
  Background). GATE PASSED attempt 1 (`-AllowProtected`; the known warnings trio — SCOPE,
  gitleaks, the App.Tests load-sensitive family ate its flaky retry, recorded, not fixed).
  Smoke PASSED on the T-166 functional bar: title held through the 12 s watchlist hold,
  0 ERR/FTL, 0 ticker failures, ProductVersion 0.1.0-alpha.127+77450f65…, palette refreshed,
  strategies+indicators provided, no leftovers, sandbox deleted. HIGH-RISK reviewer note
  for the human (gate-flagged): NewOrderDialogWindow, AmendOrderDialogWindow,
  ConfirmationDialogWindow and OrderHistoryWindow touched in markup only (spacing/opacity
  swaps; the trading path untouched). Hazards: none new.

---

## Design pass 2026-11-12 — variant A approved (source of T-183…T-184)

The user's UI complaints after the 2026-11-12 review: the buttons look clunky (the solid Accent
hover flash reads as the main offender), the settings window forces scrolling, and the section
headers read as plain bold text with a big top gap — they should read as panels. Three option
sets were proposed (quiet/primary buttons, a category rail vs densified tabs, section cards vs
header bars); the user approved **variant A** against a mockup that carried the exact dark.json
palette — an HTML mockup shared outside the repository (the image generators were quota-gated
that day), whose live hover let the user feel the resting/hover/primary states instead of
looking at a static picture. The approved shape files as T-183 (the button/kit half, riding the
Livery satellite) and T-184 (the settings window's rail + cards); no separate findings doc —
each section below carries its own origin. All rows start `planned`.

---

## Backlog sweep 2026-11-12 — gap analysis (source of T-186…T-215)

The user's ask: analyze the repository and identify the functionality still missing. A
read-only walk over the shipped inventory (the code, the board, the README, the CHANGELOG, the
plugin SDK surface) surfaced 30 gaps, filed as T-186…T-215 — every gap verified against the
code before filing (grep-level checks: no bracket orders, no SL/TP edit route from the
positions panel, no daily-loss governor, no post-only/reduce-only flags, no paper-account
reset, no tray icon, no streaming chat, the watchlist not user-editable, TIF recorded but not
simulated in the paper engine). The batch shape: the trading half first (the widest hole —
protective orders, risk, venues), then market data, the chart stack, alerts, AI/MCP, portfolio
and reporting, and the platform tail. All rows start `planned`; the human prioritizes the
lanes; the sweep's tasks queue behind the planned UI batch (T-176…T-185) by default. The
parked T-041 (Telegram) stays queued and rides the T-204 event surface when opened.

---

## T-193 — Binance spot testnet trading adapter

- **Branch:** `zt-binance-trading` off `develop` (the 2026-11-12 backlog sweep; claim commit
  `3c0190a`, base `2440250`).
- **Origin:** the sweep — real (testnet) trading exists only for Bybit; Binance rides read-only
  market data.
- **What:** a `BinanceTradingAdapter` mirroring the Bybit trading adapter's shape: signed REST
  order create/cancel/query on the Binance spot testnet, private WS fills where the venue
  serves them; arming gated by the same three conditions (the adapter selected, the endpoint
  record flagged testnet, the explicit testnet-trading toggle); credentials only from
  `ISecretStore`; unsupported order types refuse before the venue with a clear reason.
- **Scope:** Infrastructure adapter + the adapter-kit conformance coverage, settings keys, resx
  en+ru, the next free alpha bump + the CHANGELOG section.
- **Acceptance:** the conformance battery passes against the loopback stub; the arming gate
  matches the Bybit adapter's discipline verbatim; no key ever reaches a log or an argument.
- **Tests:** the adapter-kit battery for the new adapter; the arming-gate pins.
- **Status:** done — shipped as 0.1.0-alpha.137. The adapter rides a delegated market-data face
  (the venue's own `BinanceAdapter`), a listen-key private stream (issue → dial
  `{PrivateWsUrl}/{listenKey}` → receive-only → the 30-minute keepalive loop), HMAC-SHA256
  hex over the exact query string (`X-MBX-APIKEY` header), LIMIT GTC only with post-only on
  the venue's `GTX` token (`LIMIT_MAKER` reads back onto GTC+flag), strict status mapping
  (transitional statuses refuse), honest-empty positions, free+locked balances, arming via the
  generic venue resolver in `TradingSurfaceService` (no new settings keys), the venue journal
  merged over both venues' streams, the kill-switch sweep extended to the new venue. Kit
  battery 24/24 through the new delegated depth profile + symbol-first book-topic template
  (the honest pack declares blank-endpoints guards only — `BinanceAdapter` has no
  unknown-symbol/bars-limit guards); Infrastructure 22 signed-wire facts + 4 private-stream
  e2e facts; App arming pins for both venues incl. the cross-venue picker pin. No resx keys
  were added: the adapter's refusal/error strings stay English per the T-127 precedent and no
  new user-facing UI string was introduced.

## T-202 — Chart templates

- **Branch:** queued 2026-11-12 (the 2026-11-12 backlog sweep).
- **Origin:** the sweep — the chart face (type, indicators, scale) is per-symbol state; no
  named presets exist.
- **What:** save the current chart face (type, scale mode, indicators JSON, VWAP/volume-profile
  toggles) as a named template; apply a template to the active symbol or as the default for new
  symbols; templates live in the settings schema (versioned; export rides T-100).
- **Scope:** the chart VM + the settings schema, a template manager in the chart toolbar's
  flyout, resx en+ru, the next free alpha bump + the CHANGELOG section.
- **Acceptance:** save, apply, rename and delete round-trip; applying resets exactly the
  template's facets and nothing else; the default-for-new-symbols rule holds.
- **Tests:** the template round-trip battery; the toolbar pins.
- **Status:** planned.

## T-203 — Indicator-based alerts

- **Branch:** queued 2026-11-12 (the 2026-11-12 backlog sweep).
- **Origin:** the sweep — alert kinds are Price and PercentMove only.
- **What:** alert conditions over enabled indicator values — crosses above/below a level (RSI
  70/30, a price cross of an MA) — evaluated on the ticker and candle-close streams the alerts
  service already rides; the alert editor gains the indicator condition kind with honest
  capability disclosure (only loaded plugin indicators qualify; a missing plugin pauses the
  alert, it does not die).
- **Scope:** the alerts engine + the editor UI, the roster schema versioning, resx en+ru, the
  next free alpha bump + the CHANGELOG section.
- **Acceptance:** an indicator alert fires on the crossing candle close exactly once per
  definition; the editor lists only the loaded plugins' parameters; the disabled-plugin state
  reads honestly in the roster.
- **Tests:** the condition-evaluation battery over canned streams; the editor capability pins.
- **Status:** planned.

## T-204 — Alert mechanics 2.0: toasts, re-arm, sound

- **Branch:** queued 2026-11-12 (the 2026-11-12 backlog sweep).
- **Origin:** the sweep — a fire today beeps and flips a status; no toast, no repeat semantics,
  no sound choice.
- **What:** a fired alert surfaces as a notification-center toast (the T-097 lane, respecting
  DND and the source mutes); re-arm options beyond today's one-shot (per-cross re-arm, an
  N-minute cooldown); an optional per-alert sound beyond the single beep, best-effort and never
  blocking the trading thread.
- **Scope:** the alerts service (fire semantics) + the notification bridge, the editor's
  re-arm controls, settings keys, resx en+ru, the next free alpha bump + the CHANGELOG section.
- **Acceptance:** a fire produces the toast unless DND/mutes swallow it; the re-arm modes
  behave per contract; the sound path degrades honestly on platforms without it.
- **Tests:** the fire-semantics matrix (one-shot/re-arm/cooldown × DND); the toast-bridge pins.
- **Status:** planned. (The parked T-041 Telegram sink rides this event surface when opened.)

## T-205 — Streaming AI chat

- **Branch:** queued 2026-11-12 (the 2026-11-12 backlog sweep).
- **Origin:** the sweep — the Ollama route is one-shot non-streaming; the Stop button aborts a
  whole response.
- **What:** SSE streaming from the chat endpoint (`stream: true`), tokens appended live into
  the bubble; Stop cuts the stream mid-flight cleanly; a non-streaming fallback rides a
  settings key for endpoints that cannot stream.
- **Scope:** ZettaTrade.AI (the streaming reader), the bubble's incremental render, the
  settings key, tests, the next free alpha bump + the CHANGELOG section.
- **Acceptance:** a streamed reply renders progressively; Stop leaves no partial-corrupt
  state; the fallback path keeps today's behavior verbatim.
- **Tests:** the loopback SSE stub battery (chunk order, abort, malformed chunks).
- **Status:** planned.

## T-206 — Persistent chat history

- **Branch:** queued 2026-11-12 (the 2026-11-12 backlog sweep).
- **Origin:** the sweep — the chat history lives in memory only and dies with the process.
- **What:** conversations persist to SQLite; several named conversations with a session picker;
  a conversation exports to Markdown; the market-context injection and the consent chip (T-114)
  semantics are unchanged.
- **Scope:** ZettaTrade.AI persistence + the App chat VM (the conversation list), resx en+ru,
  the next free alpha bump + the CHANGELOG section.
- **Acceptance:** the history survives restart; a picked conversation restores its bubbles; the
  export writes a readable Markdown file; the consent chip still gates the context injection.
- **Tests:** the persistence round-trip battery; the export golden.
- **Status:** planned.

## T-207 — Second AI provider: OpenAI-compatible

- **Branch:** queued 2026-11-12 (the 2026-11-12 backlog sweep).
- **Origin:** the sweep — Ollama is the only provider; the canon keeps local-first as the
  default posture.
- **What:** a provider seam in the AI chat: the shipped default stays Ollama; an
  OpenAI-compatible base URL + model + API key becomes selectable (the key rides
  `ISecretStore` — saved immediately, passed via stdin never argv); the request/response
  mapping is honest per provider; no cloud-by-default posture change.
- **Scope:** ZettaTrade.AI (the provider abstraction) + the settings keys (`AI.Provider`,
  `AI.OpenAiCompatible.*`), the settings window's Integrations section, resx en+ru, the next
  free alpha bump + the CHANGELOG section.
- **Acceptance:** both providers answer over loopback stubs; the key never reaches a log,
  stdout or an argument; the default configuration behaves exactly as today.
- **Tests:** the provider stub batteries; the secret-handling pins.
- **Status:** planned.

## T-208 — MCP server expansion: resources, prompts, tools

- **Branch:** queued 2026-11-12 (the 2026-11-12 backlog sweep).
- **Origin:** the sweep — the built-in server registers five trading tools, no resources, no
  prompts.
- **What:** MCP resources (a read-only portfolio snapshot, the workspace state), prompt
  templates (a market brief, a portfolio review), and new tools — `get_order_history`,
  `get_indicators` (the enabled setups), `place_alert`/`cancel_alert`, `get_trade_stats`;
  trading-adjacent tools ride the same risk/confirmation gates; the journaling-adapter
  discipline (T-133) holds.
- **Scope:** ZettaTrade.AI (the server host registrars), resx en+ru, the next free alpha bump
  + the CHANGELOG section.
- **Acceptance:** the JSON-RPC surface lists the new resources, prompts and tools; a client
  (the app's own MCP client in tests) walks them; an alert placed via MCP appears in the
  manager and fires normally.
- **Tests:** the server e2e battery over the loopback transport (the T-020/T-050 harness
  shape).
- **Status:** planned.

## T-209 — Portfolio value history

- **Branch:** queued 2026-11-12 (the 2026-11-12 backlog sweep).
- **Origin:** the sweep — the PnL engine (T-082) and the stats equity curve (T-083) ship; the
  balances panel has no time dimension.
- **What:** periodic portfolio-value snapshots per trading account (paper-first) persisted to
  SQLite; the balances panel grows a value-over-time curve (the existing equity-curve widget)
  and an allocation list by asset; the snapshot cadence is bounded and skips when nothing
  changed materially.
- **Scope:** Infrastructure (the snapshot service + store), the balances VM, resx en+ru, the
  next free alpha bump + the CHANGELOG section.
- **Acceptance:** the curve survives restart and reads the snapshot history; the allocation
  sums to the portfolio value; the snapshots stay cheap (a bounded write cadence).
- **Tests:** the snapshot cadence battery; the store migration pins.
- **Status:** planned.

## T-210 — Export parity: positions CSV + trade statement

- **Branch:** queued 2026-11-12 (the 2026-11-12 backlog sweep).
- **Origin:** the sweep — orders, fills and candles export (T-084); positions and the portfolio
  do not, and no full statement exists.
- **What:** a positions CSV export and a one-shot trade statement bundle (orders + fills +
  realized PnL + a balances snapshot, CSV plus a JSON manifest) from the trade statistics
  window; the T-084 CSV writer is reused, never re-implemented.
- **Scope:** Infrastructure CSV service extensions + the stats VM routes, resx en+ru, the next
  free alpha bump + the CHANGELOG section.
- **Acceptance:** both exports open cleanly in a spreadsheet; the statement honors the date
  filter; an empty range exports nothing rather than a broken file.
- **Tests:** the export goldens (header stability, encoding, the empty-range negative).
- **Status:** planned.

## T-211 — Trade notes and tags

- **Branch:** queued 2026-11-12 (the 2026-11-12 backlog sweep).
- **Origin:** the sweep — the journal records the what; nothing records the why.
- **What:** user notes and tags on closed trades (the journal entities), editable from the
  trade statistics and history surfaces; the stats filters gain tag lanes; the export includes
  notes and tags (riding T-210's columns when both land).
- **Scope:** the journal entities + the store migration, the stats/history VMs, resx en+ru,
  the next free alpha bump + the CHANGELOG section.
- **Acceptance:** notes and tags survive restart; the tag filter narrows the stats honestly;
  the CSV columns join the statement only with data present (the existing headers stay
  stable).
- **Tests:** the store migration battery; the filter pins.
- **Status:** planned.

## T-212 — Backtest parameter optimization

- **Branch:** queued 2026-11-12 (the 2026-11-12 backlog sweep).
- **Origin:** the sweep — the backtester runs one parameterization per run.
- **What:** a grid sweep over a strategy's declared numeric parameters (the plugin descriptor's
  settings shape), producing a ranked result table (net, drawdown, win rate, profit factor)
  with a click-through to the single-run report; progress and cooperative cancel ride the
  existing backtest events; walk-forward is explicitly out of scope (a later task).
- **Scope:** ZettaTrade.Backtest (the sweep engine) + the window's results view, resx en+ru,
  the next free alpha bump + the CHANGELOG section.
- **Acceptance:** a two-parameter grid returns a ranked, deterministic table (the byte-stable
  report discipline per row); cancel stops between parameterizations, never mid-run.
- **Tests:** the sweep determinism battery; the cancel/progress pins.
- **Status:** planned.

## T-213 — Update checker

- **Branch:** queued 2026-11-12 (the 2026-11-12 backlog sweep).
- **Origin:** the sweep — no updater and no version awareness beyond the status chip.
- **What:** a periodic (and manual) check against the GitHub Releases API comparing the running
  version; a newer release surfaces as a status-bar chip and a notification with the release
  link; no auto-download, no auto-install — the alpha posture, documented.
- **Scope:** App update service (offline-honest), the settings key (`App.UpdateCheck`), resx
  en+ru, the next free alpha bump + the CHANGELOG section.
- **Acceptance:** a newer tag surfaces the chip and the link; an unreachable network degrades
  silently to a logged warning; the off switch silences the check entirely.
- **Tests:** the loopback releases-stub battery (parse, compare, the degraded states).
- **Status:** planned.

## T-214 — Tray icon

- **Branch:** queued 2026-11-12 (the 2026-11-12 backlog sweep).
- **Origin:** the sweep — no tray presence exists.
- **What:** a tray icon with a minimal menu (show/hide the main window, the DND toggle, exit);
  an option to keep running in the tray on close (off by default); fired alerts route through
  the tray when the window is hidden (Windows first; Linux/macOS ride the platform support
  honestly or degrade to the notification center).
- **Scope:** App tray service + the window-close policy, settings keys
  (`App.TrayCloseBehavior`), resx en+ru, the next free alpha bump + the CHANGELOG section.
- **Acceptance:** close-to-tray keeps the session alive and the single-instance hand-off
  intact; exit from the tray fully exits; the off state changes nothing.
- **Tests:** the close-policy headless pins; the single-instance interaction pin.
- **Status:** planned.

## T-215 — Profile backup and restore

- **Branch:** queued 2026-11-12 (the 2026-11-12 backlog sweep).
- **Origin:** the sweep — settings import/export ships (T-100); the SQLite stores (orders, PnL,
  drawings, candles) have no backup route.
- **What:** a one-shot profile backup (settings + the SQLite stores + drawings, a versioned
  archive) and a restore with a pre-restore summary; secrets stay on the machine by design —
  the archive carries no `ISecretStore` content, documented in SECURITY.md.
- **Scope:** Infrastructure backup service + the store paths, the settings window's
  Integrations route, resx en+ru, the next free alpha bump + the CHANGELOG section.
- **Acceptance:** a backup→restore round-trip restores the data stores; a corrupt archive
  refuses honestly; a pin proves the archive never carries secret material.
- **Tests:** the round-trip battery; the corrupt-archive negative; the no-secrets pin.
- **Status:** planned.


