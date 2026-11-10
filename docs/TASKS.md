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
| T-130 Extract Livery into a standalone private repo (MIT kept) and consume it as a package | feat/livery-split | queued | - |
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
| T-141 Soak isolation: pin the demo adapter so a run is network-free | fix/soak-isolation | queued | - |
| T-142 Book timestamps: one clock discipline across adapters (the WS latency measures transport) | fix/book-timestamps | queued | - |
| T-143 Palette suggestion rows carry automation names (not the record's ToString) | fix/palette-a11y-rows | queued | - |
| T-144 UI test suite: a headless lane over the real windows (layout, tabs, flows) | feat/ui-tests | queued | - |
| T-145 UI layout pass: a wrapping chart toolbar and a roomier settings window | feat/ui-layout | review | - |
| T-148 Kill the BrokenBase fixture leak: the T-054 base assembly never reaches the output directories | feat/fixture-leak | done | - |
| T-149 Stabilize the App.Tests heatmap stale-epoch flake: a completion-driven settle pin | feat/app-test-flak | planned | - |

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

## T-110 - Accessibility pass (keyboard, automation peers, contrast)

- **Branch:** feat/a11y, worktree zt-a11y, based on develop@33ebd26 (claimed 2026-11-10 per standing directive 6; queued 2026-11-06 at the T-074…T-115 backlog cut).
- **Origin:** canvas-heavy UI (Skia charts, book) is opaque to assistive tech by default; keyboard reachability is un-audited.
- **What:** full keyboard reachability for tabs/panels/controls, automation peers (name/role) for canvas widgets, contrast fixes against both palettes, visible focus indicators; an audit checklist recorded in docs.
- **Scope:** UI across views, chart/book peer shims, docs checklist, tests.
- **Acceptance:** every advertised control is reachable and named without a mouse.
- **Tests:** name/role assertions, focus-order golden.
- **Status:** done. Canon: shipped as 0.1.0-alpha.87 (feat `8129cae`). The zero-gap shape: Avalonia's base `Control` serves the automation-none peer, so every Skia canvas widget (candles, book, depth, tape, heatmap, equity) overrides `OnCreateAutomationPeer` with a small nested peer deriving `ControlAutomationPeer` and answering the `Custom` control type, its name composed from the same state the drawing reads in the pure `WidgetAccessibilityNames` compositors (symbol/compare-series naming plus the template resolver); every string rides the localization catalog — 10 `A11y` resx keys shipped en+ru against the 9 planned (the build surfaced two real surfaces the plan missed: `Tape.A11yWithSymbol` and `Stats.A11yEquity` — recorded, not silently absorbed). The equity curves name themselves through the window-attached `AutomationProperties.Name` (the base `GetNameCore` route — the same route every icon-only control takes: the four title-bar strip buttons, the T-095 palette's query box and the chat input). Livery's shared themes carry visible `:focus` styles for Button, ToggleButton, TabItem, MenuItem and the strip icons, borrowed from the palette's on-state accent; the palette's suggestion rows stay `Focusable=False` by design (keyboard-first surface: the query box holds focus, arrows walk, Enter executes). Contrast gained real teeth: the WCAG luminance/ratio math lives in `Livery.Themes.Contrast`, and the pins walk the REAL repo theme files — light `TextSecondary` → `#5f6672`, `Buy`/`CandleUp` → `#15803d`, `Highlight` → `#8a6100`, trading-pro `Accent` → `#047857` (mirrored in previewColors); text pairs at 4.5:1, the signal family at 3.0:1, all shipped themes conform. Tests: 24 new — 8 pure name-composition facts, 4 contrast facts (math edges + the real-files conformance), 7 live dispatcher peer facts (real peers, never the none-default; the correct expectation is `BeAssignableTo<ControlAutomationPeer>` — the widgets serve their own derived types), 5 source pins (wiring + all 10 keys in both cultures); suites UI 310 / App 1345 green. Gate passed with `-AllowProtected` (the bump); run 1 tripped only on usings order (`Avalonia.Automation.Peers` sorts before `Avalonia.Controls`), run 2 passed. Gaps recorded honestly in `docs/A11Y-AUDIT.md`: no live screen-reader walk yet, no cell-level canvas navigation, no focus-order golden.

