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
| T-047 [Critical] MCP server drops every response for numeric JSON-RPC ids (BUG-002) | fix/mcp-numeric-ids | queued | - |
| T-048 [Critical] Bybit reconnect resubscribes cancel each other (reconnect churn with 2+ symbols) (BUG-003) | fix/bybit-resubscribe | queued | - |
| T-049 Secret stores: silent data loss and rule violations across DPAPI/Keychain/secret-tool/encrypted file | fix/secrets-stores | queued | - |
| T-050 MCP pipelines: host lock stall, client id matching, unread stderr, tools enum defaults | fix/mcp-pipelines | queued | - |
| T-051 Paper trading ledger: corrupted entry price, stale reference price, phantom fills | fix/paper-ledger | queued | - |
| T-052 WebSocket transport: dead feed after reconnect, send races, frame parser, ignored depth | fix/ws-transport | queued | - |
| T-053 Settings service: reload races erase just-written values, changes never published | fix/settings-hot-reload | queued | - |
| T-054 Indicator plugin scan aborts on a plugin with missing dependencies | fix/indicator-scan | queued | - |
| T-055 Chart VM: persisted 12-hour clock lost in the common restore path + symbols bootstrap race | fix/chart-vm-restore | queued | - |
| T-056 App VM error paths: unlogged startup crash, silent order failures, off-thread and inconsistent UI | fix/app-vm-error-paths | queued | - |
| T-057 Risk: market/stop orders bypass the hard order-value cap | fix/risk-cap | queued | - |
| T-058 UI primitives: concurrent async helper vs contract, pan pitch mismatch, undisposed SKPath | fix/ui-primitives | queued | - |
| T-059 Themes: burst hot-reload drops files, watcher death, unordered live list, stale order book | fix/theme-hot-reload | queued | - |
| T-060 Localization: culture race in the cached lookup + suspected binding lifetime leak | fix/localization-races | queued | - |
| T-061 Order history store: terminal-status rewrite + upsert check-then-act race | fix/order-history-store | queued | - |
| T-062 XDG path resolver returns CWD-relative paths for empty environment variables | fix/xdg-paths | queued | - |
| T-063 The closed-task archive: sections leave the board, the archive rule and the reply language codified (supervisor-direct) | feat/task-archive | done | - |
| T-064 Worktree lifecycle: remove the task folder and branch after merge (supervisor-direct) | feat/worktree-cleanup | done | - |

## Code review 2026-11-04 — findings index (source of T-046…T-062)

Full analysis: `docs/CODE_REVIEW.md` (56 findings: 3 Critical, 23 Major, 30 Minor, all verified
against the code at cited lines; every entry carries a stable `BUG-NNN` id). The findings are
filed as T-046…T-062, one task per code area; the three Critical findings each get their own
task. `docs/CODE_REVIEW.md` is the canon of definitions and per-finding line references; when a
task lands, its fixing agent flips the covered `BUG-NNN` statuses to Fixed there, in the same
commit series as the work. Areas explicitly cleared by the review (no bug found) are listed at
the end of that file, so no effort is duplicated.


---

## T-038 — Draggable splitters: resize the chart / depth / assistant columns (user-reported)

