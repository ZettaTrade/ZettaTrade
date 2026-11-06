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
| T-075 Amend a working order from the orders panel | feat/order-amend | queued | - |
| T-076 Bulk order management: cancel every working order on a symbol | feat/order-bulk | queued | - |
| T-077 Position actions: close, reverse, partial close (paper-first) | feat/position-actions | queued | - |
| T-078 Order ticket confirmation: the pre-submit summary | feat/order-confirm | queued | - |
| T-079 Position sizing helper in the ticket (percent / risk-anchored) | feat/position-sizer | queued | - |
| T-080 Fees: maker/taker in the ticket, fills and history | feat/fees | queued | - |
| T-081 Balances panel: per-exchange and aggregated | feat/balances | queued | - |
| T-082 PnL engine: realized/unrealized accounting core | feat/pnl-engine | queued | - |
| T-083 Trade statistics dashboard: win rate, profit factor, equity curve | feat/trade-stats | queued | T-082 (soft) |
| T-084 CSV export: order history, fills, candles | feat/csv-export | queued | - |
| T-085 Local candle cache in SQLite (write-through, offline start) | feat/candle-cache | queued | - |
| T-086 Deep-history backfill and gap repair | feat/backfill | queued | T-085 |
| T-087 Trade tape (time & sales) panel | feat/trade-tape | queued | - |
| T-088 Depth chart: cumulative bid/ask areas view | feat/depth-chart | queued | - |
| T-089 Chart types: line, area, bars, Heikin-Ashi | feat/chart-types | queued | - |
| T-090 Y-axis scale modes: log and percent | feat/chart-scales | queued | - |
| T-091 Drawing tools: trend line, horizontal level, fib retracement | feat/draw-tools | queued | - |
| T-092 Standard indicator pack (EMA, RSI, MACD, Bollinger… as bundled plugins) | feat/indicator-pack | queued | - |
| T-093 VWAP + volume profile overlay | feat/vol-vwap | queued | - |
| T-094 Chart export: PNG snapshot + CSV candles | feat/chart-export | queued | - |
| T-095 Global symbol palette (Ctrl+K) across adapters | feat/symbol-palette | queued | - |
| T-096 Price and percent-move alerts with a manager | feat/price-alerts | queued | - |
| T-097 Notification center: toasts, history, DND | feat/notif-center | queued | - |
| T-098 Adapter health strip in the status bar | feat/health-strip | queued | - |
| T-099 First-run wizard (theme, adapter, symbol) | feat/first-run-wizard | queued | - |
| T-100 Settings import/export + factory reset | feat/settings-port | queued | - |
| T-101 Log viewer window + global crash guard | feat/log-viewer | queued | - |
| T-102 Hotkey manager + cheat sheet overlay | feat/hotkey-manager | queued | - |
| T-103 Detachable panels: multi-window workspaces | feat/multi-window | queued | - |
| T-104 Single instance hand-off + CLI symbol open | feat/single-instance | queued | - |
| T-105 Reconnect policy hardening: backoff, jitter, watchdog | feat/reconnect-policy | queued | - |
| T-106 Exchange rate limiter with per-endpoint budgets | feat/rate-limiter | queued | - |
| T-107 Adapter conformance test kit (shared battery) | feat/adapter-kit | queued | - |
| T-108 Performance HUD overlay (fps, memory, WS latency) | feat/perf-hud | queued | - |
| T-109 Soak harness and budget gates (cold start < 3 s, memory < 500 MB) | feat/soak | queued | - |
| T-110 Accessibility pass (keyboard, automation peers, contrast) | feat/a11y | queued | - |
| T-111 Strategy contracts MVP: candle-close signals (no auto-trading) | feat/strategies | queued | - |
| T-112 Signal markers on the chart + signal rail | feat/signal-markers | queued | T-111 |
| T-113 Backtest harness over stored candles | feat/backtest | queued | T-111 + T-085 |
| T-114 AI market-context injection into the chat | feat/ai-context | queued | - |
| T-115 User manual + screenshots (docs-only) | docs/user-manual | queued | - |
| T-116 Adapter WS E2E tests flake under solution-parallel load: the 10 s EventWait trips on scheduler starvation (queued from the T-073 verification) | - | queued | - |
| T-117 Secret scanner: gitleaks in CI + one-shot full-history audit | feat/secret-scanner | queued | - |
| T-118 Trading-safety guidance in the README: read-only keys, IP restriction, no-withdrawal, testnet-first | docs/safety-readme | queued | - |
| T-119 First-run disclaimer in the UI («not investment advice») + the explicit alpha posture | feat/first-run-disclaimer | queued | - |

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

## T-073 — Flaky concurrency test: GetString_AFactoryParkedAcrossASwitch fails intermittently under parallel-suite load (queued from the T-071/T-072 landing)