## T-130 — Extract Livery into a standalone private repo (MIT kept), consume as a package

- **Branch:** queued 2026-11-06 (open-core structure at the licensing review: Livery goes to its own PRIVATE repo, the license stays MIT — the sources stay hidden and the package is the public surface, flipping the repo public later is one click; this is the pilot of the multi-repo mechanics. Sequenced between alpha releases, coordinated across sessions — Livery paths are touched by UI work).
- **Origin:** Livery is domain-neutral by design (appearance styles/controls plus the extensions like `ObserveOnUiThread`), has no tie to trading logic, and the paid plugin plan will ride the same multi-repo mechanics this split rehearses first (a versioned package, the two-PR cross-repo workflow).
- **What:** a one-shot history-preserving split (`git filter-repo --subdirectory-filter src/Livery`) into the new private repo `Livery` on the same account, the LICENSE file stays MIT; a publish workflow shipping the NuGet package (a public binary on nuget.org, sources stay private); ZettaTrade removes the in-tree `src/Livery` and switches to `PackageReference` pinned to the package version; Livery gains its own README, its in-repo test suite and semver+changelog discipline from day one; AGENTS.md and ZettaTrade_SKILL.md updated (the target-layout note loses `src/Livery`, plus a two-repo ownership paragraph: appearance-only changes go through the Livery repo, platform-specific and trading code never move there).
- **Scope:** the bootstrap and split commits in the new Livery repo, the ZettaTrade consumption switch, the docs notes; no product behavior changes.
- **Acceptance:** the new repo's history reaches back through the split for Livery files (nothing lost at the splice); a clean ZettaTrade clone builds with zero in-tree Livery sources and the full test matrix stays green; the two-PR cross-repo path lands once as a rehearsal (a style tweak: the version-bumped PR in Livery, then the bump PR here).
- **Tests:** the matrix over the consumption switch; the Livery suite in the new repo; the rehearsal change verified on both sides.

## T-141 — Soak isolation: pin the demo adapter so a run is network-free

- **Branch:** queued 2026-11-10 (filed from the QA review of the T-108/T-102/T-124/T-110/T-109 landing batch: docs/SOAK.md advertises isolation the driver does not provide).
- **Origin:** docs/SOAK.md promises the deterministic demo adapter with no network, but scripts/soak.ps1 launches the app with no adapter pin — the review's soak window logged `Restored persisted exchange 'Bybit'` plus live api.bybit.com requests (and one to binance) inside the run, so a local soak rides the user's persisted config. CI gets away with it only because runners carry no user profile.
- **What:** the launch step pins the app away from the real profile: either a per-run config sandbox (a child-process environment override of APPDATA via `ProcessStartInfo`, PowerShell 5.1-compatible, seeded with a pinned demo-adapter selection, discarded after the run) or an equivalent app-side demo-adapter CLI/env argument. Fold the label nit found in the same review: `Get-AppCpuPercent` calls itself "share of one core-equivalent" while the math divides by the total core count — one wording survives.
- **Scope:** scripts/soak.ps1 (launch, cleanup, report wording), docs/SOAK.md; product code only if the CLI/env pin route wins.
- **Acceptance:** a soak run's app log keeps exchange HTTP/WebSocket traffic at zero outside the demo adapter on every lane; the user's appsettings stays invisible to the run (the sandbox copy absorbs it); the harness stays PowerShell 5.1-runnable and CI's weekly soak keeps the same machinery.
- **Tests:** a `-Minutes 0` self-check in the driver that asserts the pinned demo state from the run's own log census; no goldens change shape.

## T-142 — Book timestamps: one clock discipline across adapters (the WS latency measures transport)

