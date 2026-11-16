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
| T-041 Telegram trade notifications (user idea, post-beta) | feat/telegram-notifier | done | 0.2.1 |
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
| T-202 Chart templates (backlog sweep 2026-11-12) | feat/chart-templates | done | - |
| T-203 Indicator-based alerts (backlog sweep 2026-11-12) | feat/indicator-alerts | done | - |
| T-204 Alert mechanics 2.0: toasts, re-arm, sound (backlog sweep 2026-11-12) | feat/alerts-mechanics-2 | done | - |
| T-205 Streaming AI chat (backlog sweep 2026-11-12) | feat/ai-streaming | done | - |
| T-206 Persistent chat history (backlog sweep 2026-11-12) | feat/ai-chat-history | done | - |
| T-207 Second AI provider: OpenAI-compatible (backlog sweep 2026-11-12) | feat/ai-provider-openai | done | - |
| T-208 MCP server expansion: resources, prompts, tools (backlog sweep 2026-11-12) | feat/mcp-server-tools | done | - |
| T-209 Portfolio value history (backlog sweep 2026-11-12) | feat/portfolio-history | done | - |
| T-210 Export parity: positions CSV + trade statement (backlog sweep 2026-11-12) | feat/export-parity | done | - |
| T-211 Trade notes and tags (backlog sweep 2026-11-12) | feat/trade-notes | done | - |
| T-212 Backtest parameter optimization (backlog sweep 2026-11-12) | feat/backtest-optimize | done | - |
| T-213 Update checker (backlog sweep 2026-11-12) | feat/update-check | done | - |
| T-214 Tray icon (backlog sweep 2026-11-12) | feat/tray-icon | done | - |
| T-215 Profile backup and restore (backlog sweep 2026-11-12) | feat/profile-backup | done | - |
| T-216 Stabilize the dispatcher-pump drain flake: the theme-pin family's DrainTimeout misses (queued from the T-193/T-194 gate fights) | feat/flaky-theme-drain | done | - |
| T-217 Perf fixes: the streamed chat re-render throttle + the pinned stats equity snapshot (the 2026-11-13 review round) | feat/perf-fixes | done | - |
| T-218 Virtualize the AI chat transcript (queued from the 2026-11-13 review round) | feat/chat-virtualization | done | - |
| T-219 Stream the statement export past the whole-journal materialization (queued from the 2026-11-13 review round) | feat/statement-stream | done | - |
| T-220 Non-blocking alert beep (queued from the 2026-11-13 review round) | fix/alert-beep-async | done | - |
| T-221 Stabilize the E2E DetachCycle flake (queued from the 2026-11-13 review round) | fix/e2e-detach-flake | done | - |
| T-222 The settings window re-rides the approved variant-A mockup: the rail's title-bar strip + icons + note, the compact per-section cards, the pinned footer chrome, the 880x640 frame (the user-reported T-184 gap) | fix/settings-rail-fix | done | - |
| T-223 The settings pages collapse into the variant-A section grammar: the card headers become toggles (four open by default), the rail captions ride BodyFontSize, the quiet controls ride the Livery 0.1.13 surface-family keys (the user-reported T-222 gap) | feat/settings-collapse | done | - |
| T-224 The nightly gitleaks scan stops tripping on the dummy fixtures: the 2026-11-14 triage allowlists the confirmed literals (the a11y template key + the redactor/log-tail test shapes) with the audit record (the user-reported security-pipeline failure) | fix/gitleaks-allowlist | done | - |
| T-225 The Trading menu's Price alerts item stops killing the process: the pickers' TwoWay SelectedItem init race wrote null into the editor's options and the kind flags dereferenced it (the user-reported NullReferenceException crash); the editor family gets null-rejecting setters + null-safe flags and the same-shape sweep runs over every other window | fix/alerts-window-crash | done | - |
| T-226 Startup latency: the walk batches the five SQLite stores concurrently + the chart prewarms from the persisted symbol while the instruments fetch is in flight (the user-reported blank-chart wait) | feat/startup-latency | done | - |
| T-227 Exchange API-key authorization in the settings window: per-venue keys persist through ISecretStore, adapters read them at start, the Alpaca data adapter authenticates when keys are present and quiets its no-key noise when absent (the user's ask, the T-226 profiling follow-up) | feat/exchange-auth-settings | done | - |
| T-228 One instruments-info fetch per exchange per session: the startup warm-up and the chart's bootstrap share the provider cache instead of double-fetching (the T-226 profiling follow-up) | feat/instruments-fetch-dedupe | done | - |
| T-229 The plugin scanner stops logging every non-plugin dll as a failure: the scan's honest drop is a debug line while the folder's real packs still load (the T-226 profiling follow-up) | fix/not-a-plugin-log-noise | done | - |
| T-230 The update checker treats the private feed's no-release 404 as the honest no-update quiet state instead of an error entry every launch (the T-226 profiling follow-up) | fix/update-checker-404 | done | - |
| T-231 The watchlist ticker stream retries its stream failures with a bounded backoff instead of dying stale, and the Binance instruments parse takes the unrecognized quote-asset drop at the debug level (filed from the user's log analysis) | feat/watchlist-ticker-retry | done | - |
| T-233 The adapters' construction-time Disconnected snapshots stop carrying real-exchange endpoints into a demo-only run's log: a connection-state line rides a real lifecycle transition, and the soak census's host list covers every shipped venue (the T-232 finale's soak finding) | fix/adapter-state-log-leak | done | - |
| T-234 The window family rides the variant-A grammar: the dialog family resizes (the fixed-frame era ends app-wide), the confirm strips unify on the settings shape (the quiet button first, the ONE primary last, the 90 strip), the ticket/wizard/license bodies card, the toolbar widths and the pre-strip gaps normalize (the user's T-223 follow-up; renumbered off T-231 - the number collided with the landed ticker-retry task) | feat/windows-unify | done | 0.1.0-alpha.175 |
| T-235 The controls answer the pointer: the kit's icon buttons speak a visible hover/pressed tint, the pairs transfer arrows read as buttons, the alerts editor's captions clear their fields (the Edge combo stops clipping), and the screener rows stretch to the header plan without the button chrome (the user's T-223 follow-up sweep; renumbered off T-232 - the number was skipped on the synced board) | feat/affordances | done | 0.1.0-alpha.176 |
| T-236 Beta-prep finale: the full E2E/soak pass + the release readiness checklist recorded on the beta-prep tip (the beta-prep wave's closer; renumbered off T-232 - the number collided with the landed controls task) | feat/beta-prep-finale | done | - |
| T-237 The cold start meets the 3 s budget on the soak lane: the spawn-to-window wall (runtime spin-up + service assemblies + the window build) gets the dedicated startup-optimization pass first noted at the T-109 close (the T-236 readiness record's disclosed breach; renumbered off T-234 - the number was taken by the landed windows-unify task) | feat/startup-optimization | done | - |
| T-238 The beta flip: the version line moves to plain 0.2.x - the first beta 0.2.0, the third number rides the app-changing landings (the user's board decision; the signing flip stays a follow-up per docs/SECURITY.md) | feat/beta-flip | done | 0.2.0 |
| T-239 Multi-timeframe trend table in the screener: per-symbol bull/bear/range states across 15m/1h/4h/1d computed from the candle cache (the analytics sweep 2026-11-14) | feat/mtf-trend-table | done | 0.2.11 |
| T-240 Funding-rate history chart + the funding-threshold alert kind for perps (the analytics sweep 2026-11-14) | feat/funding-history | done | 0.2.12 |
| T-241 Open-interest history as a chart pane under the price series for perps (the analytics sweep 2026-11-14) | feat/oi-history-pane | done | 0.2.13 |
| T-242 Liquidation feed: the venues' free forced-order WS streams aggregated into the tape and chart markers (the analytics sweep 2026-11-14) | feat/liquidation-feed | done | 0.2.14 |
| T-243 Estimated liquidation levels overlay computed locally from OI deltas + the venues' public leverage brackets (the analytics sweep 2026-11-14) | feat/liquidation-levels | done | 0.2.15 |
| T-244 CVD pane: cumulative volume delta summed from the tape's aggressor side (the analytics sweep 2026-11-14) | feat/cvd-pane | planned | — |
| T-245 Whale-print filter + the big-trade alert kind in the tape (the analytics sweep 2026-11-14) | feat/whale-filter | planned | — |
| T-246 Book imbalance indicator derived from the depth stream (the analytics sweep 2026-11-14) | feat/book-imbalance | planned | — |
| T-247 Session analytics: daily/weekly opens, the opening range, session shading (the analytics sweep 2026-11-14) | feat/session-levels | planned | — |
| T-248 Volume profile annotations: POC/value-area labels + the naked-POC track (the analytics sweep 2026-11-14) | feat/profile-annotations | planned | — |
| T-249 Auto S/R levels from swing pivots, proximity-clustered and pan-stable (the analytics sweep 2026-11-14) | feat/auto-levels | planned | — |
| T-250 Squeeze detector (Bollinger inside Keltner) rides the standard pack (the analytics sweep 2026-11-14) | feat/squeeze-detector | planned | — |
| T-251 Trade-statistics deepening: MAE/MFE, R-multiples, time-of-day performance, Sharpe/Sortino (the analytics sweep 2026-11-14) | feat/stats-deepening | done | 0.2.3 |
| T-252 Correlation matrix over the cached candles (the analytics sweep 2026-11-14) | feat/correlation-matrix | planned | — |
| T-253 Cross-venue spread + the perp-spot basis view over the three shipped adapters (the analytics sweep 2026-11-14) | feat/venue-spread | planned | — |
| T-254 Backtest robustness: Monte Carlo over the trade sequence + walk-forward splits (the analytics sweep 2026-11-14) | feat/backtest-robustness | done | 0.2.4 |
| T-255 AI trade review: journal-backed post-trade analysis through the chat (the analytics sweep 2026-11-14) | feat/ai-trade-review | planned | — |
| T-256 Windows installer: Inno Setup over the T-125 artifacts (the beta-2 sweep 2026-11-15) | feat/windows-installer | done | - |
| T-257 macOS .dmg + Linux AppImage/.deb packaging (the beta-2 sweep 2026-11-15) | feat/mac-linux-packaging | done | — |
| T-258 Auto-update: download, verify, staged swap, rollback (the beta-2 sweep 2026-11-15) | feat/auto-update | done | 0.2.5 |
| T-259 GitHub Pages publish automation + the beta site content (the beta-2 sweep 2026-11-15) | feat/pages-publish | done | — |
| T-260 Notification sink abstraction + Telegram (the parked T-041 activated) (the beta-2 sweep 2026-11-15) | feat/notify-sinks | done | - |
| T-261 Webhook sinks: Discord, Slack, ntfy (the beta-2 sweep 2026-11-15) | feat/webhook-sinks | done | T-260 |
| T-262 Matrix + email sinks (the beta-2 sweep 2026-11-15) | feat/matrix-email-sinks | done | T-260 |
| T-263 Backtest sweep retains metrics rows plus one best report, not P full reports (the perf sweep 2026-11-15) | feat/backtest-perf | done | — |
| T-264 Backtest replay reads signals while the feed runs (the perf sweep 2026-11-15) | feat/backtest-replay | done | - |
| T-265 Backtest load assembles the walk-back pages in one pass (the perf sweep 2026-11-15) | feat/zt-backtest-load | done | - |
| T-266 UpdateStage venue-consent fact: the order-sensitive timing flake (filed 2026-11-15) | feat/update-consent-flake | done | T-258 |

## Code review 2026-11-04 — findings index (source of T-046…T-062)

Full analysis: `docs/CODE_REVIEW.md` (56 findings: 3 Critical, 23 Major, 30 Minor, all verified
against the code at cited lines; every entry carries a stable `BUG-NNN` id). The findings are
filed as T-046…T-062, one task per code area; the three Critical findings each get their own
task. `docs/CODE_REVIEW.md` is the canon of definitions and per-finding line references; when a
task lands, its fixing agent flips the covered `BUG-NNN` statuses to Fixed there, in the same
commit series as the work. Areas explicitly cleared by the review (no bug found) are listed at
the end of that file, so no effort is duplicated.


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

## Analytics backlog sweep 2026-11-14 — gap analysis (source of T-239…T-255)

The user's ask, continuing the 2026-11-12 sweep: the app's analytical tooling is thin beyond
the shipped indicator/screen/stat basics — what analytical tools are still missing? A read-only
walk over the shipped inventory (the board and archive, the indicator pack, the screener, the
stats window, the Bybit perp adapter's funding/OI surface, the AI chat) mapped eight gap lanes,
filed as T-239…T-255 — every gap checked against the code and the archive before filing (no
MTF trend read, no funding/OI history, no liquidation data anywhere, no volume-delta analytics,
no session levels, no POC/value-area annotations, no automatic levels, no MAE/MFE or
R-multiples, no correlation view, no cross-venue spread, no Monte Carlo or walk-forward, no AI
trade review). Nothing requires a paid subscription: the lanes ride data the app already
streams, free public endpoints (the venues' funding/OI/leverage-bracket REST and forced-order
WS streams), or pure computation over the candle cache (T-085). The batch shape: the
trend/derivatives lane first (the user's own ask), then order flow, sessions and levels,
stats/quant, cross-venue, and the AI-native tail. All rows start `planned`; the human
prioritizes the lanes. Filed in the zt-analytics-sweep worktree, deliberately left unlanded at
the user's call; refiled off the synced board top (`bd4c514`) as T-239…T-255 — the concurrent
T-236…T-238 landings (the beta-prep finale, the cold-start pass, the 0.2.0 beta flip) took the
original numbers.

---

## T-244 — CVD pane: cumulative volume delta

- **Branch:** `feat/cvd-pane` off `develop` (queued 2026-11-14, the analytics sweep).
- **Origin:** the tape (T-087) carries the aggressor side; nothing sums it.
- **What:** a cumulative buy-minus-sell volume series from the trade stream as an indicator pane,
  a session-anchored reset option, an honest state where the adapter serves no trade data.
- **Scope:** the pure aggregator + stream wiring + the pane renderer, resx en+ru, tests.
- **Acceptance:** CVD equals a hand-summed fixture; the pane aligns to the chart axis.
- **Tests:** aggregation goldens (buy/sell/neutral), the session reset, the pane alignment.

---

## T-245 — Whale-print filter and the big-trade alert kind

- **Branch:** `feat/whale-filter` off `develop` (queued 2026-11-14, the analytics sweep).
- **Origin:** every print rides the tape (T-087); large ones drown.
- **What:** a notional threshold filter with large prints highlighted, and a new alert kind —
  trade above threshold — riding the alert engine; thresholds per symbol or absolute, persisted.
- **Scope:** the tape VM filter + the alert kind + settings keys, resx en+ru, tests.
- **Acceptance:** client-side only (no extra subscriptions); thresholds persist across restarts.
- **Tests:** filter goldens, the alert firing, persistence.

---

## T-246 — Book imbalance indicator

- **Branch:** `feat/book-imbalance` off `develop` (queued 2026-11-14, the analytics sweep).
- **Origin:** depth flows (T-011, T-088); no derived signal.
- **What:** a bid/ask volume imbalance ratio over a configurable depth window (a pure computation
  over the book snapshot) shown as a pane or a book header chip, with optional smoothing.
- **Scope:** the pure calculator + the display, resx en+ru, tests.
- **Acceptance:** the 60 fps / 1000-level book target holds with the indicator live (no per-frame
  allocations on the hot path).
- **Tests:** calculator goldens, a perf smoke over the 1000-level synthetic book.

---

## T-247 — Session analytics: opens, opening range, session shading

- **Branch:** `feat/session-levels` off `develop` (queued 2026-11-14, the analytics sweep).
- **Origin:** levels are manual-only (T-091); the calendar (T-037) paints no session context.
- **What:** daily/weekly open lines, an opening-range band (configurable minutes), and
  Asia/Europe/US session shading (UTC-anchored for 24/7 venues, the venue calendars for stocks)
  as toggleable overlays.
- **Scope:** the calendar-riding compute + the renderer + settings, resx en+ru, tests.
- **Acceptance:** the anchors match the T-037 calendar exactly; DST and weekend fixtures hold.
- **Tests:** anchor goldens over DST/weekend calendars, the overlay alignment.

---

## T-248 — Volume profile annotations: POC, value area, naked POC

- **Branch:** `feat/profile-annotations` off `develop` (queued 2026-11-14, the analytics sweep).
- **Origin:** T-093 paints the histogram; no POC/value-area read.
- **What:** POC/VAH/VAL labels on the visible-window profile (the T-093 compute gains the
  annotations, the standard 70% expansion rule) plus a naked-POC track — session POCs persist
  until price revisits.
- **Scope:** the profile compute extension (pure) + the label rendering, resx en+ru, tests.
- **Acceptance:** the value area covers 70% of volume by the standard rule; naked POCs clear on
  revisit.
- **Tests:** POC/VA goldens, the naked-POC lifecycle, label placement.

---

## T-249 — Auto S/R levels from swing pivots

- **Branch:** `feat/auto-levels` off `develop` (queued 2026-11-14, the analytics sweep).
- **Origin:** horizontal levels are drawn manually only (T-091).
- **What:** automatic horizontal levels from swing pivots (configurable lookback and strength),
  proximity-clustered, drawn as a read-only overlay (never written into the user's drawings),
  stable across pans.
- **Scope:** the pivot detector + clustering (pure) + the overlay, resx en+ru, tests.
- **Acceptance:** the levels do not flicker when the visible window pans.
- **Tests:** detector goldens, clustering, the pan stability.

---

## T-250 — Squeeze detector (Bollinger inside Keltner) in the standard pack

- **Branch:** `feat/squeeze-detector` off `develop` (queued 2026-11-14, the analytics sweep).
- **Origin:** volatility-compression reads are absent; the pack rides the package mechanics
  (T-163).
- **What:** an indicator plugin marking bars where Bollinger bands sit inside Keltner channels
  (configurable periods/multipliers) with fired markers on release, as a pane plugin riding the
  SDK contract.
- **Scope:** the Plugins satellite (the package bumps there first, the one-line consuming bump
  here), tests in the satellite.
- **Acceptance:** the math matches reference values on fixtures.
- **Tests:** reference-value goldens, the pane integration in the satellite's harness.

---

## T-252 — Correlation matrix over the cached candles

- **Branch:** `feat/correlation-matrix` off `develop` (queued 2026-11-14, the analytics sweep).
- **Origin:** the compare overlay (T-124) is pairwise and visual only.
- **What:** an N×N Pearson correlation panel over a chosen watchlist/screener set and window (log
  returns from the T-085 cache), computed off-UI; a cell routes into the compare view; missing
  coverage is an honest dash.
- **Scope:** the pure compute + the panel, resx en+ru, tests.
- **Acceptance:** a 20-symbol matrix computes without freezing the UI.
- **Tests:** correlation goldens, the coverage-gap states, the worker budget.

---

## T-253 — Cross-venue spread and the perp-spot basis view

- **Branch:** `feat/venue-spread` off `develop` (queued 2026-11-14, the analytics sweep).
- **Origin:** three venues ship (Binance, Bybit, OKX); nothing compares the same symbol across
  them, nor perp against spot.
- **What:** a spread view for a symbol across adapters (last/bid/ask deltas in bps) and a
  perp-spot basis series where both categories exist; market-data only — no order routing
  implications, no risk-surface change.
- **Scope:** the aggregation service over the existing facades + the panel, resx en+ru, tests.
- **Acceptance:** works on any venue subset — a missing venue is a dash, never an error.
- **Tests:** aggregation goldens, the missing-venue honesty, the basis math.

---

## T-255 — AI trade review: journal-backed post-trade analysis

- **Branch:** `feat/ai-trade-review` off `develop` (queued 2026-11-14, the analytics sweep).
- **Origin:** the journal, the notes/tags (T-211) and the AI chat exist; nothing joins them
  analytically.
- **What:** a review action that composes the selected session/trade's journal data (fills, PnL,
  notes, the T-114 market context) into a structured review prompt over the existing provider
  surface; the output lands in the chat only; strictly read-only — no trading decisions, no
  auto-posting.
- **Scope:** the prompt/service + the chat handoff, resx en+ru, tests.
- **Acceptance:** works with the local model and the OpenAI-compatible provider; a provider
  failure is a localized error, never silence.
- **Tests:** prompt-composition goldens, the provider-failure matrix, the read-only guarantee.

---

## Beta-2 sweep 2026-11-15 — distribution + notifications (source of T-256…T-262)

The user's ask, the second backlog cut on top of the analytics sweep (T-239…T-255): the beta
line has opened (T-238, 0.2.0) — distribution and notifications are next. A fact-check against
the shipped inventory shaped the batch as deltas, not rewrites: the release pipeline already
publishes three-OS artifacts with sha256 (T-125, signing/notarization deliberately deferred
past the alpha with the checklist in docs/SECURITY.md), the landing rides the Vite docs-site
(T-126) yet no Pages deploy workflow or gh-pages branch exists on origin, the update checker
only surfaces a chip and a link (T-213, the documented pre-beta posture), and messaging is
only the parked T-041. Filed as T-256…T-262: installers, the auto-update loop, the Pages
publication, and the notification-sink lane (abstraction + Telegram, then the webhook batch
and Matrix/email); the batch originally carried the beta cut itself — it landed separately as
T-238 by the concurrent session before this batch could. Messenger verdicts recorded with the
user: Discord, Slack and ntfy webhooks plus Matrix and email ride free public APIs or the
user's own credentials — WhatsApp stays out (its official API is a paid Business flow;
unofficial bridges are middlemen) and Signal stays out (no official bot API) per the T-041
no-middleman canon. The messenger sinks stay free (the user's call). All rows start `planned`;
the human prioritizes the lanes.

---

---