- **Branch:** feat/flaky-localization (base `develop` at `8eeb5cc`; queued 2026-11-06; developed 2026-11-06).
- **Origin:** `LocalizationManagerTests.GetString_AFactoryParkedAcrossASwitch_ReturnsTheSnapshotCultureString` (the T-060/BUG-051 pin) failed twice during the T-071/T-072 landing verification — once inside the full parallel-solution run and once in a back-to-back solo rerun, both immediately after heavy builds — and passed every other time (solo ×7 including 3 runs at the pre-merge `1b9a717` base, full-suite ×1; both branch gates green). The failing assertion speaks the pre-BUG-051 symptom: the parked lookup returns a wrong-culture string.
- **What:** the root cause is the test harness, not the manager — the reproduction captured BOTH flake limbs of one family. (1) The parked fact's `"any lookup parked"` witness was class-wide, while the fact's `Task.Run` caller can stay queued past the switch under parallel-suite schedule pressure (the captured window carries 33 sibling tests; the 60-frame perf fact overlapped it) — a foreign lookup resolving the same process-wide `LocalizationManager.Instance` through production code (the order book layout's `Build` path, which parks in the same stamping fake) opened the witness for it, so the switch committed over a call that had never reached its snapshot; that call read the fresh culture and returned `'ru:Menu.File'` — EXACTLY the manager's documented snapshot-consistency promise ("a racing caller may still receive either the pre-switch or the post-switch string, but always the one consistent with its own snapshot"), so the manager is correct and the handshake was not. (2) The same instance coupling let parallel `Build` facts resolve the localization suites' stamping fake template ("en:OrderBook.SpreadFormat" — the captured `SpreadText` failure) and a DISPOSED manager keep `_instance` set, so `IsInitialized` stayed true for the whole process after the first creation. The fix: the handshake waits for the fact's OWN thread id at a per-name register (the snapshot is sealed before the switch may park), the `OrderBookPageLayoutTests` join the localization singleton collection (no foreign resolution during `Build`), and `LocalizationManager.Dispose` clears the static when it owns the slot. The `GetString` snapshot path itself stays byte-identical — the race was never in it.
- **Scope:** `tests/ZettaTrade.UI.Tests/Localization/LocalizationManagerTests.cs` (the per-name/thread handshake + the class remarks), `tests/ZettaTrade.UI.Tests/Charting/OrderBookPageLayoutTests.cs` (the collection join), `src/Livery/Localization/LocalizationManager.cs` (the Dispose self-clear), `Directory.Build.props` + `CHANGELOG.md` + board docs.
- **Acceptance:** (verified reality) the localization family holds zero recurrences post-fix — UI.Tests-only runs ×12 green and 21 full-solution runs at the tip/merged tree, where every CAPTURED failure belongs to the unrelated `BinanceAdapterWsE2eTests` mock-server 10 s timeout (two more such failures in the pre-capture post-fix loop are identity-unknown — PowerShell 5.1 `2>&1` swallowed the message bodies; the timeout family is the only residual discovered and is explicitly queued as T-116). The pre-fix reproduction: solution runs ×7 and UI.Tests-only runs ×10 flapped 3 times (failure texts + trx timing captured). The original ≥20-consecutive-green solution-run bar is superseded by the supervisor's merge instruction; the race explanation is written into the test headers.
- **Tests:** no new domain logic — the deterministic shape lives in the already-shipped parked-fact pin, whose handshake is now interference-proof; the reproducibility was established by rerunning the exact landing contexts (full solution / UI.Tests-only / heavy-build-then-solo) from a fresh worktree.
- **Status:** done. Canon: the branch carries `1bf6506` (the handshake seal, the layout suite's collection join, the Dispose self-clear) and this release commit (the alpha.38 cut + this section's status flip); shipped as `0.1.0-alpha.38` (see CHANGELOG.md). Gate passed with `-AllowProtected` (the `Directory.Build.props` bump is the supervisor-only protected file). The residual full-solution failures are the unrelated WS E2E `EventWait` family — queued as T-116.

## T-074 — Advanced order types: stop-limit, trailing stop, OCO

- **Branch:** queued 2026-11-06 (supervisor idea at the T-074…T-115 backlog cut).
- **Origin:** the ticket carries market/limit/stop only today (T-008, later hardened in T-013/T-057); a terminal-grade ticket offers conditionals.
- **What:** stop-limit, trailing stop and OCO pairs end-to-end: trigger-vs-limit and trail-distance validation matrix in the ticket, order DTO shapes extended (an additive, backward-compatible Core change — the supervisor gates the interface diff at review), paper-execution simulation per type against the live candle/book streams, `IRiskManager` + `IConfirmationService` wired as for every order. An unparsable trigger/trail parameter is a returned error, never a default.
- **Scope:** Core order shapes (additive), paper trading engine, ticket VM/view, adapters that can map the types (Bybit/demo; adapters that cannot return a rejected intent rather than degrade), resx en+ru, tests.
- **Acceptance:** each type rides submit → working → fill/cancel on paper; the hard order-value cap applies to notional at trigger; unsupported adapters degrade explicitly.
- **Tests:** the validation golden, per-type paper fills, OCO both-legs semantics (one fills → the other cancels best-effort, no orphans), the unparsable-params error path.
- **Status:** done. Canon: the branch carries the additive order shapes (`29f8e2e`), the paper conditional simulation (`86ce25a`) with its semantics pins (`ac5bac9`), the trigger-anchored risk cap (`0c17c34`) and the conditional ticket (`182b549`), plus this release commit (the alpha.39 cut + this section's status flip); shipped as `0.1.0-alpha.39` (see CHANGELOG.md). No Core interface changes were needed — everything rides the existing `IMarketDataAdapter`/`ITradingAdapter` seams. Gate passed with `-AllowProtected` (the `Directory.Build.props` bump is the supervisor-only protected file).

## T-075 — Amend a working order from the orders panel

- **Branch:** queued 2026-11-06 (supervisor idea at the T-074…T-115 backlog cut).
- **Origin:** cancel exists (T-031); amend is the standard pro flow and is nothing today.
- **What:** inline quantity/price edit or a context menu → revalidation through `IRiskManager`, a confirmation for large deltas via `IConfirmationService`; the row shows an amending state; adapter-side replace where supported, an explicit cancel+new sequence surfaced as such where not. A fill racing the amend wins and is reported — no double entry, no silent drop.
- **Scope:** orders panel VM, a replace flow service in App, the existing trading seam (Core diff only if the adapter map demands one — supervisor-gated), resx en+ru, tests.
- **Acceptance:** amend on the paper adapter, risk re-check enforced, unsupported amends return errors rather than degenerate.
- **Tests:** paper amend golden, fill-during-amend race, risk re-validation, unsupported-type error path.

## T-076 — Bulk order management: cancel every working order on a symbol

- **Branch:** queued 2026-11-06 (supervisor idea at the T-074…T-115 backlog cut).
- **Origin:** one-by-one cancel (T-031) is painful on stop runs; a blind global cancel is out of spec (`cancel_order` always requires the symbol).
- **What:** cancel-all scoped to the active symbol (an optional narrower cut: the symbol's set on the current exchange): a confirmation with count and notional, per-item best-effort reports, every item riding the same cancel command path (no risk bypass), paper/live tags visible.
- **Scope:** orders panel VM + a bulk service, resx en+ru, tests.
- **Acceptance:** a mixed working/cancelled set cancels only working orders; one adapter fault never stops the others.
- **Tests:** bulk cancel golden, partial-failure reporting, the confirmation gate.

## T-077 — Position actions: close, reverse, partial close (paper-first)

- **Branch:** queued 2026-11-06 (supervisor idea at the T-074…T-115 backlog cut).
- **Origin:** positions exist (T-009) but every action is a hand-built order today.
- **What:** context actions sized against the current position: close (market, paper-first), reduce by N, reverse (close + open opposite — two risk-checked orders, `IConfirmationService` mandatory); demo/paper lane first, live adapters only where the mapping exists.
- **Scope:** positions panel VM + a service, confirmation wiring, adapter mapping, resx en+ru, tests.
- **Acceptance:** close-on-empty is rejected; reverse math is two validated orders; double-click while in flight is a no-op (idempotent disable).
- **Tests:** close/reduce/reverse goldens on paper, rejection paths, the in-flight disable case.

## T-078 — Order ticket confirmation: the pre-submit summary

- **Branch:** queued 2026-11-06 (supervisor idea at the T-074…T-115 backlog cut).
- **Origin:** `IRiskManager`/`IConfirmationService` exist (T-057) but the gate is invisible; traders want one look at the order economics before submit.
- **What:** a modal confirm listing side/qty/type/limit/exchange, the paper-live tag prominently, estimated notional vs the hard cap headroom and an estimated fee line; defaults ON for live, OFF in paper; a settings key to skip below a configurable notional; market orders always confirm in live.
- **Scope:** a confirm dialog + ticket VM wiring, `Settings.*` keys + reactive settings change, resx en+ru, tests.
- **Acceptance:** the confirm composes once from the final ticket state at submit — no per-keystroke re-valuation, no double-Enter double submit.
- **Tests:** pass/block matrix, the settings-driven skip, double-Enter submit protection.

## T-079 — Position sizing helper in the ticket

- **Branch:** queued 2026-11-06 (supervisor idea at the T-074…T-115 backlog cut).
- **Origin:** sizing is raw quantities; browsers of every terminal offer balance-percent and risk-anchored sizing.
- **What:** a quick-size row: presets of available balance (25/50/100%) and a risk-percent mode anchored on the distance to the ticket's stop price; rounding to the instrument step sizes via existing metadata; preview updates live; zero network calls; hidden for instruments without metadata.
- **Scope:** ticket VM, `Settings.*` keys for defaults, resx en+ru, tests.
- **Acceptance:** no sizing path bypasses the ticket validation (the helper only proposed quantities).
- **Tests:** step-size rounding golden, risk-percent math, the stale-balance snapshot handling.

## T-080 — Fees: maker/taker in the ticket, fills and history

- **Branch:** queued 2026-11-06 (supervisor idea at the T-074…T-115 backlog cut).
- **Origin:** PnL without fees lies; fee surfaces are absent everywhere.
- **What:** a per-adapter fee schedule (paper: a configurable maker/taker model), an estimate line in the ticket, fee columns in fills and order history, and fee inclusion in the T-082 PnL accounting. A missing schedule surfaces as an explicit "—" (never a zero default that hides the cost).
- **Scope:** fill DTO extension if fees are absent (additive, supervisor-gated Core diff), paper engine, panels, resx en+ru, tests.
- **Acceptance:** maker vs taker selection visible everywhere the cost matters.
- **Tests:** schedule selection golden, rounding policy, the no-schedule passthrough (a displayed unknown, not a zero).

## T-081 — Balances panel: per-exchange and aggregated

- **Branch:** queued 2026-11-06 (supervisor idea at the T-074…T-115 backlog cut).
- **Origin:** there is no balance surface at all; portfolio oversight is a terminal baseline.
- **What:** read-only balances per adapter (paper first, then adapters that expose account data), per-exchange sections plus an aggregated view with last-price valuation and allocation bars; secrets stay in `ISecretStore` (read path only), manual refresh, no auto-trading implications.
- **Scope:** an additive read-only balances face on the trading seam (supervisor-gated Core diff), the Infrastructure fetchers, panel VM/view, resx en+ru, tests.
- **Acceptance:** paper demo shows live-updated figures; adapters without balance data render an explicit "not supported" row.
- **Tests:** aggregation golden, valuation staleness handling, the not-supported path.

## T-082 — PnL engine: realized/unrealized accounting core

- **Branch:** queued 2026-11-06 (supervisor idea at the T-074…T-115 backlog cut).
- **Origin:** a headless accounting core is the prerequisite for stats (T-083) and honest fills; nothing exists.
- **What:** average-price bookkeeping over the paper/fill ledger (reusing T-051-corrected fills), realized PnL per closing fill, unrealized from the latest price feed, fees in (T-080), a position-flip path; an `IObservable` state feed for the UI instead of polling; SQLite persistence for realized history (EF canon).
- **Scope:** a domain service (Infrastructure + a small App-side feed), resx en+ru for any surfaced strings, tests.
- **Acceptance:** determinism across restarts (reloading history reconstructs the same numbers).
- **Tests:** average-price goldens, fee application, flip-mid-stream, persistence reload equality.

## T-083 — Trade statistics dashboard: win rate, profit factor, equity curve

- **Branch:** queued 2026-11-06 (supervisor idea at the T-074…T-115 backlog cut).
- **Origin:** the data exists in order/fill history yet nothing summarizes it.
- **What:** a stats window: win rate, profit factor, max drawdown, average winner/loser, count and volume, plus a Skia equity-curve area; filters by symbol, exchange, date range and paper-live tag; read-only store queries (paging for large histories).
- **Scope:** a stats VM + window, Skia chart renderer, resx en+ru, tests. Soft-depends on T-082 (realized series) but works on raw fills alone.
- **Acceptance:** stays responsive on six-figure histories (paged queries, no first-load wall).
- **Tests:** aggregation goldens on seeded history, empty state, paging.

## T-084 — CSV export: order history, fills, candles

- **Branch:** queued 2026-11-06 (supervisor idea at the T-074…T-115 backlog cut).
- **Origin:** external analysis demands files; exports are nothing today.
- **What:** export actions for the history panels and candles: `InvariantCulture` numbers (the ru-locale decimal trap) and RFC quoting, UTC ISO-8601 timestamps, streamed writers so six-figure histories never balloon memory, cancellation-safe.
- **Scope:** an export service (Infrastructure), panel menu wiring, resx en+ru, tests.
- **Acceptance:** exports open clean in Excel/LibreOffice regardless of the OS culture.
- **Tests:** quoting/escaping golden, ru-RU host culture independence, streamed memory cap, cancel mid-export.

## T-085 — Local candle cache in SQLite (write-through, offline start)

- **Branch:** queued 2026-11-06 (supervisor idea at the T-074…T-115 backlog cut).
- **Origin:** every start re-requests the history; EF Core + SQLite are in place since T-010 and the upsert-race lesson is T-061.
- **What:** a write-through candle cache keyed (adapter, symbol, interval), upserted by open time; startup serves from cache, then refreshes over the feeds; an eviction cap and a `Caching.*` settings section; EF migrations per canon.
- **Scope:** Infrastructure persistence + the chart VM's bootstrap path, `Caching.*` settings keys, tests.
- **Acceptance:** a fresh start renders instantly from cache with the refresh riding behind; overlap conflates, never duplicates.
- **Tests:** upsert idempotency across restarts, range-overlap golden, eviction cap, the check-then-act race pinned.

## T-086 — Deep-history backfill and gap repair

- **Branch:** queued 2026-11-06 (supervisor idea at the T-074…T-115 backlog cut). **Blocker:** T-085.
- **Origin:** pan/paging (T-033) re-requests ranges, and gaps after outages render as holes; nothing stitches.
- **What:** on demand past cached ranges, fetch older segments and stitch without overlap or dup; a calendar-aware gap detector (session/timeframe calendar) with a repair pass on subscription; backpressure rides the existing `Channel<T>` legs; per-adapter depth ability surfaced (some feeds cap history — display the limit, never fabricate).
- **Scope:** the fetcher service + cache write-through (T-085), chart VM integration, tests.
- **Acceptance:** after an outage of hours, the chart heals itself on restart/subscription.
- **Tests:** stitch golden, gap detection over calendars, depth-limited adapter behavior, batching against T-106 later.

## T-087 — Trade tape (time & sales) panel

- **Branch:** queued 2026-11-06 (supervisor idea at the T-074…T-115 backlog cut).
- **Origin:** only candles and the book render; the tick-by-tick tape is a terminal staple.
- **What:** a scrolling tape per symbol — price/size/side coloring, newest at top, ring-buffer rendering at the book's 60 fps discipline (the T-011 pattern), pause-on-hover, clear button. Trades ride a stream seam: if the read-only adapter face lacks trades, the addition is additive Core (supervisor-gated); the demo adapter seeds synthetic trades.
- **Scope:** the trades stream (seam), panel VM/view in the workspace layout (splitters of T-038), resx en+ru, tests.
- **Acceptance:** 1000+ trades/s hold the 60 fps target without unbounded memory (ring buffer).
- **Tests:** ring-buffer perf, side-color assignment, empty/paused states.

## T-088 — Depth chart: cumulative bid/ask areas view

- **Branch:** queued 2026-11-06 (supervisor idea at the T-074…T-115 backlog cut).
- **Origin:** the ladder (T-011) shows levels; the cumulative shape of the book is its own read.
- **What:** a cumulative bid/ask area around midspread beside the ladder, riding the same book stream (no new transport), redraw throttled to the 60 fps target, and the depth-resolution discipline of T-072 reused (the served vs displayed depth contract).
- **Scope:** a Skia renderer in Charting, panel composition, resx en+ru, tests.
- **Acceptance:** 1000+ levels hold 60 fps; both components agree on the same resolved depth.
- **Tests:** cumulative math golden, fps budget, empty-book state.

## T-089 — Chart types: line, area, bars, Heikin-Ashi

- **Branch:** queued 2026-11-06 (supervisor idea at the T-074…T-115 backlog cut).
- **Origin:** candles only; line/area are the intraday standard, HA smooths the noise.
- **What:** renderer variants off the existing candle source; Heikin-Ashi computed client-side from OHLC; per-symbol persistence of the choice alongside the workspace symbol (the T-029/T-055 restore discipline).
- **Scope:** Charting renderers + the chart VM switcher, tests.
- **Acceptance:** every variant keeps the axis-bucket invariants (T-035/T-037 canon) and the pan/paging behaviors.
- **Tests:** HA derivation golden, per-type axis invariants, choice persistence restore.

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

## T-092 — Standard indicator pack (bundled plugins)

- **Branch:** queued 2026-11-06 (supervisor idea at the T-074…T-115 backlog cut).
- **Origin:** only the seam + an SMA sample (T-034) exist; the standard set is missing while the plugin machinery is ready (T-054, T-067, T-068).
- **What:** EMA, WMA, RSI, MACD, ATR and Bollinger as separate bundled plugin assemblies (plugin-first canon), registered into the existing catalog, parameter editors through the loaded-plugins gate, reference-formula tests over fixed series.
- **Scope:** the new plugin assemblies + catalog registration + resx for names, tests.
- **Acceptance:** cold start pays no cost for unloaded plugins; formulas match reference vectors.
- **Tests:** formula goldens, scan tolerance, settings roundtrip per plugin.

## T-093 — VWAP + volume profile overlay

- **Branch:** queued 2026-11-06 (supervisor idea at the T-074…T-115 backlog cut).
- **Origin:** the intraday VWAP and volume-at-price histogram are standard reads; both absent.
- **What:** session-anchored VWAP (optional ±1σ bands) and a right-margin volume-at-price histogram over the visible window; active only when candles carry volume — an explicit "no volume data" state otherwise (never zero-fabricated).
- **Scope:** Charting overlays + the indicator seam reuse, tests.
- **Acceptance:** session anchors handle day boundaries of the T-037 calendar correctly.
- **Tests:** VWAP boundary math golden, histogram bucket alignment, the no-volume state.

## T-094 — Chart export: PNG snapshot + CSV candles

- **Branch:** queued 2026-11-06 (supervisor idea at the T-074…T-115 backlog cut).
- **Origin:** sharing charts (reports, chat with T-041's future posts) needs images; exports of a chart are nothing.
- **What:** a chart menu action rendering the current frame via Skia at a fixed scale with a symbol/timeframe watermark, saving to the user-picked path; the visible-range CSV rides T-084's writer contract.
- **Scope:** Charting + a menu item, resx en+ru, tests.
- **Acceptance:** the export matches the on-screen frame (same lens, same annotations state).
- **Tests:** pixel-diff smoke on a seeded chart, watermark presence, path/error handling.

## T-095 — Global symbol palette (Ctrl+K)

- **Branch:** queued 2026-11-06 (supervisor idea at the T-074…T-115 backlog cut).
- **Origin:** T-014's autocomplete lives inside the ticket only; a Ctrl+K palette is the terminal norm.
- **What:** a fuzzy palette over all adapters' instruments with recents and the existing favorites store as inputs (read-only — the favorites area is parked task A's turf), keyboard-first, Enter applies the workspace symbol context (T-017 discipline); backdrop styling via the chrome canon.
- **Scope:** palette VM/view, instrument source wiring, resx en+ru, tests.
- **Acceptance:** opens under 100 ms with thousands of instruments; esc/dismiss returns focus cleanly.
- **Tests:** fuzzy ranking golden, per-adapter empty states, hotkey conflict check with existing bindings.

## T-096 — Price and percent-move alerts with a manager

- **Branch:** queued 2026-11-06 (supervisor idea at the T-074…T-115 backlog cut).
- **Origin:** alerts are a terminal baseline; nothing exists (T-041's Telegram sink is a different layer — delivery).
- **What:** local evaluations on the existing candle/book streams (no extra polls): crossing a price, % move within a window; a manager table (create/edit/snooze/delete), persistence, cross-session reload; a firing alert beeps + toasts (to T-097 when it lands); triggering an order from a signal is explicitly out of scope — no auto-trading without `IConfirmationService`.
- **Scope:** alert engine (a service), manager UI, `Settings.*` keys, resx en+ru, tests.
- **Acceptance:** edge semantics exact (above/at/below), no alert lost across restart if persisted.
- **Tests:** trigger edge matrix, persistence reload, sound-failure degrade.

## T-097 — Notification center: toasts, history, DND

- **Branch:** queued 2026-11-06 (supervisor idea at the T-074…T-115 backlog cut).
- **Origin:** order fills and failures land in logs only (T-056 hardening); users need visible feedback.
- **What:** an in-app toast host (top-right, chrome-styled per T-066) + a history drawer; sources: fills/cancels/rejections, alerts (T-096 synergy), adapter state flips (T-098 synergy); per-source mute and a DND toggle; clicking routes to the source panel (the T-012 focus-routing precedent).
- **Scope:** a notification service + toast host, routing, resx en+ru, tests.
- **Acceptance:** bursts of dozens of fills coalesce; the host never blocks the UI thread (async canon).
- **Tests:** coalescing golden, DND replay, click-route matrix.

## T-098 — Adapter health strip in the status bar

- **Branch:** queued 2026-11-06 (supervisor idea at the T-074…T-115 backlog cut).
- **Origin:** reconnect states exist inward (T-012, T-052); the user sees nothing in aggregate.
- **What:** a dot-per-adapter in the status bar (connected/reconnecting/down/market-closed colors), a tooltip with the last event time and error counters from the existing transport state, click → an open log pane later (T-101 synergy); localized labels en+ru.
- **Scope:** status bar VM composition (the T-018 slot), a state aggregator service, resx en+ru, tests.
- **Acceptance:** state flips render within a beat; the strip adds no polling loops (observables only).
- **Tests:** state mapping from transport events, tooltip composition.

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

## T-105 — Reconnect policy hardening: backoff, jitter, watchdog

- **Branch:** queued 2026-11-06 (supervisor idea at the T-074…T-115 backlog cut).
- **Origin:** reconnect fixes are per-adapter (T-048, T-052); the policy itself is implicit and untestable.
- **What:** an explicit per-adapter policy object (exponential + jitter bounds + max delay) and a health watchdog that force-cycles a silent socket; a central supervisor exposing state (later to T-098); adapter implementations consume the policy — zero behavior drift on the happy path.
- **Scope:** a policy/supervisor service in Infrastructure, adapter wiring, tests.
- **Acceptance:** a half-open socket heals inside the watchdog window; churn with 2+ symbols stays fixed (T-048 regression pinned).
- **Tests:** watchdog golden, jitter bounds, churn regression.

## T-106 — Exchange rate limiter with per-endpoint budgets

- **Branch:** queued 2026-11-06 (supervisor idea at the T-074…T-115 backlog cut).
- **Origin:** REST/token budgets are unmanaged today; bursts risk 429s and bans.
- **What:** per-endpoint token buckets with weights, a priority queue (user actions over background fills from T-086), 429/Retry-After aware; limits live adapter-side so the UI never busy-waits; demo/testnet exempt.
- **Scope:** an Infrastructure limiter + adapter hooks, `Settings.*` keys, tests.
- **Acceptance:** background backfill never starves a user submit; Retry-After honored.
- **Tests:** drain/refill timing golden, Retry-After compliance, preemption.

## T-107 — Adapter conformance test kit (shared battery)

- **Branch:** queued 2026-11-06 (supervisor idea at the T-074…T-115 backlog cut).
- **Origin:** every adapter carries bespoke tests; the shared contract deserves a common gauntlet (the Bybit/demo/Binance variance shows it).
- **What:** an abstract test battery parameterized over canned offline captures: subscribe/reconnect restore, depth resolution (T-072's resolver), the rejected-not-default error contract, guard invariants; new adapters run the kit as part of gate.
- **Scope:** the kit as a test project + gate integration, capture-pack fixtures.
- **Acceptance:** the demo and Bybit adapters pass the kit outright; the kit documents itself as the adapter recipe.
- **Tests:** the kit itself (this is a test deliverable) plus its gate wiring.

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

## T-114 — AI market-context injection into the chat

- **Branch:** queued 2026-11-06 (supervisor idea at the T-074…T-115 backlog cut).
- **Origin:** the assistant is wired (T-032) but blind to the now; MCP tools fetch on demand, yet a compact snapshot as context is the tier everyone reads first.
- **What:** a context provider that compacts the workspace moment (last price, session change, book top N, active indicator values, alert states, paper/live tag) into a token-bounded preamble, injected per message behind an explicit user toggle chip; local models get the same treatment first; the MCP protocol-purity rule stays absolute (nothing writes to stdout but the protocol).
- **Scope:** an MCP tool/context provider in AI, the chat VM toggle, resx en+ru, tests.
- **Acceptance:** the preamble fits a tight token budget and reads deterministically for the same snapshot.
- **Tests:** compaction golden, the consent toggle matrix, the paper/live tag correctness.

## T-115 — User manual + screenshots (docs-only)

- **Branch:** queued 2026-11-06 (supervisor idea at the T-074…T-115 backlog cut).
- **Origin:** features have grown docs-shaped (themes, MCP, secrets); a user manual keeps the README tight and points elsewhere.
- **What:** `docs/USER-MANUAL.md` — installation per platform, the concepts (workspace symbol, panels, indicators, strategies, MCP, themes, secrets posture), a keyboard map, paper-vs-live safety notes; screenshots from real runs in the current themes. Docs-only: no version bump, no code touches.
- **Scope:** docs only (+ screenshot captures).
- **Acceptance:** every shipped surface documented; the README links it.
- **Tests:** none (docs).

## T-116 — Adapter WS E2E tests flake under solution-parallel load: the 10 s `EventWait` trips on scheduler starvation (queued from the T-073 verification)

- **Branch:** queued 2026-11-06 from the T-073 post-fix verification, no branch yet.
- **Origin:** during the T-073 post-fix full-solution verification, `ZettaTrade.Infrastructure.Tests.Exchange.BinanceAdapterWsE2eTests.Depth_and_trade_frames_publish_order_book_and_trades` failed with `System.TimeoutException` inside `WaitAsync(EventWait)` (captured console: the xUnit `[FAIL]` line, 10 s mark, Infrastructure run at 23 s duration) while every focused/localization run stayed green; the merged-tree capture run (`post5.trx` window) left the mock-server handshake, subscribe and all frames delivered under no extra load — the fact passes solo. Entire E2E family carries `private static readonly TimeSpan EventWait = TimeSpan.FromSeconds(10)` (`BinanceAdapterWsE2eTests`, `BybitAdapterWsE2eTests`, `BinanceAdapterResubscribeTests`, `BybitAdapterResubscribeTests`, `WebSocketClientTests`, `PaperTradingAdapterTests`).
- **What:** the mock-server collect/subscribe/frame budget is a bare-bones constant with a 10 s cap; under solution-level load (parallel testhosts chewing cores, xUnit parallelism in sibling assemblies) a scheduler stall crosses it and the fact fails spuriously — no product defect is involved (the WS client and adapter behave). Root-cause the real latency budget (collector wait vs scheduling), then either raise the family budget uniformly (60 s) or split tight timing assertions (latency ceilings) from the completion waits; no production code changes expected.
- **Scope:** `tests/ZettaTrade.Infrastructure.Tests/Exchange/*Tests.cs` (the shared `EventWait` constants and `PollUntilAsync` budgets), tests only.
- **Acceptance:** full-solution consecutive runs (≥20, cold and warm) hold zero WS E2E timeout failures; the budget rationale is written into the test files' headers.
- **Tests:** no new logic; the existing E2E battery (mock server + adapters) is the test.

## T-117 — Secret scanner: gitleaks in CI + one-shot full-history audit

- **Branch:** queued 2026-11-06 (supervisor idea at the open-source key-hygiene note; not urgent, tail of the backlog).
- **Origin:** an open-source terminal makes the repo the trust surface: the repo carries an `origin` remote and ships harder, but there is no CI or scanning at all (no `.github`). A quick regex probe of HEAD (sk-/AKIA/ghp_/AIza/PEM markers) is clean, though the 260-commit history has never been machine-audited.
- **What:** `.github/workflows/security.yml` running gitleaks (official action over git history mode) on pushes and PRs, failing the build on any finding; a committed `gitleaks.toml` allowlisting exactly the known dummy test fixtures (identified during the audit — sample-shaped strings only, never real credentials); the one-shot full-history pass over all 260 commits while the history is young (alpha posture — cheap to redo before beta), its results recorded in `docs/SECURITY.md` with the remediation policy: revoke-and-rotate by default, history rewrite only by an explicit supervisor decision; optionally a guarded local gitleaks step in `scripts/gate.ps1` when the binary is present (non-breaking where it is not).
- **Scope:** `.github/workflows`, `gitleaks.toml`, `docs/SECURITY.md`, optionally `scripts/gate.ps1`.
- **Acceptance:** a seeded dummy-key commit is caught by the workflow; the scoped commits are attributed (the console-level `-S` probes of 2026-11-06 were inconclusive — this audit settles them with the scanner); the allowlist covers only explicitly-named fixtures.
- **Tests:** the allowlist regression (the scanner passes the current tree with the fixtures present), a red-team fixture outside the allowlist fails, workflow lint when available.

## T-118 — Trading-safety guidance in the README: read-only keys, IP restriction, no-withdrawal, testnet-first

- **Branch:** queued 2026-11-06 (supervisor idea at the open-source key-hygiene note; not urgent, tail of the backlog).
- **Origin:** the top-level README today is a docs-site readme (npm/GitHub Pages instructions) — the product carries zero key guidance, no disclaimer block and no alpha badge.
- **What:** a real project README section (or `docs/SECURITY.md`, linked from it, coordinated with T-115 so the user manual references rather than duplicates): recommend user-scoped API keys only — read-only permissions for the market-data setups, trade-only (never withdrawal, never transfer) for trading setups, an IP allowlist where the venue offers one; testnet-first instructions (Bybit Testnet, Binance Testnet) tied to the endpoint registry capabilities (T-015) and where keys live (`ISecretStore` machines — DPAPI/Keychain — never the repo, never argv); an explicit alpha status line plus an investment-disclaimer block ("educational software, not investment advice"). The LICENSE stays standard MIT unchanged — its warranty disclaimer already covers the legal base; only the UI and README get the added wording.
- **Scope:** `README.md` (+ `docs/SECURITY.md`), docs-only (no version bump).
- **Acceptance:** every claim matches shipped features only (no promises beyond testnet/documentable paths).
- **Tests:** none (docs), a words-only review.

## T-119 — First-run disclaimer in the UI («not investment advice») + the explicit alpha posture

- **Branch:** queued 2026-11-06 (supervisor idea at the open-source key-hygiene note; not urgent, tail of the backlog).
- **Origin:** the alpha posture is half-visible: the status-bar chip shows `0.1.0-alpha.NN` (AppVersion trims the `+build` metadata), but the explicit statements — «не является инвестиционной рекомендацией» and «не использовать с реальными средствами без собственного тестирования» — appear nowhere in the interface.
- **What:** a non-blocking dismissible disclosure panel on the first app start holding both statements plus the paper/testnet safety hint; an explicit acknowledge persists a flag and the panel is not shown again (relaunchable from Settings → About); fully localized en+ru (all wording in resx — no ViewModel static localized strings, the standing canon); if T-099 (first-run wizard) lands first, the panel becomes the wizard's zero step and the flag is shared.
- **Scope:** a disclosure VM/view, a persisted first-run flag in the settings, resx en+ru, tests.
- **Acceptance:** the panel shows exactly once per installation, the acknowledge never gates trading actions (it informs — the `IConfirmationService` risk flows stay untouched), and the alpha wording is discoverable later from About.
- **Tests:** first-run/second-run golden, the persisted flag reload, the en/ru wiring, the Settings → About reveal path.
