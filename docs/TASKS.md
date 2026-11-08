# ZettaTrade — Task Board

Statuses: `planned` → `in-progress` → `review` → `done`. One task per feature. Agents work only in their own worktree/branch, within the listed scope. Branches are created with `pwsh scripts/new-worktree.ps1 -Name <name> -Base develop` (feature branches base on `develop`; the script's default base is `main`). A task's detail section leaves this file for `docs/TASKS-ARCHIVE.md` once the task is done and merged; the one-line board row stays.

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
| T-090 Y-axis scale modes: log and percent | feat/chart-scales | queued | - |
| T-091 Drawing tools: trend line, horizontal level, fib retracement | feat/draw-tools | queued | - |
| T-092 Standard indicator pack (EMA, RSI, MACD, Bollinger… as bundled plugins) | feat/indicator-pack | done | - |
| T-093 VWAP + volume profile overlay | feat/vol-vwap | done | - |
| T-094 Chart export: PNG snapshot + CSV candles | feat/chart-export | queued | - |
| T-095 Global symbol palette (Ctrl+K) across adapters | feat/symbol-palette | done | - |
| T-096 Price and percent-move alerts with a manager | feat/price-alerts | queued | - |
| T-097 Notification center: toasts, history, DND | feat/notif-center | done | - |
| T-098 Adapter health strip in the status bar | feat/health-strip | done | - |
| T-099 First-run wizard (theme, adapter, symbol) | feat/first-run-wizard | review | - |
| T-100 Settings import/export + factory reset | feat/settings-port | queued | - |
| T-101 Log viewer window + global crash guard | feat/log-viewer | queued | - |
| T-102 Hotkey manager + cheat sheet overlay | feat/hotkey-manager | queued | - |
| T-103 Detachable panels: multi-window workspaces | feat/multi-window | queued | - |
| T-104 Single instance hand-off + CLI symbol open | feat/single-instance | queued | - |
| T-105 Reconnect policy hardening: backoff, jitter, watchdog | feat/reconnect-policy | done | - |
| T-106 Exchange rate limiter with per-endpoint budgets | feat/rate-limiter | done | - |
| T-107 Adapter conformance test kit (shared battery) | feat/adapter-kit | done | - |
| T-108 Performance HUD overlay (fps, memory, WS latency) | feat/perf-hud | queued | - |
| T-109 Soak harness and budget gates (cold start < 3 s, memory < 500 MB) | feat/soak | queued | - |
| T-110 Accessibility pass (keyboard, automation peers, contrast) | feat/a11y | queued | - |
| T-111 Strategy contracts MVP: candle-close signals (no auto-trading) | feat/strategies | queued | - |
| T-112 Signal markers on the chart + signal rail | feat/signal-markers | queued | T-111 |
| T-113 Backtest harness over stored candles | feat/backtest | queued | T-111 + T-085 |
| T-114 AI market-context injection into the chat | feat/ai-context | done | - |
| T-115 User manual + screenshots (docs-only) | docs/user-manual | queued | - |
| T-116 Adapter WS E2E tests flake under solution-parallel load: the 10 s EventWait trips on scheduler starvation (queued from the T-073 verification) | feat/ws-e2e-budget | done | - |
| T-117 Secret scanner: gitleaks in CI + one-shot full-history audit | feat/secret-scanner | done | - |
| T-118 Trading-safety guidance in the README: read-only keys, IP restriction, no-withdrawal, testnet-first | feat/safety-readme | done | - |
| T-119 First-run disclaimer in the UI («not investment advice») + the explicit alpha posture | feat/first-run-disclaimer | done | - |
| T-130 Extract Livery into a standalone private repo (MIT kept) and consume it as a package | feat/livery-split | queued | - |
| T-131 Plugin SDK seam: versioned abstractions that out-of-tree plugins build against | feat/plugin-sdk | queued | - |
| T-132 Closed-source plugin packaging: signed private-repo artifacts + local license-key activation | feat/plugin-packaging | queued | T-131 |
| T-133 MCP paper fills enter the journal: tool-placed fills ride the ledger the panels and PnL read | feat/mcp-journal | done | T-082 |
| T-134 Supervisor-mode skill (OpenCode): the standing directives load only on explicit invocation | feat/supervisor-skill | done | - |

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

- **Branch:** queued 2026-11-04 (user idea: «в дальнейшем я хочу чтобы программа отправляла информацию о сделках в телеграм (но это не срочно, после доведения до бета)») — deliberately parked after the beta milestone.
- **Origin:** fills and order events land only in the journal and the logs today; the trader wants them pushed where he actually reads.
- **What:** a notification sink that sends order/fill event messages (symbol, side, price, quantity, exchange, paper/live tag) to a user's Telegram chat through a bot token stored in `ISecretStore` (a token never travels as a command-line argument — stdin/secret-store only, the standing security rules). Opt-in per event type (fills, cancels, rejections), async and BEST-EFFORT: a failed send logs a warning and never blocks, retries or corrupts the trading flow; no third-party middleman services — the token is the user's own bot.
- **Scope:** a new notification service in Infrastructure riding the existing trading/journal event surface, the settings keys (`Notifications.*`), `ISecretStore` token, resx en+ru message templates, tests.
- **Acceptance:** e2e probe against a LOOPBACK Telegram API stub (the T-032 harness pattern): a fill event produces the expected message POST; a stub 500/timeout degrades to a logged warning with the trading flow untouched; the flag off or the token missing means quietly no sink.
- **Tests:** the message composition golden (en/ru templates), the event filtering, the best-effort failure matrix (timeout, 5xx, malformed response).

## T-090 — Y-axis scale modes: log and percent

- **Branch:** queued 2026-11-06 (supervisor idea at the T-074…T-115 backlog cut).
- **Origin:** log is mandatory for long ranges; % change is the relative read; both absent.
- **What:** a log price mode with a strict positive-domain guard (invalid candles clamp visibly, never default), and a percent-change mode anchored to the first visible close; axis labels/formatting follow the T-037 time-grid counterpart on the price side; per-symbol persistence.
- **Scope:** Charting scale transforms within the existing axis pipeline, tests.
- **Acceptance:** wheel zoom stays stable across modes; switching never loses the visible window.
- **Tests:** transform goldens, domain edge cases, the mode-switch window guarantee.

## T-091 — Drawing tools: trend line, horizontal level, fib retracement

- **Branch:** queued 2026-11-06 (supervisor idea at the T-074…T-115 backlog cut).
- **Origin:** annotation is the most-requested chart feature family in terminal land; nothing exists.
- **What:** an overlay layer (Skia) above candles: trend line, horizontal level, fib retracement; draw/drag/edit/delete, snapping to price/time grid; per (symbol, interval) persistence in the local store; drawings never leave the process (no sync, no upload).
- **Scope:** Charting overlay + tool palette UI, persistence, resx en+ru, tests.
- **Acceptance:** drawings stay anchored across pan/zoom and reloads (T-033/T-036 lessons respected).
- **Tests:** coordinate anchoring roundtrip, hit-test/drag golden, persistence reload.

## T-094 — Chart export: PNG snapshot + CSV candles

- **Branch:** queued 2026-11-06 (supervisor idea at the T-074…T-115 backlog cut).
- **Origin:** sharing charts (reports, chat with T-041's future posts) needs images; exports of a chart are nothing.
- **What:** a chart menu action rendering the current frame via Skia at a fixed scale with a symbol/timeframe watermark, saving to the user-picked path; the visible-range CSV rides T-084's writer contract.
- **Scope:** Charting + a menu item, resx en+ru, tests.
- **Acceptance:** the export matches the on-screen frame (same lens, same annotations state).
- **Tests:** pixel-diff smoke on a seeded chart, watermark presence, path/error handling.

## T-096 — Price and percent-move alerts with a manager

- **Branch:** queued 2026-11-06 (supervisor idea at the T-074…T-115 backlog cut).
- **Origin:** alerts are a terminal baseline; nothing exists (T-041's Telegram sink is a different layer — delivery).
- **What:** local evaluations on the existing candle/book streams (no extra polls): crossing a price, % move within a window; a manager table (create/edit/snooze/delete), persistence, cross-session reload; a firing alert beeps + toasts (to T-097 when it lands); triggering an order from a signal is explicitly out of scope — no auto-trading without `IConfirmationService`.
- **Scope:** alert engine (a service), manager UI, `Settings.*` keys, resx en+ru, tests.
- **Acceptance:** edge semantics exact (above/at/below), no alert lost across restart if persisted.
- **Tests:** trigger edge matrix, persistence reload, sound-failure degrade.

## T-099 — First-run wizard (theme, adapter, symbol)

- **Branch:** queued 2026-11-06 (supervisor idea at the T-074…T-115 backlog cut).
- **Origin:** a cold start lands on a bare workspace; onboarding is nothing.
- **What:** an opt-in three-step wizard — theme picker, adapter pick (testnet/demo defaults, keys stay in `ISecretStore` via the stdin path, never argv), first symbol pick; stored through the reactive settings service; skippable and re-runnable from settings; demo is the default lane so nothing real is touched without explicit choice.
- **Scope:** wizard VM/views, settings keys, resx en+ru, tests.
- **Acceptance:** skipping leaves a working demo configured app.
- **Tests:** step navigation golden, settings-write integration, the skip path.

## T-100 — Settings import/export + factory reset

- **Branch:** queued 2026-11-06 (supervisor idea at the T-074…T-115 backlog cut).
- **Origin:** no portability of a tuned setup; settings live only in the local store.
- **What:** export the user-facing settings subset (themes are files already; secrets are NEVER exported) as a versioned JSON; import with schema validation and a dry-run diff preview; factory reset behind a typed confirmation; changes re-apply live through the reactive settings change stream (T-053 canon).
- **Scope:** a settings port service, settings-window tab wiring, resx en+ru, tests.
- **Acceptance:** round-trips byte-identical; no restart needed after import.
- **Tests:** roundtrip golden, invalid-schema rejection, the live reapply cases.

## T-101 — Log viewer window + global crash guard

- **Branch:** queued 2026-11-06 (supervisor idea at the T-074…T-115 backlog cut).
- **Origin:** Serilog writes files only (the stdout rule); users cannot inspect without an editor, and T-056 flagged silent death paths.
- **What:** an in-app viewer (live tail, level/source filter, export a bounded excerpt) with secrets-shaped strings redacted; a global unhandled exception handler that shows a crash dialog with the log path instead of dying silently.
- **Scope:** a log viewer VM/window (virtualized tail), a crash guard host hook, resx en+ru, tests.
- **Acceptance:** tens of thousands of lines tail smoothly; the redaction never leaks shaped patterns.
- **Tests:** virtualization perf, redaction golden, the unhandled-handler path.

## T-102 — Hotkey manager + cheat sheet overlay

- **Branch:** queued 2026-11-06 (supervisor idea at the T-074…T-115 backlog cut).
- **Origin:** hotkeys are wired (T-002) but undiscoverable and unchangeable.
- **What:** a settings tab mapping actions to keys with conflict detection, persisted user overrides, and a "?" overlay listing the live map; the binding registrar stays the single seam so advertised vs real never diverges.
- **Scope:** hotkey service extension, settings tab, overlay view, resx en+ru, tests.
- **Acceptance:** overrides apply without restart; conflicts diagnose at edit time.
- **Tests:** conflict golden, rebind live-reload, the overlay mirror.

## T-103 — Detachable panels: multi-window workspaces

- **Branch:** queued 2026-11-06 (supervisor idea at the T-074…T-115 backlog cut).
- **Origin:** splitters (T-038) resized one window; multi-monitor setups deserve owned windows.
- **What:** chart/book/assistant detach into owned windows (chrome templates compose per the T-066 canon), geometry per display persisted, the workspace symbol context broadcasts across all panels, app shutdown persists both layouts.
- **Scope:** layout service + window shells, symbol context broadcast, resx en+ru, tests.
- **Acceptance:** detach/attach is lossless; restore across DPI shifts stays sane.
- **Tests:** detach/attach roundtrip golden, context broadcast, geometry restore.

## T-104 — Single instance hand-off + CLI symbol open

- **Branch:** queued 2026-11-06 (supervisor idea at the T-074…T-115 backlog cut).
- **Origin:** a second launch spawns a second app; integrations (shortcuts, scripts) want arg-driven opens.
- **What:** second launches forward arguments (`--symbol`, `--exchange`, optional protocol strings) to the running instance and exit; the running instance applies the workspace symbol. Args are validated allowlist-style — nothing can inject orders; nothing writes to stdout outside the MCP protocol rule.
- **Scope:** app bootstrapping, a handoff service, resx en+ru, tests.
- **Acceptance:** a second start on any platform redirects cleanly and exits promptly.
- **Tests:** handoff golden, malformed-arg rejection, exit-parity cases.

## T-108 — Performance HUD overlay (fps, memory, WS latency)

- **Branch:** queued 2026-11-06 (supervisor idea at the T-074…T-115 backlog cut).
- **Origin:** the AGENTS targets (60 fps book, <1 ms WS, <500 MB) have no visible instrument; nothing guards them day-to-day.
- **What:** a dev-mode overlay showing chart/book fps, working memory, WS latency histogram and channel backpressure bírd gauges; off by default, settings-driven, zero allocation when off (no subscriptions, no timers).
- **Scope:** a HUD service + overlay, `Diagnostics.*` settings keys, resx en+ru, tests.
- **Acceptance:** the HUD's own cost is in the noise when running; off-state leaves zero traces.
- **Tests:** the gauge math golden, the off-state zero-cost assertion.

## T-109 — Soak harness and budget gates (cold start < 3 s, memory < 500 MB)

- **Branch:** queued 2026-11-06 (supervisor idea at the T-074…T-115 backlog cut).
- **Origin:** the perf targets are asserted ad-hoc; multi-hour stability is unmeasured.
- **What:** a scripted soak (demo adapter + fixture replays) sampling memory/GC/fps over hours, a summary report, plus CI-able budget assertions for cold start and memory; headless VM lane runnable from `scripts/`.
- **Scope:** the soak harness, CI integration bits, a docs page on interpreting reports.
- **Acceptance:** a 4-hour soak on the fixtures finishes within memory budget; budget gates run in gate on demand.
- **Tests:** the harness itself plus the budget assertions' goldens.

## T-110 — Accessibility pass (keyboard, automation peers, contrast)

- **Branch:** queued 2026-11-06 (supervisor idea at the T-074…T-115 backlog cut).
- **Origin:** canvas-heavy UI (Skia charts, book) is opaque to assistive tech by default; keyboard reachability is un-audited.
- **What:** full keyboard reachability for tabs/panels/controls, automation peers (name/role) for canvas widgets, contrast fixes against both palettes, visible focus indicators; an audit checklist recorded in docs.
- **Scope:** UI across views, chart/book peer shims, docs checklist, tests.
- **Acceptance:** every advertised control is reachable and named without a mouse.
- **Tests:** name/role assertions, focus-order golden.

## T-111 — Strategy contracts MVP: candle-close signals (no auto-trading)

- **Branch:** queued 2026-11-06 (supervisor idea at the T-074…T-115 backlog cut).
- **Origin:** plugin-first declares strategies as separate assemblies (AGENTS); nothing exists — no host, no contracts, no `ZettaTrade.Strategies` project yet.
- **What:** a minimal signal contract (evaluate on candle close, emit neutral/long/flat events per symbol+interval), the host+loader in Strategies reusing the indicator scanner's canon (T-054's fix included), a demo strategy; the output is a signals channel only — no order placement, no risk bypass; the human decides.
- **Scope:** the new Strategies project, contracts + host, the demo plugin, tests.
- **Acceptance:** a broken/unloadable plugin degrades the scan, never the app (T-054 regression pinned).
- **Tests:** strategy-kit conformance, the demo golden on seeded candles, scan tolerance.

## T-112 — Signal markers on the chart + signal rail

- **Branch:** queued 2026-11-06 (supervisor idea at the T-074…T-115 backlog cut). **Blocker:** T-111.
- **Origin:** signals without a surface are invisible; markers + a rail personalize the workspace read.
- **What:** markers at candle closes (▲/▼ with tooltips) via the chart overlay layer, plus an N-item signal rail panel streaming live; a click prefills the ticket (T-025's prefill-not-submit lesson), never sends.
- **Scope:** Charting overlay + the rail panel, the T-111 contracts (consumed, unchanged), resx en+ru, tests.
- **Acceptance:** marker placement matches the signal's candle exactly; bursts do not flood the rail (coalesced).
- **Tests:** placement golden, the rail stream, prefill routing.

## T-113 — Backtest harness over stored candles

- **Branch:** queued 2026-11-06 (supervisor idea at the T-074…T-115 backlog cut). **Blocker:** T-111 + T-085.
- **Origin:** strategies need an offline replay to earn trust; live data is the wrong place to discover bad math.
- **What:** a deterministic simulator replaying cached candles through the strategy contract with the paper ledger's fills (T-051-corrected path), an equity summary report (feeds T-083 later), reproducible seeds; entirely offline (no adapters, no limiter).
- **Scope:** a backtest service + report + a run/progress VM, resx en+ru, tests.
- **Acceptance:** the same seed reproduces the same curve byte-for-byte; cancel mid-run is clean.
- **Tests:** determinism golden, ledger integration, the cancel path.

## T-115 — User manual + screenshots (docs-only)

- **Branch:** queued 2026-11-06 (supervisor idea at the T-074…T-115 backlog cut).
- **Origin:** features have grown docs-shaped (themes, MCP, secrets); a user manual keeps the README tight and points elsewhere.
- **What:** `docs/USER-MANUAL.md` — installation per platform, the concepts (workspace symbol, panels, indicators, strategies, MCP, themes, secrets posture), a keyboard map, paper-vs-live safety notes; screenshots from real runs in the current themes. Docs-only: no version bump, no code touches.
- **Scope:** docs only (+ screenshot captures).
- **Acceptance:** every shipped surface documented; the README links it.
- **Tests:** none (docs).

## T-1117 — Secret scanner: gitleaks in CI + one-shot full-history audit

- **Branch:** feat/secret-scanner (base `develop` at `e2d1ac5`; developed 2026-11-06).
- **Origin:** an open-source terminal makes the repo the trust surface: the repo carries an `origin` remote and ships harder, but there is no CI or scanning at all (no `.github`). A quick regex probe of HEAD (sk-/AKIA/ghp_/AIza/PEM markers) is clean, though the 260-commit history has never been machine-audited.
- **What:** `.github/workflows/security.yml` running gitleaks (official action over git history mode) on pushes and PRs, failing the build on any finding; a committed `gitleaks.toml` allowlisting exactly the known dummy test fixtures (identified during the audit — sample-shaped strings only, never real credentials); the one-shot full-history pass over all 260 commits while the history is young (alpha posture — cheap to redo before beta), its results recorded in `docs/SECURITY.md` with the remediation policy: revoke-and-rotate by default, history rewrite only by an explicit supervisor decision; optionally a guarded local gitleaks step in `scripts/gate.ps1` when the binary is present (non-breaking where it is not).
- **Scope:** `.github/workflows`, `gitleaks.toml`, `docs/SECURITY.md`, optionally `scripts/gate.ps1`.
- **Acceptance:** a seeded dummy-key commit is caught by the workflow; the scoped commits are attributed (the console-level `-S` probes of 2026-11-06 were inconclusive — this audit settles them with the scanner); the allowlist covers only explicitly-named fixtures.
- **Tests:** the allowlist regression (the scanner passes the current tree with the fixtures present), a red-team fixture outside the allowlist fails, workflow lint when available.
- **Status:** done. Canon: this branch carries `432a61f` (the gitleaks workflow + the fixture allowlist toml + the guarded gate step) and this status-flip commit (docs/SECURITY.md — created by T-117, T-118 only links later — plus the board row). No version bump: CI/docs-only work. The one-shot full-history machine audit (gitleaks 8.24.3 = the CI-pinned CLI + an 8.30.1 control, committed config and stock defaults alike): **zero findings** throughout — over the develop line's 267 commits at the audit start and over 269 with T-117's own commits included (234–236 patch-bearing + 33 merges; the all-refs view also covered the sibling in-flight branches, 239 non-merge) — details + the local verification of the seeded-commit and allowlist semantics in docs/SECURITY.md. The seeded-commit/allowlist acceptance was verified locally per protocol: three high-entropy dummy markers seeded into a disposable temp clone (never this worktree, deleted after the check) were caught by the action's exact commit-range invocation on both CLI versions, an allowlist-semantics probe suppressed only the named fixture literals both ways, and the current tree passed the committed config. Gate passed with `-AllowProtected` (the `.github/*` + `scripts/gate.ps1` protected-area edits are exactly the T-117 scope); workflow lint clean via actionlint; the gate's gitleaks guard is strictly skip-when-missing (proven absent-binary) and green-enforced when the binary is on PATH. Pending external link: the first pushed run flips the workflow green in CI itself (cannot be exercised from the task session).

## T-118 — Trading-safety guidance in the README: read-only keys, IP restriction, no-withdrawal, testnet-first

- **Branch:** queued 2026-11-06 (supervisor idea at the open-source key-hygiene note; not urgent, tail of the backlog).
- **Origin:** the top-level README today is a docs-site readme (npm/GitHub Pages instructions) — the product carries zero key guidance, no disclaimer block and no alpha badge.
- **What:** a real project README section (or `docs/SECURITY.md`, linked from it, coordinated with T-115 so the user manual references rather than duplicates): recommend user-scoped API keys only — read-only permissions for the market-data setups, trade-only (never withdrawal, never transfer) for trading setups, an IP allowlist where the venue offers one; testnet-first instructions (Bybit Testnet, Binance Testnet) tied to the endpoint registry capabilities (T-015) and where keys live (`ISecretStore` machines — DPAPI/Keychain — never the repo, never argv); an explicit alpha status line plus an investment-disclaimer block ("educational software, not investment advice"). The LICENSE stays standard MIT unchanged — its warranty disclaimer already covers the legal base; only the UI and README get the added wording.
- **Scope:** `README.md` (+ `docs/SECURITY.md`), docs-only (no version bump).
- **Acceptance:** every claim matches shipped features only (no promises beyond testnet/documentable paths).
- **Tests:** none (docs), a words-only review.
- **Status:** done. Docs-only (no version bump, no CHANGELOG section). Canon: the branch carries `577fd42` — the rewritten top-level README with the trading-safety guidance front and center: explicit alpha status, user-scoped key hygiene (read-only for market data, trade-only for trading, withdrawal/transfer off, IP allowlist where offered), testnet-first instructions tied to the configurable `Exchange` endpoint section and the paper-trading/risk/confirmation surfaces, the per-machine `ISecretStore` storage path, and the investment-disclaimer block; the docs-site instructions stay subordinated under a dedicated heading; `docs/SECURITY.md` is referenced as a link only (created by the sibling task T-117). Gate passed; this board-flip commit closes the section.

## T-130 — Extract Livery into a standalone private repo (MIT kept), consume as a package

- **Branch:** queued 2026-11-06 (open-core structure at the licensing review: Livery goes to its own PRIVATE repo, the license stays MIT — the sources stay hidden and the package is the public surface, flipping the repo public later is one click; this is the pilot of the multi-repo mechanics. Sequenced between alpha releases, coordinated across sessions — Livery paths are touched by UI work).
- **Origin:** Livery is domain-neutral by design (appearance styles/controls plus the extensions like `ObserveOnUiThread`), has no tie to trading logic, and the paid plugin plan will ride the same multi-repo mechanics this split rehearses first (a versioned package, the two-PR cross-repo workflow).
- **What:** a one-shot history-preserving split (`git filter-repo --subdirectory-filter src/Livery`) into the new private repo `Livery` on the same account, the LICENSE file stays MIT; a publish workflow shipping the NuGet package (a public binary on nuget.org, sources stay private); ZettaTrade removes the in-tree `src/Livery` and switches to `PackageReference` pinned to the package version; Livery gains its own README, its in-repo test suite and semver+changelog discipline from day one; AGENTS.md and ZettaTrade_SKILL.md updated (the target-layout note loses `src/Livery`, plus a two-repo ownership paragraph: appearance-only changes go through the Livery repo, platform-specific and trading code never move there).
- **Scope:** the bootstrap and split commits in the new Livery repo, the ZettaTrade consumption switch, the docs notes; no product behavior changes.
- **Acceptance:** the new repo's history reaches back through the split for Livery files (nothing lost at the splice); a clean ZettaTrade clone builds with zero in-tree Livery sources and the full test matrix stays green; the two-PR cross-repo path lands once as a rehearsal (a style tweak: the version-bumped PR in Livery, then the bump PR here).
- **Tests:** the matrix over the consumption switch; the Livery suite in the new repo; the rehearsal change verified on both sides.

## T-131 — Plugin SDK seam: versioned plugin abstractions for out-of-tree modules

- **Branch:** queued 2026-11-06 (open-core prerequisite; Core contracts are the supervisor's lane per AGENTS — additive interfaces only).
- **Origin:** plugin-first is the architecture since day one, yet no formally VERSIONED contract exists for modules compiled outside the solution; without one every core refactor breaks out-of-tree builds — the paid modules first of all — and the marketplace shape stays theoretical.
- **What:** a contract surface `ZettaTrade.Plugins.Abstractions` — the plugin identity/manifest, the lifecycle via the `IAsyncInitializable` canon, DI registration entry points, the UI extension points (panel/toolbar/settings section registration), market-data and services access, and the trading-safety obligations (any order path inside a plugin passes `IRiskManager`/`IConfirmationService`, no exceptions); SemVer discipline declared with the governor note (a breaking release = a major bump + a pinned contract baseline test); the author guide `docs/PLUGINS.md` documents the contract, the rejected-not-defaulted error semantics and the platform rules (nothing writes to stdout except MCP protocol messages; logs go to files only).
- **Scope:** the contract assembly, the loader wiring (one blessed path to register external assemblies), docs; NO proprietary code lands in the public repo — the surface stays MIT.
- **Acceptance:** a sample MIT fixture plugin built out of the solution compiles against the published contract and loads through the loader with its panels registering; the matrix stays green with the sample present and absent.
- **Tests:** loader contract tests (manifest parsing, duplicate ids, missing dependencies fail loudly), the end-to-end sample load golden, a public-API surface test pinning the contract baseline (accidental breaks fail CI).

## T-132 — Closed-source plugin packaging: signed artifacts + local license-key activation

- **Branch:** queued 2026-11-06 (licensing mechanics; cut in full only when the first paid module is actually chosen — until then it fixes the agreed skeleton). **Blocker:** T-131.
- **Origin:** in the open-core split the closed modules live in private repos and ship as SIGNED artifacts; customer machines activate them with a license key validated locally — the entitlement flow must exist and behave identically everywhere before real money moves.
- **What:** the private-repo convention fixed in docs: the name `zettatrade-plugin-<feature>`, a proprietary LICENSE + EULA file, CI building a signed NuGet/zip artifact whose version tracks the SDK version it builds against; the consumer-side activation: the license key entered once (a dialog or a file), LOCAL validation (an offline-signed key check — no phone-home required; an optional online revalidation may arrive later without touching the no-telemetry promise — validation logs to files only), and refused-not-defaulted semantics: invalid, expired, tampered or wrong-product keys REFUSE activation loudly and the plugin stays off; the EULA surfaces at first activation.
- **Scope:** the key validation lib + the activation UI (resx en+ru), the packaging docs/templates for the private CI, `docs/LICENSES.md` describing the open-core split (MIT core vs proprietary modules) in a glance-readable form; the private repos themselves appear only per actual module.
- **Acceptance:** a signed closed fixture activates with a valid key and is refused with an explicit error on invalid/expired/tampered/wrong-product keys; no network is required for validation; no proprietary artifact or wording lands in the public repo.
- **Tests:** key crypto goldens (valid/expired/tampered/wrong-product/missing), the activation gate in UI, the refused-not-defaulted contract on every failure path, file-only logging for the validator.
