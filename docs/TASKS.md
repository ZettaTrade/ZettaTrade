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
| T-068 MCP client settings: external servers with transport, address/port and task-level options (supervisor idea) | feat/mcp-client-settings | queued | - |
| T-069 The appearance contracts move into Livery: Core stops owning IThemeManager/ILocalizationManager (supervisor idea) | feat/appearance-contracts | queued | - |

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

## T-067 — Indicator plugin settings render only for plugins that are actually loaded (supervisor idea)

- **Branch:** feat/indicator-settings-gate (base `develop` at `2674f03`; queued 2026-11-05, supervisor idea; developed 2026-11-06).
- **Origin:** the Indicators tab hardcodes the SMA enable/period row: with the plugin-first architecture the surface must reflect the actual scanned catalog, not a shipped assumption — a missing (or skipped-by-scan) plugin assembly would still render a dead row.
- **What:** the scanned catalog contract gains `LoadedNames` — the canonical names of the indicators the plugins actually registered (an immutable snapshot materialized at the end of the startup scan, the plugin set is fixed for the session). `SettingsViewModel` consumes it through constructor injection: the SMA row renders ONLY when the catalog registers the name (`IsSmaPluginLoaded`), the honest empty state shows when the scan registered zero indicators (`IsIndicatorCatalogEmpty` — localized hint naming the app data directory's `plugins` folder and the logged scan skips; the queued text guessed "next to the exe", the real location is the data `plugins` folder and the hint says so), and a save whose plugin the session does not own never rewrites the indicator config at all — the persisted rows ride untouched (the chart pipeline skips unknown names on its own seam, so neither side fabricates). With the plugin loaded the row keeps its exact former contract: the enable/period drafts seed from the persisted config, Save rewrites the `ChartIndicators` array on a real difference only.
- **Scope:** `ZettaTrade.Charting` (`IIndicatorCatalog.LoadedNames` — the member the availability flag drives), `ZettaTrade.Infrastructure/Indicators/PluginIndicatorCatalog.cs` (the snapshot implementation), `SettingsViewModel` (the injected catalog + the two flags + the save gate), `SettingsWindow.axaml` (the conditional row + the empty state), `CompositionRoot.cs` (the catalog wiring), resx en/ru (`Settings.Indicators.Empty`), tests in App/Infrastructure suites.
- **Acceptance:** same-state tests pass with the SMA plugin present (row visible, the persisted period + enable seed the drafts and Save persists a real difference exactly as before — an unchanged save stays silent); with the plugin assembly gone the row vanishes and the empty state shows for a dead catalog (plugins that exist but do not carry the SMA row hide BOTH surfaces — the two flags answer two independent questions); the settings save path never fabricates a missing plugin's config.
- **Tests:** 11 new: the ViewModel gate matrix (loaded → row + no empty state; empty catalog → both hidden; plugins-but-no-SMA → both hidden) and the save trio (a real difference persists the rewritten array, an unchanged save never touches `ChartIndicators`, an absent plugin never fabricates the row — the persisted config rides untouched); the view-source pins (the visibility bindings + the localized hint, en and ru — no headless render harness, the standing precedent); the catalog snapshot facts (a staged SMA assembly registers its canonical name, an empty and a missing plugins directory register no names). Full matrix green (749: 16 Core + 174 UI + 387 App + 172 Infrastructure).
- **Status:** done. Canon: the branch carries `e989eae` (the gate + the 11 test pins) and this release commit (the alpha.33 cut + this section's status flip); shipped as `0.1.0-alpha.33` (see CHANGELOG.md). Gate passed with `-AllowProtected` (the `Directory.Build.props` bump is the supervisor-only protected file).

## T-068 — MCP client settings: external servers with transport, address/port and task-level options (supervisor idea)

- **Branch:** queued 2026-11-05 (supervisor idea).
- **Origin:** the Mcp tab today drives only the LOCAL server side (the trading-tools host: `ServerEnabled`). The MCP CLIENT surface (`IMcpClient`/`McpClientService` supports stdio AND Streamable HTTP transports) is code-only: nobody can attach an external MCP server without editing config files by hand; the supervisor asks for address/port (and other useful parameters) in settings.
- **What:** the Mcp tab gains an external-servers block: per-server entry editing (name, transport Stdio/StreamableHttp, command+args for stdio, address+port for HTTP, enabled flag, optional auth secret-ref key resolving through `ISecretStore` — never a literal secret), a connect/disconnect state row per entry, and the task-level options the transport already exposes (HTTP timeout; bounded stderr drain cannot regress). Config persists under a new settings section; startup connects enabled entries the same `McpClientService.ConnectAsync` path; validation fails loudly (pinned error state + log), the local-server block stays byte-compatible.
- **Scope:** `McpServerConfig` shape (additive fields only), `McpClientService` (config consumption, endpoint validation, auth-ref resolution), `SettingsViewModel` + `SettingsWindow.axaml` (the editor block + status rows), resx en/ru, secrets integration, tests.
- **Acceptance:** e2e loopback HTTP MCP stub (the T-032 harness pattern): a configured address/port connects and lists tools; a stub outage shows the pinned error state, never a hang; stdio entries launch their configured command; a disabled entry never dials; secrets never appear in logs or config.
- **Tests:** the settings round-trip (add/edit/disable entries), the HTTP loopback matrix (happy, 500, timeout, malformed), the stdio launch + stderr drain cases, auth-ref resolution from the fake store

## T-069 — The appearance contracts move into Livery: Core stops owning IThemeManager/ILocalizationManager (supervisor idea)

- **Branch:** queued 2026-11-05 (supervisor idea).
- **Origin:** Livery's first consumer is a non-trading project, so the only remaining brand/hardware tie is the contracts file the supervisor owns (`ZettaTrade.Core/Interfaces`): `IThemeManager`, `ILocalizationManager`, `IAsyncInitializable` (+ the `ThemeInfo`/`LanguageInfo` records) make every library consumer drag the whole Core assembly in just to render a themed `TextBlock`.
- **What:** the appearance contracts relocate into Livery (or a supervisor-chosen neutral seam) so the library references nothing ZettaTrade-branded and can package standalone; the reference direction flips — Core consumes the contracts from Livery, all Core consumers update namespaces; the public surface stays backward compatible where the move allows it (a declare-and-deprecate shim is acceptable if a clean flip is intrusive). The NuGet-pack decision from T-065 is re-evaluated here: with the contracts in-library, `dotnet pack` becomes the intended sharing mode for the second consumer.
- **Scope:** `ZettaTrade.Core/Interfaces/*` (protected; supervisor-owned), `src/Livery` (csproj + the relocated contracts), consumer namespaces (`UI`, `App`, tests), `slnx` reference graph, `AGENTS.md` layout line.
- **Acceptance:** `src/Livery` carries no ZettaTrade reference; a fresh-clone build + tests stay green; `GATE PASSED` (`-AllowProtected` for Core/Interfaces + board docs).
- **Tests:** the existing theme/localization suites ride along (namespace + reference deltas); the build graph pins that Livery compiles before Core (dependency direction provable from the slnx order).
