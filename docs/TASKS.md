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
| T-176 The user-action E2E lane: real-window UI tests ride FlaUI (the user's framework proposal, the Windows tier) | feat/e2e-flaui-lane | planned | - |
| T-177 The signal rail scrolls its overflow and drags its height (the user ask: a scrollbar and a splitter for the rail) | feat/rail-scroll-splitter | planned | T-112, T-172 |
| T-178 The drawings delete affordance: the selected drawing's removal is discoverable (the user ask: how do you delete a drawn line - a button) | feat/drawings-delete-affordance | planned | T-091 |
| T-179 The Skia header bands breathe: the tape's and the book's column headers stop sitting flush on their first rows (the user ask: padding between the table header and the data) | fix/skia-header-padding | planned | T-103 |
| T-180 The theme preview swatches mirror the Accent again: dark, light and oled stop advertising the retired colors (the review finding) | fix/theme-preview-mirror | planned | - |
| T-181 The order ticket's CTA and side segments answer hover and pressed: the window's class styles grow the interaction variants (the review finding) | fix/ticket-hover-response | planned | - |
| T-182 The small-surface strays ride the scale and the palette: the off-scale margins and the bare Opacity dimmers move onto named keys (the review findings) | fix/surface-strays | planned | - |
| T-183 The button kit: quiet resting, a soft-tint hover, one primary per window, the corner tokens (the user-approved design pass) | feat/button-kit | planned | - |
| T-184 The settings window rides the category rail and the section cards: no whole-window scroll, the headers read as panels (the user-approved design pass) | feat/settings-rail-cards | planned | T-183 (soft) |

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

## T-176 - The user-action E2E lane: real-window UI tests (the framework call)

- **Branch:** queued 2026-11-11 (filed from the user's correction: the "UI tests" asks meant tests that imitate user actions on the compiled app - the FlaUI shape - not the headless pins; the framework proposal rides this section).
- **Origin:** the board's UI-task acceptance tests are headless pins (the T-144 harness lane): in-process, input-injected, no real window. The user asked for a separate lane of user-action tests over the real application. The supervisor's framework call (researched 2026-11-11): FlaUI 6.0 (UIA3, MIT, .NET 10 supported since 6.0.0) drives the real process through Windows UI Automation; Avalonia exposes the full accessibility tree on Windows with `AutomationProperties.AutomationId` as the stable, localization-proof test identifier. Appium (Avalonia's own internal choice, per the official testing docs) is the heavier alternative: a server process plus drivers, its Windows engine rides the deprecated-maintenance WinAppDriver stack; it earns its cost only with macOS/Linux E2E ambitions.
- **What:** a separate E2E test project riding FlaUI.UIA3: launches the compiled app, attaches through the automation tree, walks the critical flows as a user (startup, the settings round-trip, a splitter drag, a detach cycle, the order ticket path, hotkeys); Windows-only tests (a trait gates them off elsewhere; the dev box and the smoke lane run Windows); the views gain `AutomationId`s where missing (the accessibility work already seeded many names); the headless interaction scripts (T-172's shape) stay the cheap in-gate tier, FlaUI is the real-process tier on top.
- **Scope:** a new tests/ZettaTrade.E2E.Tests project (xUnit + FluentAssertions, FlaUI.Core + FlaUI.UIA3 packages), AutomationId additions in views, no production behavior change; no alpha bump (tests-only).
- **Acceptance:** the E2E lane launches the real app and passes the critical-flow scripts locally; the gate runs it behind the Windows trait; the headless pins keep their lane.
- **Tests:** the E2E critical-flow scripts; the existing gate stays green.
- **Status:** planned.

---

## T-177 - The signal rail scrolls its overflow and drags its height

- **Branch:** queued 2026-11-11 (filed from the user's ask: add a scrollbar to the signal rail and a splitter too).
- **Origin:** the T-112 signal rail rides a fixed `MaxHeight="172"` cap inside the chart panel with its rows on a bare vertical StackPanel - no ScrollViewer, so a busy rail's older readings clip out of sight; and the cap is a hardcoded constant, the user cannot grow the rail when it matters (the workspace's other boundaries all drag since T-172).
- **What:** the rows surface scrolls (the overflow reachable, the newest-first order preserved); the rail's boundary becomes draggable - the T-172 ColumnResizeHandle's row-wise sibling (a RowResizeHandle) writes a rail height that clamps sanely and persists through the settings pipeline (the T-038/T-172 persistence discipline); the chart above stays the star (the rail grows within its band, never eats the chart).
- **Scope:** ChartPanelView.axaml (+ the rail height VM property and the persistence seam it rides), the App.Tests headless pins; alpha.122 + the CHANGELOG section.
- **Acceptance:** a headless pin proves the overflow scrolls (rows beyond the viewport reachable) and a drag at the rail boundary resizes the persisted rail height (round-trip); the chart's hit area never loses its full band.
- **Tests:** the App.Tests headless rail-lane pins (the scroll reachability, the drag round-trip); the full gate stays green.
- **Status:** planned.

---

## T-178 - The drawings delete affordance: the selected drawing's removal is discoverable

- **Branch:** queued 2026-11-11 (filed from the user's ask: how do you delete drawn lines from the chart - maybe a button should be added).
- **Origin:** the T-091 drawings layer already routes removal - a click selects the drawing and the Delete key removes it (Escape cancels the in-flight placement, else clears the selection) - but the route is undiscoverable: the hotkey registry (the "?" cheat sheet, the settings tab) carries only the four window-level actions, nothing names the chart's own Delete/Escape routes, and no visible affordance offers the removal. The user's question is the discoverability gap.
- **What:** a discoverable removal affordance on the selection (a visible delete button where the selection already shows - the drawings toolbar strip or at the selection itself), plus the chart's own Delete/Escape routes surfacing where the user reads them (the cheat sheet's drawing lane or the affordance's tooltip). The removal's semantics stay untouched (the store, the layer, the metrics keep the T-091 discipline; deleting an annotation is not a trading action - no confirmation).
- **Scope:** CandlestickChart (the affordance's anchor and the key routes), the drawings toolbar surface, the cheat-sheet/tooltip wording, the App.Tests headless pins; alpha.123 + the CHANGELOG section.
- **Acceptance:** a headless pin selects a drawing, proves the affordance visible and wired to the same removal route the Delete key drives, and proves the chart's keys read where the user looks for them.
- **Tests:** the App.Tests headless drawings-lane pins (the affordance presence + the route pairing); the full gate stays green.
- **Status:** planned.

---

## T-179 - The Skia header bands breathe: the column headers stop sitting flush on their first rows

- **Branch:** queued 2026-11-11 (filed from the user's ask: add padding between the table header and the data, and look for similar places).
- **Origin:** the trade tape's column headers (Time/Price/Size, T-103) are drawn with their text baseline exactly on the header band's bottom edge - the first trade row starts at that same edge, so the caption glyphs touch the row glyphs with zero gap; the order book's column headers (Price/Cumulative volume) repeat the identical geometry. The user's screenshots are both defect sites.
- **What:** a shared draw fix shape - the column-header baseline lifts to a named constant inside the band (the band height unchanged, no layout ripple) in `TradeTapeWidget` and `OrderBookWidget`; a render-geometry pin asserts baseline &lt; first-row top for both widgets; a sweep of the remaining Skia surfaces (DepthChart, Heatmap - no header bands) recorded so the pattern has no other hiding places.
- **Scope:** the two widget draw sites, the App.Tests geometry pin; alpha.124 + the CHANGELOG section.
- **Acceptance:** the pin proves a visible gap between the column captions and the first row on both widgets; the tape's and the book's band heights stay untouched (no row-count change at a fixed panel size).
- **Tests:** the App.Tests widget geometry pin (both widgets); the full gate stays green.
- **Status:** planned.

---

## UI review 2026-11-12 — findings index (source of T-180…T-182)

The read-only review pass that followed the 2026-11-10 design batch's landing (T-150…T-162 all
implemented, the Livery and Plugins satellites split out, the board at T-179): a re-walk of the
shipped palettes, the order ticket, the kit consumers and the small windows against the landed
canon, plus the full test suite (2492 green). Three findings survive — two verified defects (the
stale preview swatches in three theme files, the ticket's dead hover/pressed states) and one
cosmetic strays sweep — filed as T-180…T-182; all rows start `planned`.

## T-180 - The theme preview swatches mirror the Accent again (dark, light, oled)

- **Branch:** queued 2026-11-12 (the 2026-11-12 review findings batch).
- **Origin:** the A11Y-audit convention makes the fourth preview swatch the Accent mirror
  (trading-pro.json and midnight.json follow it), but the T-151 Accent re-tune never reached the
  mirrors: dark.json advertises the retired hover blue `#0f3460` (Accent `#2563eb`), light.json a
  grey `#dee2e6` (Accent `#3b82f6`), oled.json a near-black `#1a1a1a` (Accent `#2563eb`) — the
  theme picker's swatch strip sells colors the theme no longer owns.
- **What:** the three previewColors arrays mirror the shipped Accent values again; a pin battery
  walks every shipped theme asserting the mirror convention (the A11Y audit's own rule, enforced
  in the gate from now on).
- **Scope:** themes/dark.json, themes/light.json, themes/oled.json, the UI.Tests pin battery;
  alpha.125 + the CHANGELOG section.
- **Acceptance:** the theme picker's swatches read the live Accent in every shipped theme; a
  stale mirror fails the pin naming the file and the pair.
- **Tests:** the preview-mirror pin battery over the real theme files (the negative fixture
  included); the existing theme-identity pins stay green.
- **Status:** planned.

## T-181 - The order ticket's CTA and side segments answer hover and pressed

- **Branch:** queued 2026-11-12 (the 2026-11-12 review findings batch).
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
- **Status:** planned.

## T-182 - The small-surface strays ride the scale and the palette

- **Branch:** queued 2026-11-12 (the 2026-11-12 review findings batch).
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
- **Status:** planned.

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

## T-183 - The button kit: quiet resting, a soft-tint hover, one primary per window, the corner tokens

- **Branch:** queued 2026-11-12 (the 2026-11-12 design pass, variant A approved by the user).
- **Origin:** every button rests as a Surface fill + hairline border and flashes a **solid
  Accent fill on hover** (Livery Controls.axaml:29-35) — the flash the user names as the main
  offender; no hierarchy exists (the settings' "Open themes folder" and the ticket's "Place"
  read identically), and the corner radius is not tokenized at all (no Corner keys in
  Tokens.axaml). The approved variant A: quiet by default, tinted on hover, one primary per
  window.
- **What:** the Livery button vocabulary grows the approved shape — the resting state goes
  transparent/barely-tinted over a hairline Border-family stroke; hover becomes a **soft Accent
  tint** (~12%, a published derived key, the T-150 derived-key shape), pressed a stronger tint,
  keyboard focus keeps the Highlight border; a `.primary` class (solid Accent fill, the
  readable foreground the T-152 publisher scores) marks the ONE main action per window; corner
  radius and row-action sizing publish as tokens (CornerM = 4) and Button/ComboBox/TextBox
  adopt them for one consistent rounding. The windows then adopt: each window's main action
  goes primary, the rest quiet.
- **Scope:** Livery Themes (the Controls.axaml button family + the Tokens.axaml corner/padding
  keys) — the package change rides the satellite repo per the multi-repo rule (bump Livery
  there, land, then the one-line consuming bump); the App windows' class adoption in the
  consuming bump's wake; resx untouched.
- **Acceptance:** no button flashes a solid Accent fill on hover (source pin); every window
  carries at most one primary action; the hover pairs' contrast passes the T-151 battery; no
  bare corner-radius literals remain in the adopted surfaces.
- **Tests:** the contrast pins for the new pairs (the hover tint vs Surface, primary vs its
  foreground); source pins (the solid-Accent hover out, the radius literals onto the tokens);
  Livery's own gate rides the satellite.
- **Status:** planned.

## T-184 - The settings window rides the category rail and the section cards

- **Branch:** queued 2026-11-12 (the 2026-11-12 design pass, variant A approved by the user);
  soft blocker T-183 (the buttons land first).
- **Origin:** the settings window (760x720, four folded tabs) scrolls its long tabs whole —
  Integrations and Workspace wrap their bodies in ScrollViewers, so the user scrolls to reach
  anything; the section headers are bare bold TextBlocks with a top margin, reading as plain
  text; the user asked for panels that read as panels. The approved variant A mockup fixes all
  three: a left category rail, section cards, compact headers.
- **What:** the tab strip becomes a left category rail (~190px: the retired tab captions
  Appearance/Indicators/Integrations/Workspace become the rail items; the selected item rides
  the soft Accent tint + the 3px Accent tick, keyboard navigable per the T-110 discipline); the
  window grows taller-and-wider (~880x640) so each page fits without the whole-body scroll —
  the long lists keep their own scrollports, Save/Close stay pinned; every section becomes a
  Surface card (1px Border stroke, the CornerM radius) whose header is a compact label (Caption
  11, uppercase, letter-spaced, TextSecondary) with a 3px Accent tick — the card+header shape
  ships once (the Livery kit if shared, else the App themes), the settings' sections consume.
- **Scope:** SettingsWindow.axaml (+ the rail's selected-index state on SettingsViewModel, the
  TabControl index binding re-rides), the Livery kit if the section-card shape is shared; resx:
  the rail captions reuse the existing tab keys; no settings behavior change — every binding,
  command and persistence seam untouched.
- **Acceptance:** no whole-window scroll at the default size (the headless bounds pin in the
  T-144 lane); every section reads as a card with the compact header; the rail keyboard-navigates;
  the pinned Save/Close footer survives the re-layout.
- **Tests:** the headless bounds pin (no scroll at the default size); the existing settings
  goldens stay green (behavior parity); a source pin that the section headers ride the shared
  style.
- **Status:** planned.


