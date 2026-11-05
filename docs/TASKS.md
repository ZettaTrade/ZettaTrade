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
| T-058 UI primitives: concurrent async helper vs contract, pan pitch mismatch, undisposed SKPath | fix/ui-primitives | queued | - |
| T-059 Themes: burst hot-reload drops files, watcher death, unordered live list, stale order book | fix/theme-hot-reload | queued | - |
| T-060 Localization: culture race in the cached lookup + suspected binding lifetime leak | fix/localization-races | done | - |
| T-061 Order history store: terminal-status rewrite + upsert check-then-act race | fix/order-history-store | done | - |
| T-062 XDG path resolver returns CWD-relative paths for empty environment variables | fix/xdg-paths | done | - |
| T-063 The closed-task archive: sections leave the board, the archive rule and the reply language codified (supervisor-direct) | feat/task-archive | done | - |
| T-064 Worktree lifecycle: remove the task folder and branch after merge (supervisor-direct) | feat/worktree-cleanup | done | - |
| T-065 Localization + themes stand alone: a lean ZettaTrade.Appearance library (supervisor idea) | feat/appearance | done | - |
| T-066 Window chrome + control restyling: styled title bar, buttons, inputs across the shell (supervisor idea) | feat/window-chrome | queued | - |
| T-067 Indicator plugin settings render only for plugins that are actually loaded (supervisor idea) | feat/indicator-settings-gate | queued | - |
| T-068 MCP client settings: external servers with transport, address/port and task-level options (supervisor idea) | feat/mcp-client-settings | queued | - |

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

## T-044 — UI color hygiene: XAML literals and HTML entities out, colors into constants/themes (supervisor-direct)

- **Branch:** feat/ui-colors (supervisor-direct task, not board-originated)
- **What:** the two HTML-entity escapes in XML docs replaced with plain text; the Skia fallback colors named as `static readonly` constants (`CandlestickChart`, `OrderBookWidget`); the XAML carries no color literals anymore: the favorite stars consume the new `Highlight` theme key, the three error texts the new `Error` key, the chat timestamps the existing `TextSecondary`; both keys ship in every theme file plus the sample theme and the theme-key doc lists follow.
- **Status:** done. Canon: the branch carried `9d5610e` (the refactor) + `a2f3c64` (the version cut) and landed through the merges `1c3b955` + `b58328f`; the build shipped as `0.1.0-alpha.21` (see CHANGELOG.md).

## T-045 — Workflow bookkeeping codified: develop-based branches, TASKS.md statuses, app version bump (supervisor-direct)

- **Branch:** feat/skill-rules (supervisor-direct task, not board-originated)
- **What:** `AGENTS.md` and the registered skill `.opencode/skills/zettatrade/SKILL.md` carry the three bookkeeping rules: feature branches base on `develop` (`scripts/new-worktree.ps1` defaults to `main`), a task's status moves are written into `docs/TASKS.md` while they happen (board row + section), and every app-changing task cuts its alpha version in `Directory.Build.props` + a `CHANGELOG.md` section; the gate is invoked with `-BaseBranch develop`; the stale T-039 row/section flipped to done (canon `9324fd9`, branch tip `3761bc0`, shipped as `0.1.0-alpha.20`).
- **Status:** done. Canon: commit `24d9789` is on develop. Docs-only: no version bump (rule 6).

## T-046 — DPAPI secret store wipes all stored secrets after a silent load failure (BUG-001, Critical)