- **Branch:** feat/layout-splitters (user-reported 2026-11-03 right after merging T-027: «хотелось бы менять размер (сдвигать) график/стакан/аи-панель» — the user wants to resize/shift the chart, the order book and the AI assistant panel columns). Grounding: `MainWindow.axaml` pinned the workspace as a fixed `*,300` inner pair (chart/book) and, after T-027, the outer `*,Auto` grid gave the assistant a fixed `Width="300"` Border — nothing in the layout was draggable.
- **What:** two vertical `GridSplitter`s — between the chart and the book, and between the book and the assistant panel. The dragged widths persist as `UI.*` keys (UiSettingsKeys, the reactive settings pipeline) and restore BEFORE the first frame (startup-walk parity with `ShowAiPanel`: no relayout flash). The assistant's fixed `Width` pin becomes the persisted bound width of its column when visible; the AI-side splitter hides together with the panel (`IsAiChatVisible` false ⇒ no grab handle parked in an empty gutter). Min-width clamps per column (the book and the chat need usable minimums; the chart keeps the rest).
- **Scope:** `MainWindow.axaml` (+ the width-owning ViewModel mirror of `ChatViewModel`'s real-difference persistence discipline), `UiSettingsKeys`, tests. Core untouched; no resx (splitters carry no labels).
- **Acceptance:** probe (UIA geometry, the T-027 harness pattern): drag the chart/book splitter → the book column reflows (its left edge follows the drag), restart → the positions survive cold start; with the AI panel hidden the AI-side splitter is absent from the UIA tree; both widths round-trip.
- **Tests:** the persisted-clamp/restore facts (mirror `ChatViewModelTests`: restore without rewrite, changes-stream echo absorbed, min-width clamp on garbage/stored-out-of-range values), the hide-follows-show wiring when the panel toggles while a custom width is stored.
- **Status:** done. Canon: commit `168879b` is on feat/layout-splitters. The UIA geometry probe (drag → reflow, the hidden splitter absent from the UIA tree) is left for the merge review; the identical mechanics are verified headless against Avalonia 12.1.4: a TwoWay `GridLength` binding on `ColumnDefinition.Width` writes back through a real `GridSplitter` resize, the star workspace stays one and the pixel column resizes alone, `MinWidth` clamps the drag, programmatic pushes still reflow after a splitter left a local value behind, and the drag persists once per settled burst (the probe used the keyboard route, which runs the same `MoveSplitter` path a mouse drag does). Wave note: the release version bump + CHANGELOG land once for the whole fixed-wave in a separate release branch after the merges — a supervisor-approved deviation from rule 6 for parallel work.

## T-040 — MCP server settings in the settings window (user idea)

- **Branch:** feat/mcp-settings (user idea 2026-11-04: «можно mcp вынести в окно настроек, также как и настройки ии ассистента»)
- **Origin:** the local MCP server (the AI assistants' trading tools) is wired at startup with no user-facing control — its state lives only in config files, while the assistant itself already owns a settings surface (T-032).
- **What:** a settings-window section for the MCP server: the enable state (+ persist) and the knobs the server host exposes — a live toggle starts/stops the local host in-process (or defers the start to the next launch when the transport cannot rebind), the running/stopped state renders honestly. Stdio purity stays untouched (nothing ever writes to stdout except MCP protocol messages — the gate pins it).
- **Scope:** `SettingsViewModel` + `SettingsWindow.axaml`, the settings keys (`Mcp.*` prefix), `McpServerHost` wiring, resx en+ru, tests.
- **Acceptance:** probe (the T-030/T-038 settings harness): the toggle round-trips (window open → flip → Save → restart → the state survives); the live toggle flips the host without a restart when possible, otherwise the note says the next launch applies it.
- **Tests:** the toggle VM facts (persisted-state restore, echo absorption, real-difference discipline), the host start/stop path, the state surfacing (running / deferred / failed with a log).
- **Status:** done. Canon: commit `269d33b` is on feat/mcp-settings; the gate prints `GATE PASSED` (baseline develop) and the 16 new tests (7 runner facts, 9 settings-tab facts) are green: the default keeps the server untouched (the shipped behavior survives), the stored enable choice starts the host at the walk, a refused start lands the honest Failed state with the choice stored for the next launch (nothing throws to the caller), the save persists only a real difference and applies the flip live both ways, external flips feed the next open's draft through the seed cache only, non-bool stream payloads cannot poison the choice, and the three state rows stay mutually exclusive. The manual UI round-trip probe (window open → flip → Save → restart) stays with merge review — the window itself was not probed headless. The host runner ships as `McpHostService` (registration in `CompositionRoot` after `IMcpServerHost`), keys `Mcp:ServerEnabled` defaulting to off. Wave note: the release version bump + CHANGELOG land once for the whole fixed-wave in a separate release branch after the merges — a supervisor-approved deviation from rule 6 for parallel work.

## T-041 — Telegram trade notifications (post-beta)

- **Branch:** queued 2026-11-04 (user idea: «в дальнейшем я хочу чтобы программа отправляла информацию о сделках в телеграм (но это не срочно, после доведения до бета)») — deliberately parked after the beta milestone.
- **Origin:** fills and order events land only in the journal and the logs today; the trader wants them pushed where he actually reads.
- **What:** a notification sink that sends order/fill event messages (symbol, side, price, quantity, exchange, paper/live tag) to a user's Telegram chat through a bot token stored in `ISecretStore` (a token never travels as a command-line argument — stdin/secret-store only, the standing security rules). Opt-in per event type (fills, cancels, rejections), async and BEST-EFFORT: a failed send logs a warning and never blocks, retries or corrupts the trading flow; no third-party middleman services — the token is the user's own bot.
- **Scope:** a new notification service in Infrastructure riding the existing trading/journal event surface, the settings keys (`Notifications.*`), `ISecretStore` token, resx en+ru message templates, tests.
- **Acceptance:** e2e probe against a LOOPBACK Telegram API stub (the T-032 harness pattern): a fill event produces the expected message POST; a stub 500/timeout degrades to a logged warning with the trading flow untouched; the flag off or the token missing means quietly no sink.
- **Tests:** the message composition golden (en/ru templates), the event filtering, the best-effort failure matrix (timeout, 5xx, malformed response).

## T-044 — UI color hygiene: XAML literals and HTML entities out, colors into constants/themes (supervisor-direct)

- **Branch:** feat/ui-colors (supervisor-direct task, not board-originated)
- **What:** the two HTML-entity escapes in XML docs replaced with plain text; the Skia fallback colors named as `static readonly` constants (`CandlestickChart`, `OrderBookWidget`); the XAML carries no color literals anymore: the favorite stars consume the new `Highlight` theme key, the three error texts the new `Error` key, the chat timestamps the existing `TextSecondary`; both keys ship in every theme file plus the sample theme and the theme-key doc lists follow.
- **Status:** done. Canon: the branch carried `f9bdfb3` (the refactor) + `77edb66` (the version cut) and landed through the merges `e3f3f8a` + `fd478ca`; the build shipped as `0.1.0-alpha.21` (see CHANGELOG.md).

## T-045 — Workflow bookkeeping codified: develop-based branches, TASKS.md statuses, app version bump (supervisor-direct)

- **Branch:** feat/skill-rules (supervisor-direct task, not board-originated)
- **What:** `AGENTS.md` and the registered skill `.opencode/skills/zettatrade/SKILL.md` carry the three bookkeeping rules: feature branches base on `develop` (`scripts/new-worktree.ps1` defaults to `main`), a task's status moves are written into `docs/TASKS.md` while they happen (board row + section), and every app-changing task cuts its alpha version in `Directory.Build.props` + a `CHANGELOG.md` section; the gate is invoked with `-BaseBranch develop`; the stale T-039 row/section flipped to done (canon `3f31a7d`, branch tip `9b9bece`, shipped as `0.1.0-alpha.20`).
- **Status:** done. Canon: commit `8a0651a` is on develop. Docs-only: no version bump (rule 6).

## T-046 — DPAPI secret store wipes all stored secrets after a silent load failure (BUG-001, Critical)

- **Branch:** feat/dpapi-store-wipe (findings of the 2026-11-04 code review; full definitions and line references live in `docs/CODE_REVIEW.md`)
- **Origin:** the secret store is the last line of defense for user keys; a silent wipe destroys every stored secret on the first corrupt file - the exact scenario the trading-safety rule "never overwrite encrypted secrets when decryption fails" forbids.
- **What:** `BUG-001` - `WindowsDpapiSecretStore.LoadStoreAsync` logs and swallows every load failure (corrupt JSON, invalid base64, transient read error) and continues with an empty/partial `_cache`; the very next `SetAsync` -> `SaveStoreAsync` rewrites the whole file from that cache, permanently destroying all previously stored secrets. `InitializeAsync` never surfaces the failure.
- **Scope:** `src/ZettaTrade.Infrastructure/Secrets/WindowsDpapiSecretStore.cs` (a failure-surfacing path out of `InitializeAsync`; if an interface change is needed, escalate).
- **Acceptance:** a corrupt/truncated/unreadable store file fails loudly at initialization and is never overwritten by a subsequent `SetAsync`; secrets become unusable-not-lost (an explicit error surfaced, a logged condition); the happy path is unchanged.
- **Tests:** the load-failure matrix (missing / corrupt JSON / bad base64 / locked file) with the no-wipe invariant pinned (after a failed load, a `SetAsync` attempt leaves the file byte-identical and reports the error).
- **Status:** done. Canon: commit `d6896a2` is on feat/dpapi-store-wipe; the load-failure matrix (corrupt/truncated JSON, invalid base64, locked file) + the happy-path roundtrip pass and the Initialize failure surfaces loudly (the store then refuses all operations; the on-disk secrets are never overwritten). The optional explicit `storePath` constructor override is a test seam (the interface is unchanged). Wave note: the release version bump + CHANGELOG land once for the whole fixed-wave in a separate release branch after the merges — a supervisor-approved deviation from rule 6 for parallel work.

## T-047 — MCP server drops every response for numeric JSON-RPC ids (BUG-002, Critical)

- **Branch:** queued 2026-11-04 (code review 2026-11-04; definitions in `docs/CODE_REVIEW.md`)
- **Origin:** every mainline MCP client numbers its JSON-RPC requests (`"id": 1, 2, ...`); against our server they all hang forever - the basic handshake with real clients is broken.
- **What:** `BUG-002` - the response id is parsed with `GetString()`, which returns null for numeric ids, so the `id != null` gate drops every response; compounded by the initialize reply hardcoding `id = "1"` instead of echoing the request's id.
- **Scope:** `src/ZettaTrade.AI/Mcp/McpServerHost.cs`.
- **Acceptance:** ids travel through unchanged: string, numeric and null ids all produce correctly matched replies; the initialize reply echoes the initialize request's id; a numeric-id client completes handshake + a tool call without hanging.
- **Tests:** the id-shape matrix (string / integer / null / missing) against the request loop; an end-to-end numeric-id round-trip (tools/call) with a scripted client.

## T-048 — Bybit reconnect resubscribes cancel each other (reconnect churn with 2+ symbols) (BUG-003, Critical)

- **Branch:** queued 2026-11-04 (code review 2026-11-04; definitions in `docs/CODE_REVIEW.md`)
- **Origin:** after any network blip a user tracking 2+ symbols enters an unrecoverable reconnect loop with no market data until a restart - the feed degenerates to nothing silently.
- **What:** `BUG-003` - the resubscribe pipeline is `_resubscribeStreams.Select(...).Switch()`: a new emission disposes (cancels) the previous in-flight inner subscription. `HandleConnectionStateChanged` emits all tracked symbols synchronously in one `foreach` without awaiting, so with >= 2 symbols every resubscribe send but the last is cancelled mid-`SendAsync`; cancelling an in-flight send aborts the socket and re-triggers the reconnect. The same flaw rides the gap-recovery path (a new gap frame kills an in-flight resubscribe).
- **Scope:** `src/ZettaTrade.Infrastructure/Exchange/BybitAdapter.cs`.
- **Acceptance:** with 3 tracked symbols a reconnect resubscribes all three to completion (no in-flight send aborted); gap recovery serializes against in-flight resubscribes; no reconnect churn loop.
- **Tests:** a loopback WebSocket fake that counts overlapping `SendAsync` calls and records cancellations; the 2- and 3-symbol reconnect scenarios; a gap-frame-during-resubscribe scenario.

## T-049 — Secret stores: silent data loss and rule violations across platforms (BUG-009, BUG-010, BUG-011, BUG-036...BUG-041)

- **Branch:** queued 2026-11-04 (code review 2026-11-04; definitions in `docs/CODE_REVIEW.md`)
- **Origin:** the stores share the files, so one branch fixes the whole surface; two findings violate standing trading-safety rules (secrets via stdin only; never overwrite on a failed write), the rest are silent data loss on all platforms.
- **What:**
  - `BUG-009` - macOS keychain passes the secret as a command-line argument (`"-w", value`), exposing it in the process list; `-w` without a value does read stdin (the comment claiming otherwise is factually wrong).
  - `BUG-010` - DPAPI `SaveStoreAsync` swallows all write errors while `SetAsync`/`RemoveAsync` report success: a failed write leaves the secret memory-only; a deletion is silently undone after restart.
  - `BUG-011` - DPAPI `ListKeysAsync` blocks synchronously on `_lock.Wait(ct)` inside an async contract method: freeze/deadlock risk while a write holds the semaphore across file I/O.
  - `BUG-036` - `ISecretStore.ListKeysAsync` always returns an empty list on macOS and Linux.
  - `BUG-037` - on timeout/cancellation the `security`/`secret-tool` child process is never killed (`using Process` disposes the handle, not the process): an orphan that may hold a keychain prompt.
  - `BUG-038` - the keychain update is a non-atomic destroy-then-create: a failure between the steps loses the old secret; the empty `catch { }` swallows real keychain errors.
  - `BUG-039` - DPAPI `InitializeAsync` -> `LoadStoreAsync` reassigns `_cache` without holding `_lock`: a concurrent early `SetAsync` is clobbered and then erased from disk by the next save.
  - `BUG-040` - DPAPI `GetAsync` returns null on any decryption failure, conflating "corrupt/unreadable" with "missing".
  - `BUG-041` - `EncryptedFileSecretStore` on a truncated file silently "starts fresh" and the next `SetAsync` overwrites the undecryptable file (fails the never-overwrite rule; the content is unrecoverable either way).
- **Scope:** `src/ZettaTrade.Infrastructure/Secrets/` (`WindowsDpapiSecretStore.cs`, `MacOSKeychainSecretStore.cs`, `LinuxSecretServiceStore.cs`, `EncryptedFileSecretStore.cs`).
- **Acceptance:** secrets travel via stdin only; write failures surface (no swallowed success); `ListKeysAsync` works on all platforms; children die on timeout; the update path is atomic or fails with the old secret intact; decrypt failures are distinguishable from missing keys.
- **Tests:** per-bug invariant assertions; the never-overwrite invariant for the DPAPI + encrypted-file pair; platform guards documented (Windows tests run everywhere; macOS/Linux paths skipped with the guard noted).

## T-050 — MCP pipelines: host lock stall, client id matching, unread stderr, tools defaults (BUG-007, BUG-008, BUG-020...BUG-024, BUG-056)

- **Branch:** queued 2026-11-04 (code review 2026-11-04; definitions in `docs/CODE_REVIEW.md`)
- **Origin:** the host and the client are the AI integration's two halves and the tools are its trading surface; the tools findings drop the "never substitute defaults for unparsable enums" rule - invalid orders flow toward execution.
- **What:**
  - `BUG-007` - `TradingToolsRegistrar` parses side/type with `Enum.TryParse`: numeric and undefined values pass (`"999"` -> `(OrderSide)999`), and `OrderRequest.Validate` never checks enum definedness either (a Core concern if it needs a contract change - escalate).
  - `BUG-008` - an unparsable `timeInForce` silently defaults to `GTC`; an unparsable `timeframe` silently defaults to `H1` (the model asks M1 and gets H1 candles).
  - `BUG-020` - the host holds `_lock` across `await tool.Handler(...)` (which may await a confirmation dialog): `RegisterTool`/`RegisterResource`/`RegisterPrompt` and `tools/list` block synchronously on `_lock.Wait()` - a UI-thread registration stalls until the tool call completes (deadlock risk).
  - `BUG-021` - the client's stderr drain is a fire-and-forget `_ = Task.Run(...)`: its exceptions are unobserved, and once the task dies the child's stderr pipe can fill and block the server process.
  - `BUG-022` - reconnecting while connected overwrites `_serverProcess`/`_httpClient` without killing the previous child (a leaked process); `DisposeAsync` calls `Kill` on a process that never started (`InvalidOperationException` thrown out of disposal).
  - `BUG-023` - the stdio reply is never matched to the request id (a random Guid); any interleaved server line (notification/log) desynchronizes the stream; a cancelled `ReadLineAsync` leaves a half-consumed line corrupting subsequent reads.
  - `BUG-024` - HTTP auth resolves `AuthSecretRef` to a hardcoded `"placeholder-token"` Bearer header instead of the secret store (HTTP authentication never works; secrets bypass `ISecretStore`).
  - `BUG-056` - malformed requests (missing `method`/`params.name`, bad JSON) and unknown methods with an id produce no JSON-RPC error reply (the client waits forever); `StopAsync` cancels and immediately disposes `_cts` while the run loop may sit blocked in `Console.In.ReadLineAsync`.
- **Scope:** `src/ZettaTrade.AI/Mcp/` (`McpServerHost.cs`, `McpClientService.cs`, `Tools/TradingToolsRegistrar.cs`), `src/ZettaTrade.Core/Entities/OrderRequest.cs`.
- **Acceptance:** no defaults for unparsable/undefined enums (an explicit error per the rule); the host lock is held only over synchronous work (registrations never wait on tool calls); replies are matched by id with desync tolerance; the stderr drain is lifespan-owned; reconnect leaks no child process and disposal never throws; malformed requests get error replies; HTTP auth uses `ISecretStore`.
- **Tests:** a scripted host client (registration while a tool call is in flight), a client framing/desync simulation, the tools parse-rejection matrix (`999`, undefined names, malformed JSON), the HTTP-transport secret path with a fake store.

## T-051 — Paper trading ledger: corrupted entry price, stale reference price, phantom fills (BUG-005, BUG-006, BUG-033...BUG-035)

- **Branch:** queued 2026-11-04 (code review 2026-11-04; definitions in `docs/CODE_REVIEW.md`)
- **Origin:** the paper adapter is the trading simulator behind every demo user; its average entry and unrealized PnL are the numbers the user acts on, and every defect here corrupts exactly them (or lies about a rejection).
- **What:**
  - `BUG-005` - the net-position branch treats any same-sign fill as "extending": a reducing fill is re-averaged with the wrong denominator (long 1 @ 100 -> sell 0.5 @ 150 leaves entry 350 for the remaining 0.5 instead of 100), corrupting average entry and unrealized PnL on every partial close.
  - `BUG-006` - the reference price takes `bars[^1]` of a from-anchored `limit: 1` window (`now - 24h`): on ascending-order backends (Binance klines oldest-first) that is a ~24-hour-old close, so paper market fills and limit-crossing decisions use a day-old price; Bybit/Demo differ silently.
  - `BUG-033` - `FillAndApply` records the Filled order into `_orders` before the balance validation inside `ApplyFillToPortfolio`: a balance-rejected order leaves a phantom Filled record while the caller receives an exception.
  - `BUG-034` - the crossing-zero branch opens a fresh opposite position at the fill price but never resets `OpenedAt`: the new position carries the closed one's timestamp.
  - `BUG-035` - `CancelOrderCoreAsync` waits on `_tradingLock.WaitAsync()` without the caller's `ct`: a wedged lock holder blocks cancellation indefinitely.
- **Scope:** `src/ZettaTrade.Infrastructure/Exchange/PaperTradingAdapter.cs`.
- **Acceptance:** a reducing fill never changes the average entry of the remaining size; the reference close is the newest bar on every backend (an explicit ordering contract with the adapters, e.g. a max-window fetch and a latest-close selection); a balance rejection persists no Filled order; a fresh position carries its own fill-time `OpenedAt`; cancel respects the caller's cancellation.
- **Tests:** the ledger scenarios (extend / partial close / cross-zero / balance rejection) against an ascending and a descending history fake; the cancel-with-cancelled-token scenario.

## T-052 — WebSocket transport: dead feed after reconnect, send races, frame parser, ignored depth (BUG-004, BUG-027...BUG-032)

- **Branch:** queued 2026-11-04 (code review 2026-11-04; definitions in `docs/CODE_REVIEW.md`)
- **Origin:** the transport is the market-data lifeline; the BUG-004 defect silences every feed permanently after the first transient disconnect - the worst failure class in a trading terminal (stale prices presented as live).
- **What:**
  - `BUG-004` - the background loop silently reconnects the socket but nothing ever re-sends the `SUBSCRIBE` frames (stream starts run only on new Rx outer subscriptions): after any transient disconnect the ticker/depth/trade feeds of all subscribed symbols go silent forever while the Rx subscriptions stay alive; a subscriber disposing mid-start cancels the start's send token and aborts the shared socket, making the feed unrecoverable.
  - `BUG-027` - the Bybit trade-frame parser calls `TryGetProperty("S")` before the `ValueKind != Object` check: a non-object array entry throws `InvalidOperationException` and aborts all remaining trades of the frame instead of being counted as dropped (contradicting the method's own doc).
  - `BUG-028` - `SubscribeSymbolAsync`/`UnsubscribeSymbolAsync` send without the `_streamStartGate` used by `StartSymbolStreamsAsync`: an ungated unsubscribe concurrent with a gated subscribe produces two simultaneous `ClientWebSocket.SendAsync` calls on one socket -> `InvalidOperationException` ("there is already one outstanding send").
  - `BUG-029` - `ConnectAsync` mutates the shared `_cts` (`Cancel`/`Dispose`/replace) with no synchronization: overlapping calls corrupt the session token (a leaked CTS, or the old loop's `Task.Delay` dies as `ObjectDisposedException`); `_reconnectAttempts` resets only on a successful open, so a failed previous session shrinks the next one's attempt budget.
  - `BUG-030` - `DisconnectAsync` disposes `_webSocket` while the receive loop may be blocked in `ReceiveAsync`: the resulting `ObjectDisposedException` matches none of the loop's inner catches and escapes as a bogus "background loop crashed" error.
  - `BUG-031` - `DemoMarketDataAdapter` increments `++_tradeSequence` on concurrent per-symbol timer threads: lost increments, duplicate `demo-...-{sequence}` trade ids, parity flips of side/price.
  - `BUG-032` - `ExchangeAdapterBase.SubscribeOrderBook(symbol, depth)` silently ignores `depth`: Binance pins 20 levels, Bybit 50; a caller requesting 5 or 100 gets the fixed depth with no error.
- **Scope:** `src/ZettaTrade.Infrastructure/Exchange/` (`BinanceAdapter.cs`, `WebSocketClient.cs`, `BybitAdapter.cs`, `ExchangeAdapterBase.cs`), `src/ZettaTrade.Infrastructure/Demo/DemoMarketDataAdapter.cs`.
- **Acceptance:** a killed-and-rejoined connection resubscribes everything (no silent feed); all socket sends serialize behind one gate; the connect/disconnect lifecycle is race-free and free of bogus errors; malformed trade frames are counted and skipped, never thrown; the depth contract is explicit (honored via a seam change - escalate if `IMarketDataAdapter` needs an interface change - or rejected with a clear error, never a silent default).
- **Tests:** loopback-WS scenarios (drop, rejoin, a resubscribe storm, overlapping sends), the frame-parser matrix, the depth contract test, a demo counter concurrency test.

## T-053 — Settings service: reload races erase just-written values, changes never published (BUG-012, BUG-013, BUG-045)

- **Branch:** queued 2026-11-04 (code review 2026-11-04; definitions in `docs/CODE_REVIEW.md`)
- **Origin:** the settings service is the single persistence interface for every workspace preference; the reload race silently loses user edits - the most painful silently-losing bug inside a trading terminal.
- **What:**
  - `BUG-012` - `LoadFromFileAsync` mutates the shared `_settings` (`Clear()` + repopulate) without `_saveLock`: a debounced external reload interleaved with `SetAsync`/`SaveAsync` erases just-written values and the next save persists the stale snapshot (lost writes).
  - `BUG-013` - the external reload replaces values but never calls `_changes.OnNext`: every `Changes` subscriber keeps operating on stale values (the promised hot-reload contract never reaches consumers).
  - `BUG-045` - the change payload mixes types (`OldValue` boxed `JsonElement?` vs a typed `NewValue`), so subscribers comparing both always mis-handle one side; the `_isSaving` guard resets in `finally` before the `File.Move` event is delivered, so every save can self-trigger a reload and widen the BUG-012 race.
- **Scope:** `src/ZettaTrade.Infrastructure/Configuration/SettingsService.cs` (+ the change payload type; keep `SettingsChange` backward compatible or escalate).
- **Acceptance:** external reloads serialize with saves (no lost writes); every external change publishes a well-typed `SettingsChange`; a save never self-triggers its own reload (the guard covers the whole delivery window); old and new values share one type.
- **Tests:** the lost-write race repro pinned first, then the fix; the change-publication assertions; the save-produces-zero-reload-events assertion.

## T-054 — Indicator plugin scan aborts on a plugin with missing dependencies (BUG-014)

- **Branch:** queued 2026-11-04 (code review 2026-11-04; definitions in `docs/CODE_REVIEW.md`)
- **Origin:** the documented plugin-loader contract says a malformed plugin is skipped with a logged warning and the chart keeps working; with a missing dependent assembly the scan instead throws - the whole indicator seam degrades into a startup failure.
- **What:** `BUG-014` - `assembly.GetTypes()` is called outside the try (which only wraps `LoadFrom`): a plugin DLL whose dependencies are missing throws `ReflectionTypeLoadException` out of the catalog scan instead of skipping the file.
- **Scope:** `src/ZettaTrade.Infrastructure/Indicators/PluginIndicatorCatalog.cs`.
- **Acceptance:** a plugin that fails reflection logs its warning and is excluded; the remaining plugins of the directory still load; the scan itself never throws.
- **Tests:** extend the loader matrix with a dependency-missing plugin fixture (the garbage-dll case exists; this one covers the `GetTypes()` unwrap step).

## T-055 — Chart VM: persisted 12-hour clock lost in the common restore path + symbols bootstrap race (BUG-015, BUG-016)

- **Branch:** queued 2026-11-04 (code review 2026-11-04; definitions in `docs/CODE_REVIEW.md`)
- **Origin:** every restart with the default `H1` time frame silently reverts the clock format the user configured; and rapid exchange switches can land the toolbar on a stale symbol list and overwrite an explicit user pick during a fetch.
- **What:**
  - `BUG-015` - `InitializeAsync` restores the persisted clock setting only at the tail of the time-frame restore path; the early returns (no stored TF, unparsable TF, restored == default `H1` - the common case) skip it: the chart silently re-renders the 24-hour clock on every restart.
  - `BUG-016` - `BootstrapSymbols` has no epoch/guard: every `AdapterChanges` emission starts an independent fetch and last-completed delivery wins `AvailableSymbols`/`SelectedSymbol`; two rapid exchange switches deliver out of order and the old exchange's list clobbers the new one's; a user pick made during the fetch is overwritten by the stale persisted value read afterwards.
- **Scope:** `src/ZettaTrade.App/ViewModels/ChartViewModel.cs`.
- **Acceptance:** the persisted clock is applied in every restore path (no-TF / unparsable / default `H1` / custom alike); an exchange switch lands on the newest fetch regardless of delivery order and never overwrites an explicit user pick (the epoch/guard pattern the chart's older-history machinery already uses).
- **Tests:** the restore matrix (no TF / unparsable / default `H1` / custom); the switch-race scenario (two reversed fetch completions + a mid-fetch user pick).

## T-056 — App VM error paths: unlogged startup crash, silent order failures, off-thread and inconsistent UI (BUG-017...BUG-019, BUG-053...BUG-055)

- **Branch:** queued 2026-11-04 (code review 2026-11-04; definitions in `docs/CODE_REVIEW.md`)
- **Origin:** all six findings share one failure class: the user cannot see what went wrong, or the UI quietly misrepresents state/time; in a trading terminal, silent failure is the costliest class after data loss.
- **What:**
  - `BUG-017` - the order dialog catches only `InvalidOperationException`, but `PlaceOrderAsync` can throw network exceptions via the paper adapter's reference-bar REST call; they escape to `ReactiveCommand.ThrownExceptions`, which nothing observes: the order is silently not placed (no message, no journal entry).
  - `BUG-018` - the StatusBar fallback-notice `Observable.Timer(...)` callback runs on a ThreadPool thread and sets the UI-bound `ConnectionStatus` without `ObserveOnUiThread` (reached when the persisted exchange id is unregistered at startup).
  - `BUG-019` - `OnFrameworkInitializationCompleted` is `async void` with no try/catch: any exception in `BuildServiceProviderAsync`/`ApplyStoredUiSettingsAsync` crashes the process before the window exists, with nothing in the Serilog file log.
  - `BUG-053` - the chat `_persistedShowAiPanel` cache is updated in completion order of concurrent `SelectMany(FromAsync)` writes: two rapid toggles can complete out of order and a subsequent toggle to the stale-cached value is filtered out - the restart restores the old visibility.
  - `BUG-054` - the positions panel catches only `InvalidOperationException`: other cancel/reload failures escape to unobserved `ThrownExceptions` or the window's subscription (stale/empty lists, no error line).
  - `BUG-055` - the positions panel formats stored UTC timestamps as-is while the history window calls `.ToLocalTime()`: the same order shows times differing by the local UTC offset between the two windows.
- **Scope:** `src/ZettaTrade.App/ViewModels/` (`NewOrderDialogViewModel.cs`, `StatusBarViewModel.cs`, `ChatViewModel.cs`, `PositionsPanelViewModel.cs`, `OrderHistoryViewModel.cs`), `src/ZettaTrade.App/App.axaml.cs`.
- **Acceptance:** every listed failure lands as a visible localized error (orders also in the journal); no UI-bound property is written off the UI thread; startup exceptions reach the file log with the fail-fast path explicit; both windows render one timestamp convention (stored UTC -> local, localized).
- **Tests:** per-bug pins (the uncaught-exception scenarios over the VM throw surfaces; a marshaling assert on the timer path; a bootstrap-failure log assert; the timestamp convention held across the two views).

## T-057 — Risk: market/stop orders bypass the hard order-value cap (BUG-026)

- **Branch:** queued 2026-11-04 (code review 2026-11-04; definitions in `docs/CODE_REVIEW.md`)
- **Origin:** `MaxOrderValueHardCapUsd` is the terminal's last automatic damper against a catastrophic order; its own documentation says it "must never be bypassable by any caller". Today any price-less order bypasses it entirely and relies solely on a human dialog - one fat-fingered market order of any size sails through as a routine flow.
- **What:** `BUG-026` - `DefaultRiskManager` checks `MaxOrderValueHardCapUsd` only for orders with an explicit price; market/stop orders (no estimable value from the request) return `Approved(requiresConfirmation: true)` without any cap check.
- **Scope:** `src/ZettaTrade.App/Services/DefaultRiskManager.cs`.
- **Acceptance:** no order of any type/side slips past the cap; an order without an estimable value is either approximated against the last available price or rejected with an explicit "cannot evaluate risk" error - never silently approved; the estimate-vs-reject decision is agreed with the supervisor at review (both satisfy the rule).
- **Tests:** the cap matrix across order types with and without an explicit price; the reject path's error reporting.

## T-058 — UI primitives: concurrent async helper vs contract, pan pitch mismatch, undisposed SKPath (BUG-025, BUG-048, BUG-049)

- **Branch:** queued 2026-11-04 (code review 2026-11-04; definitions in `docs/CODE_REVIEW.md`)
- **Origin:** one seam, one theme: small contract violations with cumulative cost - serialized semantics silently running concurrently, a pan moving by the wrong distance, native memory churn on every redraw.
- **What:**
  - `BUG-025` - `ObservableAsyncExtensions` documents serialized-in-emission-order execution, but the implementation `.Select(...).Merge()` runs the per-notification tasks concurrently.
  - `BUG-048` - pan pacing computes the bar pitch from `Bounds.Width` while the draw pass uses `plotRect.Width` (bounds minus the measured price gutter): dragging "one bar" moves the window by a different distance than the painted bar width.
  - `BUG-049` - the `SKPath` returned by `path.Detach()` is never disposed (`DrawPath` copies; the wrapper waits for its finalizer): native memory churn on every redraw for every indicator line.
- **Scope:** `src/ZettaTrade.UI/Extensions/ObservableAsyncExtensions.cs`, `src/ZettaTrade.UI/Charting/CandlestickChart.cs`.
- **Acceptance:** the helper runs its per-notification tasks strictly in order (the contract decides the mechanism, e.g. concatenation/queueing instead of `Merge`); pan pacing and painted pitch share one computed constant; the draw pass retains no undisposed `SKPath`.
- **Tests:** the helper's ordering test; the pan-parity assert (one pitch = the drawn pitch, measured through the same layout); a path-dispose regression assert.

## T-059 — Themes: burst hot-reload drops files, watcher death, unordered live list, stale order book (BUG-046, BUG-047, BUG-050)

- **Branch:** queued 2026-11-04 (code review 2026-11-04; definitions in `docs/CODE_REVIEW.md`)
- **Origin:** the hot-reload is a headline feature (edit your theme file, see it live) and its failure modes are silent (files dropped, the watcher dead, no error anywhere); a live-list race is a crash risk on every theme enumeration.
- **What:**
  - `BUG-046` - one global `Throttle` collapses a burst spanning different theme files into a single event: only the last-changed file reloads, others changed in the same 150 ms window never do; the FSW `Error` handler only logs: after an internal-buffer overflow the watcher stops raising events and hot-reload silently dies.
  - `BUG-047` - `AvailableThemes` returns the live `List<ThemeInfo>` that the background hot-reload pipeline mutates under `_lock`: an unsynchronized consumer enumerating it throws `InvalidOperationException`.
  - `BUG-050` - `OrderBookWidget` never subscribes to `ThemeChanged`/`LanguageChanged` (unlike the chart): after a theme or language switch the drawn book (colors, column headers) stays stale until the next `Book` snapshot arrives - indefinitely while disconnected.
- **Scope:** `src/ZettaTrade.UI/Themes/` (`ThemeFileWatcher.cs`, `ThemeManager.cs`), `src/ZettaTrade.UI/Charting/OrderBookWidget.cs`.
- **Acceptance:** a burst over N files reloads all N; a watcher error restarts the watch (a logged, not silent, failure); `AvailableThemes` hands out an immutable snapshot; the order book re-renders live on theme and language changes.
- **Tests:** the watcher burst + overflow scenarios; the snapshot contract (no mutation while enumerated); a widget render test on theme/language change.

## T-060 — Localization: culture race in the cached lookup + suspected binding lifetime leak (BUG-051, BUG-052 suspected)

- **Branch:** queued 2026-11-04 (code review 2026-11-04; definitions in `docs/CODE_REVIEW.md`)
- **Origin:** the language switch is a runtime feature across the whole terminal; the cache race hands a racing caller a string in the wrong culture; BUG-052 is a suspected leak - investigate first, then fix or close with evidence.
- **What:**
  - `BUG-051` - `GetString` reads the cache key from `_currentCulture` before `GetOrAdd`: a concurrent language switch clears the cache and a racing caller receives a string in the wrong culture (self-heals on the next switch).
  - `BUG-052` (suspected) - the `{loc:Localized}` observable chain (`GetObservable(...)` -> `ToBinding()`) may keep detached/abandoned controls rooted for the app lifetime: VERIFY Avalonia binding weak-reference semantics BEFORE treating this as a confirmed leak.
- **Scope:** `src/ZettaTrade.UI/Localization/` (`LocalizationManager.cs`, `LocalizedExtension.cs`).
- **Acceptance:** a switching storm yields no wrong-culture string at a caller (the cache key is a per-call snapshot); BUG-052 resolved with a documented verdict (fixed, or closed as a false positive with the evidence attached below).
- **Tests:** a concurrency stress (N callers x rapid switches); if the leak confirms, an instance-count/weak-reference probe.

## T-061 — Order history store: terminal-status rewrite + upsert check-then-act race (BUG-042, BUG-043)

- **Branch:** queued 2026-11-04 (code review 2026-11-04; definitions in `docs/CODE_REVIEW.md`)
- **Origin:** the order history is the journal of record for trading activity; rewriting a terminal status falsifies the journal (an order marked Cancelled that actually Filled), and the upsert race surfaces as UNIQUE-constraint errors into the callers.
- **What:**
  - `BUG-042` - `MarkCancelledAsync` unconditionally sets `Status = Cancelled`: a late/duplicate cancel for an order already `Filled`/`Rejected`/`Expired` rewrites the history instead of being ignored.
  - `BUG-043` - the upsert is `FindAsync` then `Add` without a real DB upsert: two concurrent `RecordPlacedAsync` calls for the same order both see null and the second `SaveChangesAsync` fails with a UNIQUE-constraint error instead of updating.
- **Scope:** `src/ZettaTrade.Infrastructure/Persistence/SqliteOrderHistoryStore.cs`.
- **Acceptance:** terminal statuses are final (a cancel attempt on a terminal order is ignored, never a silent rewrite); the placement is a true DB upsert - concurrent `RecordPlacedAsync` calls never surface constraint errors.
- **Tests:** the status-transition matrix (each terminal status x a cancel attempt); the concurrent place/cancel scenario.

## T-062 — XDG path resolver returns CWD-relative paths for empty environment variables (BUG-044)

- **Branch:** queued 2026-11-04 (code review 2026-11-04; definitions in `docs/CODE_REVIEW.md`)
- **Origin:** the Linux platform contract (XDG Base Directory Specification) requires the default when a variable is empty; today an empty `XDG_CONFIG_HOME`/`XDG_DATA_HOME`/`XDG_STATE_HOME` silently produces CWD-relative paths - broken locations in containers and scripts that set variables without values.
- **What:** `BUG-044` - `ConfigPathResolver` falls back only on null (`??`), so an empty-string variable fails the fallback: `Path.Combine` then resolves against the current working directory.
- **Scope:** `src/ZettaTrade.Infrastructure/Configuration/ConfigPathResolver.cs`.
- **Acceptance:** unset OR empty environment variables both fall back to the platform defaults; a non-empty variable wins as before.
- **Tests:** the env matrix (unset / empty / valid) pinned per resolved directory.

## T-063 — The closed-task archive: sections leave the board, the archive rule and the reply language codified (supervisor-direct)

- **Branch:** feat/task-archive (supervisor-direct task, not board-originated; stacked on `feat/bug-board@e25c96a`, merged first).
- **What:** the closed-task sections (T-001...T-037, T-039, T-042, T-043) moved verbatim from `docs/TASKS.md` into the new `docs/TASKS-ARCHIVE.md` (canonical T order; the queued sections T-038/T-040/T-041 stayed on the board); the board header names the archive path; rule 5 of `AGENTS.md` + the registered skill `.opencode/skills/zettatrade/SKILL.md` (bodies kept byte-identical) gained the archive-once-done-and-merged step, and the new rule 9 pins the working language: reason in English, answer in Russian; everything shipped in the repo stays English.
- **Status:** done. Canon: the branch carried `b0192ee` (the archive split + the two rules) + `34e1b91` (three queued sections restored to the board) and landed as a fast-forward on develop. Docs-only: no version bump (rule 6). Worktree `zt-task-archive` and the branch were removed after the merge (rule 1, see T-064).

## T-064 — Worktree lifecycle: remove the task folder and branch after merge (supervisor-direct)

- **Branch:** feat/worktree-cleanup (supervisor-direct task, not board-originated; based on develop after the T-063 merge).
- **Origin:** rule 1 spelled out only the worktree creation; the merged tasks' folders lingered on the disk (`zt-ui-colors`, `zt-skill-rules`, `zt-bug-board`, `zt-task-archive`) with their branches live.
- **What:** rule 1 of `AGENTS.md` (+ the registered skill, identical bodies) and `.opencode/agents/zt-supervisor.md` now say it: once the task is done and merged, remove its worktree folder (`git worktree remove <folder>` — it refuses on uncommitted files, resolve those first) and delete its branch (`git branch -d <name>`); no stale task folders linger around the repo. The first sweep ran alongside this rule: the four merged worktrees above were removed (each was clean and fully merged) and the branches `feat/ui-colors`, `feat/skill-rules`, `feat/bug-board`, `feat/task-archive` were deleted.
- **Status:** done. Canon: this branch's single docs commit (see git log). Docs-only: no version bump (rule 6).