- **Branch:** queued 2026-11-10 (filed from the same QA review: the T-108 histogram conflates transport with clock skew and the adapters stamp differently).
- **Origin:** the WS-latency histogram mixes transport latency with clock offset and is adapter-inconsistent: Bybit stamps the assembled OrderBook with the exchange's server `ts` (BybitAdapter's WS book path carries the message's stamp into BybitOrderBookAssembler), Binance stamps `DateTimeOffset.UtcNow`; the review measured ~650 ms of local-vs-Bybit offset and saw the whole book stream land in the >500 ms overflow bucket.
- **What:** one discipline everywhere — recommended: local receive time (`DateTimeOffset.UtcNow` at each adapter's ingestion point), making the histogram transport-only; the documented alternative keeps exchange stamps and carries a clock-skew caveat in the HUD. The `OrderBook.Timestamp` semantic is documented once; consumers outside the histogram see no change.
- **Scope:** every adapter's book path (Bybit incl. BybitOrderBookAssembler, Binance, the demo adapter), the stamps' goldens, the HUD histogram's doc comment; Core interfaces untouched.
- **Acceptance:** under a skewed local clock a fast feed keeps its samples in the small buckets; each adapter's stamping choice is pin-tested; the change is behavior-compatible for everything but the histogram.
- **Tests:** per-adapter stamping pins plus a histogram golden with an injected skew; the existing book/HUD goldens adjusted.

## T-143 — Palette suggestion rows carry automation names (not the record's ToString)

- **Branch:** queued 2026-11-10 (filed from the same QA review; per docs/A11Y-AUDIT.md the palette row naming sat outside T-110's claimed scope).
- **Origin:** the palette's suggestion rows surface their UIA Name as the SymbolPaletteRow record's ToString — a screen reader reads `SymbolPaletteRow { Symbol = BTC/USDT, ExchangeId = bybit, ... }` instead of a symbol-first entry (caught in the compare-pick flow of the review probe).
- **What:** compose a real automation name per row — symbol first, exchange plus the favorite/recent qualifiers as needed, localized en+ru — via `AutomationProperties.Name` bindings in the palette view or a composed name riding the rows; the pins live next to T-110's accessibility fixtures.
- **Scope:** the symbol palette view/ViewModel, resx en+ru (the `Chart.*` prefix), the a11y pins/tests; the palette's keyboard-first behavior stays untouched.
- **Acceptance:** in both languages a row's UIA Name reads like a symbol entry (e.g. `BTC/USDT on Bybit`), every string resolves en+ru, and the compare-pick flow reports a clean name instead of the record dump.
- **Tests:** row-naming pins in the palette a11y fixture (en+ru); the palette's unchanged behavior goldens.

## T-144 — UI test suite: a headless lane over the real windows

- **Branch:** queued 2026-11-10 (filed at the user's request during the turn that also shipped the T-141…T-143 findings; T-145's layout pins are the acceptance's first tranche).
- **Origin:** the window-level UI facts ride source pins and ad-hoc UIA probes today (the chrome/accessibility pins say it plainly: no headless render harness), so layout regressions like the T-145 toolbar overflow reach the user instead of the gate.
- **What:** a headless UI lane over the real views: Avalonia.Headless (the XUnit flavor) materializes actual windows against the ViewModel graph with mocked services; the pinned facts — at every min-size layout the chart toolbar never intersects the book/tape/watchlist headers, the settings window opens with its folded tab strip and the pinned Save/Close row inside the bounds, the overlay surfaces (palette, hotkey cheat sheet) route their keys, and a language flip re-inks the static strings; the lane rides the local gate and the CI matrix.
- **Scope:** the headless package reference, a headless harness folder in tests/ZettaTrade.App.Tests, the gate wiring for the lane; no product code changes.
- **Acceptance:** a headless bounds fact that fails against the pre-T-145 XAML (the toolbar painting over the book header) and passes after it; the lane stays green in the gate and CI across the three OS targets.
- **Tests:** the harness facts themselves are the deliverable, plus a window-materialization smoke per window.

## T-145 — UI layout pass: a wrapping chart toolbar and a roomier settings window

- **Branch:** feat/ui-layout, worktree zt-ui-layout, based on develop@b36fa3e (claimed 2026-11-10 by the QA-review session on develop@fc4691a; the user filed both complaints with a screenshot: the toolbar painted over the book header, the settings dialog baked six tab headers into a 560-wide fixed frame; re-based as T-103/T-129/T-132, the T-149 claim, the T-148 fix and the T-141..143 queue landed).
- **Origin:** the chart header is a 13-column grid (a star symbol column plus twelve auto columns) inside the chart column; the auto sum exceeds the column, nothing clips, and the chart Border — the workspace grid's last child — paints the spillover on top of the book, tape and watchlist panels; resizing only shrinks the star, never the auto sum. The settings window is fixed 560×640 with six tabs — the header strip consumes the dialog's width and the fixed size blocks the bigger lists.
- **What:** the chart header splits into two rows — the symbol picker owns a stretch row, the controls (time frame, type, scale, the toggles, the exports, compare) ride a finite-width WrapPanel that folds onto further lines when the column narrows; the rows below keep their indices (the header is one nested grid child). The settings window grows to a resizable 760×720 (Min 640×560) and folds to four tabs per the user's pick: Appearance and Indicators stand, MCP plus Import/Export plus the factory reset fold into Integrations, Pairs plus the hotkey editor fold into Workspace; every retired tab header survives as its section caption inside the merged tab, and the port section's visibility rides its own stack (a hidden port never hides MCP).
- **Scope:** MainWindow.axaml, SettingsWindow.axaml, resx en+ru (the two new tab keys), the layout pins test file; no ViewModel or contract changes.
- **Acceptance:** no toolbar pixel lands on the book/tape/watchlist columns at any window size down to the minimums; the settings dialog resizes with the pinned Save/Close row and four tab headers fitting the strip; every migrated control keeps its binding, handler and en+ru localization.
- **Tests:** layout source pins (the stale header grid gone, the wrap band present, the four-tab count and the resize attributes, the migrated content keys) with the new tab keys resolving in both cultures; the existing chrome, accessibility and indicator-gate pins stay green.
- **Status:** review. Canon: the branch carries the claim `46875e8` and the work commit `641c99d` — the two view restructures, two `Settings.*` resx keys en+ru, the 8 `WindowLayoutPinsTests` pins beside the untouched chrome/accessibility/indicator-gate pins, the CHANGELOG section and the alpha.91→alpha.92 bump. The rebase resolved against T-103's detachable-panel restructure: MainWindow keeps the develop structure (the chart header moved into the `ChartPanelView` panel host), the wrap band rides `ChartPanelView.axaml` — the full toolbar including the T-091 drawing tools folds there, and the two chart-toolbar pins read the panel source with `ToolCommand` added to the wired set. The develop landing awaits review and the user's merge call.

## T-149 - Stabilize the App.Tests heatmap stale-epoch flake: a completion-driven settle pin

- **Branch:** feat/app-test-flak, worktree zt-app-test-flak, based on develop@772a39c (claimed 2026-11-10 on develop@243c50a, re-based onto 772a39c when T-103/T-129/T-132 landed mid-flight; the stabilization work starts on the user's go).
- **Origin:** the gate's flaky retry keeps absorbing an App.Tests failure: the feat/fixture-leak gate log (2026-11-10) records ZettaTrade.App.Tests.Services.HeatmapFeedServiceTests.AStaleEpochComposition_PublishesNothing failing the first run and passing the retry ("passed on flaky retry; consider stabilizing the timing-sensitive test"); an earlier gate run showed the same rescue, but gate-last.log keeps only the last run, so that occurrence is no longer reproducible from logs. The retry is the safety net, not a fix - the flake is pre-existing on develop and surfaces on unlucky scheduling. Third member of the stabilization family after T-135 and T-138.
- **What:** the fixture's settle pin is a bounded Task.Yield() spin-poll whose budget is calibrated for an idle machine (the while/SpinAbortBudget loop after parkedGate.SetResult()): under the gate's solution-parallel load the pool-starved pipeline outlives the budget and the poll aborts loudly - the recorded FAIL. Make the pin completion-driven instead of spin-driven: await the outcome's own TCS (pumpConnect) behind a timeout guard, so the wait completes exactly when the swap's composition arms the demo pump, a never-landing composition still fails loudly on the timeout, and no spin budget can be exhausted by scheduling noise; the coverage intent stays and the gate's retry remains belt-and-suspenders.
- **Scope:** tests/ZettaTrade.App.Tests only (the HeatmapFeedServiceTests fixture); no product code, no version bump (test stabilization, matching the T-135/T-138 precedent).
- **Acceptance:** a streak of consecutive first-run green runs of the full App.Tests suite (target: ten, zero retry absorptions), the stabilized test keeps its original coverage intent (the stale-epoch drop still asserted through the real pump connection), the gate stops printing the flaky-retry warning on this path.
- **Tests:** the stabilized fixture plus the repeated first-run streak; the gate over the branch.
- **Status:** planned