- **Branch:** feat/dpapi-store-wipe (findings of the 2026-11-04 code review; full definitions and line references live in `docs/CODE_REVIEW.md`)
- **Origin:** the secret store is the last line of defense for user keys; a silent wipe destroys every stored secret on the first corrupt file - the exact scenario the trading-safety rule "never overwrite encrypted secrets when decryption fails" forbids.
- **What:** `BUG-001` - `WindowsDpapiSecretStore.LoadStoreAsync` logs and swallows every load failure (corrupt JSON, invalid base64, transient read error) and continues with an empty/partial `_cache`; the very next `SetAsync` -> `SaveStoreAsync` rewrites the whole file from that cache, permanently destroying all previously stored secrets. `InitializeAsync` never surfaces the failure.
- **Scope:** `src/ZettaTrade.Infrastructure/Secrets/WindowsDpapiSecretStore.cs` (a failure-surfacing path out of `InitializeAsync`; if an interface change is needed, escalate).
- **Acceptance:** a corrupt/truncated/unreadable store file fails loudly at initialization and is never overwritten by a subsequent `SetAsync`; secrets become unusable-not-lost (an explicit error surfaced, a logged condition); the happy path is unchanged.
- **Tests:** the load-failure matrix (missing / corrupt JSON / bad base64 / locked file) with the no-wipe invariant pinned (after a failed load, a `SetAsync` attempt leaves the file byte-identical and reports the error).
- **Status:** done. Canon: commit `13c8b86` is on feat/dpapi-store-wipe; the load-failure matrix (corrupt/truncated JSON, invalid base64, locked file) + the happy-path roundtrip pass and the Initialize failure surfaces loudly (the store then refuses all operations; the on-disk secrets are never overwritten). The optional explicit `storePath` constructor override is a test seam (the interface is unchanged). Wave note: the release version bump + CHANGELOG land once for the whole fixed-wave in a separate release branch after the merges — a supervisor-approved deviation from rule 6 for parallel work.


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


## T-063 — The closed-task archive: sections leave the board, the archive rule and the reply language codified (supervisor-direct)

- **Branch:** feat/task-archive (supervisor-direct task, not board-originated; stacked on `feat/bug-board@8cce8a3`, merged first).
- **What:** the closed-task sections (T-001...T-037, T-039, T-042, T-043) moved verbatim from `docs/TASKS.md` into the new `docs/TASKS-ARCHIVE.md` (canonical T order; the queued sections T-038/T-040/T-041 stayed on the board); the board header names the archive path; rule 5 of `AGENTS.md` + the registered skill `.opencode/skills/zettatrade/SKILL.md` (bodies kept byte-identical) gained the archive-once-done-and-merged step, and the new rule 9 pins the working language: reason in English, answer in Russian; everything shipped in the repo stays English.
- **Status:** done. Canon: the branch carried `f07dd17` (the archive split + the two rules) + `55a243f` (three queued sections restored to the board) and landed as a fast-forward on develop. Docs-only: no version bump (rule 6). Worktree `zt-task-archive` and the branch were removed after the merge (rule 1, see T-064).

## T-064 — Worktree lifecycle: remove the task folder and branch after merge (supervisor-direct)

- **Branch:** feat/worktree-cleanup (supervisor-direct task, not board-originated; based on develop after the T-063 merge).
- **Origin:** rule 1 spelled out only the worktree creation; the merged tasks' folders lingered on the disk (`zt-ui-colors`, `zt-skill-rules`, `zt-bug-board`, `zt-task-archive`) with their branches live.
- **What:** rule 1 of `AGENTS.md` (+ the registered skill, identical bodies) and `.opencode/agents/zt-supervisor.md` now say it: once the task is done and merged, remove its worktree folder (`git worktree remove <folder>` — it refuses on uncommitted files, resolve those first) and delete its branch (`git branch -d <name>`); no stale task folders linger around the repo. The first sweep ran alongside this rule: the four merged worktrees above were removed (each was clean and fully merged) and the branches `feat/ui-colors`, `feat/skill-rules`, `feat/bug-board`, `feat/task-archive` were deleted.
- **Status:** done. Canon: this branch's single docs commit (see git log). Docs-only: no version bump (rule 6).


## T-065 — Localization + themes stand alone: a lean ZettaTrade.Appearance library (supervisor idea)

- **Branch:** feat/appearance (queued 2026-11-05, supervisor idea; developed 2026-11-05).
- **Origin:** `ZettaTrade.UI` bundles "shared UI components, themes, and localization infrastructure" together with the whole charting/widget world, and its project references (`ZettaTrade.Core`, `ZettaTrade.Infrastructure`) drag domain code into every consumer of a themed `TextBlock`. The supervisor wants the appearance layer (localization + the theme engine) reusable on its own — even as a local NuGet package later.
- **What:** a new lean assembly `ZettaTrade.Appearance`: `Localization\LocalizationManager` + `LocalizedExtension`, `Resources\Strings(.Designer/.ru)` (namespace rehomed to `ZettaTrade.Appearance.Resources`), `Themes\ThemeManager` + `ThemeFileWatcher` + `PreviewColorsToBrushesConverter`, and the shared UI-thread marshaller `ObserveOnUiThread` (its only member's file moves from `UI.Extensions`). The composition root passes the protected file paths: the user themes directory is a REQUIRED constructor argument (the lib must not reach into `Infrastructure.Configuration` for `ConfigPathResolver`), built-ins keep the exe-adjacent `themes\` default; `ZettaTrade.UI` keeps charting/primitives and references the new lib (its direct `ZettaTrade.Charting` reference replaces the dropped transitive one); `App` consumes both; XAML `xmlns:loc`/`xmlns:themes` and every C# `using` rehomed. Packaging decision recorded in the section (NOT yet implemented): the library ships as a solution project reference; a local NuGet pack is deferred until a second consumer exists — in-repo self-packaging adds a build step and version drift with no reuse yet. The `ObserveOnUiThread` pointers in the convention docs follow the move (`ZettaTrade.Appearance.Extensions`).
- **Scope:** new `src\ZettaTrade.Appearance` (csproj + moved sources), `slnx` registration, `ZettaTrade.UI.csproj`/`App.csproj` reference graph, `CompositionRoot.cs` (paths + namespaces), the six XAML windows' `xmlns` lines, the touch-point C# files (charting widgets, `CandlestickChart`, `ChartTickCulture`, `OrderBookWidget`, `OrderBookPageLayout`, `SettingsViewModel`, `StatusBarViewModel`/`MainViewModel`/`OrderBookViewModel`/`ChartViewModel`/`MainWindow.axaml.cs`, `CompositionRoot`), test namespaces (`UI.Tests`Themes/Localization/Charting, `App.Tests` Settings), `AGENTS.md` layout line + the `ObserveOnUiThread` pointer, version bump + CHANGELOG.
- **Acceptance:** runtime behavior byte-for-byte unchanged (theme hot-reload, live language switch, settings persistence); a fresh clone build + tests stay green; `GATE PASSED` (`-AllowProtected`: slnx + AGENTS).
- **Tests:** the existing theme/localization/resx-sync suites ride along (namespace-only deltas); a constructor-validation test pins the required user-themes directory; no new domain logic.
- **Status:** done. Canon: the branch carries `8a7c164` (the extraction) + this release docs commit with the status flip; shipped as `0.1.0-alpha.28` (see CHANGELOG.md).

## T-066 — Window chrome + control restyling: styled title bar, buttons, inputs across the shell (supervisor idea)

- **Branch:** queued 2026-11-05 (supervisor idea).
- **Origin:** today every window rides the default Fluent chrome: native title bars, stock-styled buttons/inputs, nothing accent-keyed — the visual layer the supervisor asks to own.
- **What:** a custom window chrome driven by the theme (a styled title bar over `ExtendClientAreaToDecorationsHint`: localized app title, working drag/maximize/restore/minimize/close, snap-friendly), plus a shared control style set (buttons, text inputs, combos, tabs, menu) living in the Appearance library as Avalonia styles + resource dictionaries; all chrome/control colors consume the theme JSON keys (new keys ship in every theme file + the sample + the theme-key doc lists — the T-044 discipline); no hardcoded color literals anywhere.
- **Scope:** `ZettaTrade.Appearance` (styles/dictionaries mount from the theme palette), App windows (`MainWindow` + the dialog windows), resx en/ru keys for chrome labels, theme JSON + docs.
- **Acceptance:** dragging/max/min/close behave on all five windows; theme and language switches retint the chrome LIVE; edge cases pinned (double-click title = maximize, Aero snap still works, RTL not required); visuals agree on Windows/macOS/Linux.
- **Tests:** visual tests for chrome states where feasible (Avalonia headless), a regression assert that every window carries the chrome style and that the theme keys exist in every shipped theme; full matrix green.

## T-067 — Indicator plugin settings render only for plugins that are actually loaded (supervisor idea)

- **Branch:** queued 2026-11-05 (supervisor idea).
- **Origin:** the Indicators tab hardcodes the SMA enable/period row: with the plugin-first architecture the surface must reflect the actual scanned catalog, not a shipped assumption — a missing (or skipped-by-scan) plugin assembly would still render a dead row.
- **What:** the settings row appears ONLY when the scanned catalog contains the plugin (keyed by indicator name in the same catalog record); with zero plugins loaded the tab shows an honest empty state (localized hint: no plugins found next to the exe / scan skips logged); when loaded, the row behaves exactly as today (enable + period persist into `ChartIndicators` JSON). The chart pipeline keys plugin indicators by name — keep the association stable.
- **Scope:** `SettingsViewModel` (availability flag driven by the scanned `IIndicatorCatalog`/`PluginIndicatorCatalog` state), `SettingsWindow.axaml` (conditional row + empty state), resx en/ru keys, existing `ChartIndicatorConfig` persistence.
- **Acceptance:** same-state tests pass with the SMA plugin present (row visible, period + enable persisted as today); with the plugin assembly removed from the plugins folder the row vanishes and the hint shows; the settings save path never fabricates a missing plugin's config.
- **Tests:** Settings VM tests with populated vs empty catalog; App tests pinned to the UI conditional.

## T-068 — MCP client settings: external servers with transport, address/port and task-level options (supervisor idea)

- **Branch:** queued 2026-11-05 (supervisor idea).
- **Origin:** the Mcp tab today drives only the LOCAL server side (the trading-tools host: `ServerEnabled`). The MCP CLIENT surface (`IMcpClient`/`McpClientService` supports stdio AND Streamable HTTP transports) is code-only: nobody can attach an external MCP server without editing config files by hand; the supervisor asks for address/port (and other useful parameters) in settings.
- **What:** the Mcp tab gains an external-servers block: per-server entry editing (name, transport Stdio/StreamableHttp, command+args for stdio, address+port for HTTP, enabled flag, optional auth secret-ref key resolving through `ISecretStore` — never a literal secret), a connect/disconnect state row per entry, and the task-level options the transport already exposes (HTTP timeout; bounded stderr drain cannot regress). Config persists under a new settings section; startup connects enabled entries the same `McpClientService.ConnectAsync` path; validation fails loudly (pinned error state + log), the local-server block stays byte-compatible.
- **Scope:** `McpServerConfig` shape (additive fields only), `McpClientService` (config consumption, endpoint validation, auth-ref resolution), `SettingsViewModel` + `SettingsWindow.axaml` (the editor block + status rows), resx en/ru, secrets integration, tests.
- **Acceptance:** e2e loopback HTTP MCP stub (the T-032 harness pattern): a configured address/port connects and lists tools; a stub outage shows the pinned error state, never a hang; stdio entries launch their configured command; a disabled entry never dials; secrets never appear in logs or config.
- **Tests:** the settings round-trip (add/edit/disable entries), the HTTP loopback matrix (happy, 500, timeout, malformed), the stdio launch + stderr drain cases, auth-ref resolution from the fake store.
