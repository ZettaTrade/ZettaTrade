# ZettaTrade — Task Board

Statuses: `planned` → `in-progress` → `review` → `done`. One task per feature. Agents work only in their own worktree/branch, within the listed scope. Branches are created with `pwsh scripts/new-worktree.ps1 -Name <name>`.

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
| T-014 Instrument autocomplete in the order dialog | feat/symbol-autocomplete | planned | — |
| T-015 Configurable exchange endpoints + registry seam | feat/exchange-config | planned | — |
| T-016 Exchange selector in the toolbar | feat/exchange-selector | planned | needs a 2nd adapter |
| T-017 One symbol context for the workspace (chart+book), fill the activation gap | feat/workspace-symbols | done | — |
| T-018 App version display in the status bar | feat/status-version | done | — |

---

## T-001 — Load themes from JSON files (owner: supervisor)

- **Branch:** develop
- **Scope:** `src/ZettaTrade.UI/Themes`, `src/ZettaTrade.App/ZettaTrade.App.csproj`, `src/ZettaTrade.App/Views/MainWindow.axaml`, `themes/*.json`, `tests/ZettaTrade.UI.Tests`.
- **What:** Built-in themes shipped as `themes/*.json` next to the exe; user files from the config themes dir override built-ins by name; `baseTheme` field drives the Avalonia variant; Ctrl+, bound for real. Done in commit `5691681` (`feat(ui): load themes from JSON files instead of hardcoded dictionaries`).
- **Status:** done. Known follow-ups are T-002…T-006.

## T-002 — Wire advertised menu hotkeys

- **Branch:** `feat/hotkeys` (worktree `Q:\Projects\__My\zt-hotkeys`, based on develop@5691681)
- **Scope:** `src/ZettaTrade.App/Views/*.axaml` (KeyBindings only), `tests/ZettaTrade.App.Tests`. Do not touch Core or ThemeManager.
- **Outcome (in review):** Menu gesture audit found that `NewOrderCommand`, `OpenAiChatCommand`, `ShowPositionsCommand`, `ShowOrderHistoryCommand`, `ManageMcpServersCommand` are `Task.CompletedTask` placeholders (dead items even on click); only `OpenSettingsCommand` (via Interaction) and `ExitCommand` (shutdown) are real. So: `Ctrl+,` was already bound for real; `Alt+F4` is the OS close gesture (kept as-is, no binding — double handling); gestures for placeholder commands (`Ctrl+N`, `Ctrl+Shift+A`) were removed with an explanatory XAML comment. Acceptance criteria adjusted accordingly.
- **Also in the branch (user-approved scope add, separate chore commit):** `.gitattributes` now pins `* text=auto eol=lf` (+ explicit binary for ico/png/jpg). Fresh worktrees/clones with the global `core.autocrlf=true` were turning every file into CRLF and failing the format gate; the checked-out files were converted to LF once in this worktree.
- **Acceptance (achieved):**
  - Every remaining advertised gesture is a working hotkey or the OS gesture (`Ctrl+,` opens settings; `Alt+F4` closes the window).
  - Runtime check: Ctrl+N and Ctrl+Shift+A open nothing (placeholders, no longer advertised); the settings window still opens via Ctrl+, on a fresh checkout.
  - No new command properties invented; `gate.ps1` → GATE PASSED (40 tests green).
- **Known caveat:** the `Alt+F4` label is Windows-oriented (macOS exits via Cmd+Q); platform-specific gesture labels are future polish, not blocking.

## Proposed (not scheduled)

- **feat/hotkeys follow-up:** wire `NewOrderCommand`/`OpenAiChatCommand` etc. to real views (New Order dialog with IRiskManager validation; AI chat already has a ChatViewModel and a side panel in the main window — the command probably should focus/open it instead of a separate window). Gestures (`Ctrl+N`, `Ctrl+Shift+A`) come back with those tasks.


## T-003 — Hot-reload of theme files

- **Branch:** `feat/theme-reload` (worktree `Q:\Projects\__My\zt-theme-reload`, based on develop with the merged T-002)
- **Scope:** `src/ZettaTrade.UI/Themes`, `tests/ZettaTrade.UI.Tests`. No App/Core/UI-view changes.
- **Outcome (in review):** New `ThemeFileWatcher` (one `FileSystemWatcher` per directory, debounced 150 ms via Rx `Throttle`; renames publish the new path, deletions are never published). `ThemeManager` subscribes with `FromAsync`+`Concat` (serialized processing, one bad file cannot kill the pipeline); a hot change to the file of the **active** theme re-applies it live — on the UI thread when an Avalonia app exists, directly in unit tests. Constructor gained optional directory overrides (tests never touch real app/user folders). `InitializeAsync` starts watching after both loads; `Dispose` tears down subscription, watchers, subject and lock.
- **Acceptance (achieved):**
  - Runtime: a user file overriding the active built-in recolors the running app with **no restart** — chart flipped dark → magenta, status chip shows the override's DisplayName; broken JSON keeps colors + keeps the app alive, a later valid file is still picked up (logs: 2 hot loads, 3 "Theme applied", 0 failures).
  - Registration order stable on reload (replace in place verified); deletions keep the last loaded version.
  - Unit tests: modify/create/delete/invalid/dispose/order — 6 new tests (13 total in UI.Tests); watcher lifecycle clean (started in `InitializeAsync`, disposed with the manager).
  - `gate.ps1` → GATE PASSED (46 tests).
- **Note:** after the run's cleanup the app keeps the last loaded palette until the next theme (re)apply/restart when the override file is deleted — by design (deletion never removes a registration).

## T-004 — Settings: themes folder + file validation

- **Branch:** `feat/theme-settings-ui` (worktree `Q:\Projects\__My\zt-settings-ui`, stacked on `feat/theme-reload` — merge order T-003 → T-004, the warning clearing leans on the hot reload)
- **Scope:** Settings window views/ViewModels in `src/ZettaTrade.App`, read-only diagnostics surface on the concrete `ThemeManager` (`IThemeManager` contract untouched), `IShellUtils`/`ShellUtils` in `src/ZettaTrade.Infrastructure/Shell` (platform code stays in Infrastructure), resx keys `Settings.*` for **both en and ru**.
- **Outcome (in review):**
  - `ThemeManager.LoadErrors` — per-file snapshot of load failures with technical reasons (`InvalidJson` from `JsonException`, `MissingName` for a parsed-but-nameless schema, `ReadFailed` for missing/locked files); `LoadErrorsChanged` is a `BehaviorSubject` snapshot stream (replays on subscribe), published on the UI thread when Avalonia runs (same dispatching contract as the hot-reload reapply, so ViewModels subscribe plainly). A successful reload of the same path clears exactly its entry.
  - `IShellUtils`/`ShellUtils`: opens a location with the OS file manager — explorer.exe / open / xdg-open via `ProcessStartInfo` (`UseShellExecute=true`, `ArgumentList`); unsupported platforms throw.
  - Settings window: an "Open themes folder" button (`ReactiveCommand` → `_shellUtils.OpenInFileManager(ConfigPathResolver.ThemesDirectory)`, errors logged), the schema-example hint pointing at `themes/trading-pro.json`, and a warning list that shows the **file names** of broken theme files (full path as tooltip, hidden while every file loads). CompositionRoot registers the concrete `ThemeManager` plus the `IThemeManager` alias (single instance, single init).
  - Behavioral contracts of `ThemeManager`/`IThemeManager` unchanged — the added surface is diagnostics-only reading, which is why this stacks on T-003 in `ThemeManager.cs`.
- **Acceptance (achieved):**
  - "Open themes folder" works on Windows/macOS/Linux via the Infrastructure abstraction; runtime-verified on Windows: Explorer opens at `%APPDATA%\ZettaTrade\config\themes` (window titled "themes").
  - A broken theme file appears in settings with its file name; **a fixed file clears the warning without an app restart** — the hot reload from T-003 reloads the file and the success path clears the entry (screenshot pair `shots\settings_errors_visible.png` → `settings_errors_cleared.png`; no separate reload button needed).
  - All new strings through the localization system, en/ru in sync (`Settings.ThemesFolder.Open`, `Settings.ThemeErrors.Header`, `Settings.ThemeErrors.Hint`).
  - Unit tests: `ThemeManagerLoadErrorsTests` (broken list + publish count, fixed-file clears only its entry, missing file → ReadFailed, replay on subscribe) and SettingsViewModelTests coverage for the command and the live warning state; `gate.ps1` → GATE PASSED (50 tests).

## T-005 — Canonical theme identity

- **Branch:** `fix/theme-identity` (worktree `Q:\Projects\__My\zt-theme-identity`, stacked on `feat/theme-settings-ui` @fc6df3b — merge order T-003 → T-004 → T-005; it edits the same `ThemeManager.cs` as T-003/T-004)
- **Scope:** `src/ZettaTrade.UI/Themes`, the one status-bar chip ViewModel in `src/ZettaTrade.App`, `tests/ZettaTrade.UI.Tests`, `tests/ZettaTrade.App.Tests`.
- **What:** `CurrentTheme` currently returns `DisplayName` ("Light") while settings store `Name` ("light"); lookups match both, so two themes with equal display names would collide ambiguously. Make `Name` the single identifier: manager state and snapshot keys use Names; UI chips render `DisplayName`.
- **Outcome (in review):**
  - `ThemeManager` now keeps the canonical Name in `_current`: `CurrentTheme` **and** the `ThemeChanged` stream carry Names (the `IThemeManager` contract already documented Name — the implementation diverged, Core untouched). `ApplyThemeAsync` matches **only** by Name (case-insensitive); an unknown value logs a warning and changes nothing. The redundant `_activeName` field was dropped — the hot-reload reapply rule (T-003) compares the loaded file's Name with `_current`.
  - App read boundary (`ApplyStoredThemeAsync`): the stored value is resolved to the canonical Name — exact Name match first, DisplayName fallback as the upgrade path from pre-T-005 builds ("Dark" → "dark", case-insensitive); an unknown value is logged and the startup theme stays the canonical `"dark"`. No silent settings rewrite — the value is only interpreted, then canonical Names are stored going forward.
  - Surfaces: the status-bar chip maps Name → DisplayName through `AvailableThemes` (`ResolveChipName`, raw name as the fallback before the manager's list is populated); `SettingsViewModel.IsThemeApplied` matches by Name. Chip and settings subscribes stay plain (same UI-thread dispatching contract as T-003 — no disposal changes).
  - Canonical defaults: `ThemeManager.DefaultThemeName` and `UiSettingsKeys.DefaultTheme` are now `"dark"`.
- **Acceptance (achieved):**
  - Runtime (Windows): stored legacy `{"Theme":"Dark"}` → log `Theme applied: dark` (pre-fix builds logged `Theme applied: Dark`), status chip text "Dark" resolved via DisplayName.
  - Legacy values resolve after upgrade (`"Dark"` verified at runtime, `"light"` same lookup path); rows in `AvailableThemes` stay unique by Name (replace-in-place registration from T-001, pinned by the duplicate-names tests).
  - Name-only matching pinned by tests: `ApplyThemeAsync_WithDisplayNameOnly_DoesNotApply`, `ApplyThemeAsync_WithDuplicateDisplayNames_MatchesByCanonicalName` (UI.Tests), `Constructor_WithDuplicateDisplayNames_ResolvesByCanonicalName` (App.Tests); unknown-name keep-current and published-Name assertions updated.
  - `gate.ps1` → GATE PASSED (56 tests: Core 13, Infra 11, App 13, UI 19).

## T-006 — Theme preview swatches in the picker

- **Branch:** `feat/theme-preview` (worktree `Q:\Projects\__My\zt-theme-preview`, based on develop @ b9b37fe — the stack T-003/T-004/T-005 is merged, so it starts on fresh files)
- **Scope:** settings window view only in `src/ZettaTrade.App`, small shared converter in `src/ZettaTrade.UI/Themes`. No ViewModel changes.
- **What:** Each entry in the theme combo shows the motif from its `previewColors` (a strip of 3–4 colored squares) — the data is already in every JSON but shown nowhere.
- **Outcome (in review):**
  - `PreviewColorsToBrushesConverter` (`src/ZettaTrade.UI/Themes`, XAML `x:Static` singleton): converts the hex list to `ISolidColorBrush`es in one call; unparseable entries are skipped entirely, an empty or missing `previewColors` yields an empty sequence — so the ItemsControl renders nothing and there is no per-row layout glitch. Follows the existing `Color.Parse` defensive convention.
  - `SettingsWindow.axaml`: the theme combo item template is a `*,Auto` grid — `DisplayName` left, a 10×10 rounded strip right-aligned. The same template drives the closed selection box, so the preview is visible without opening the list. No margins/spacing on the strip column: zero swatches ⇒ zero width.
  - No ViewModel, contract, or resx changes; `ZettaTrade.Core` untouched (`ThemeInfo.PreviewColors` was already there).
- **Acceptance (achieved):**
  - Runtime (Windows): dropdown shows strips for Dark/Light/Midnight Blue/OLED Black (`shots\theme_preview_dropped.png`); a user theme without `previewColors` ("No Swatch") shows a clean row without any strip; the closed selection box shows the strip next to "Dark" (`theme_preview_closed.png`).
  - Fallback pinned by unit tests (`PreviewColorsToBrushesConverterTests`: valid hex → matching brushes incl. alpha, malformed entries skipped, empty list and null → no swatch) — UI.Tests 23 (+4).
  - No localization keys needed. `gate.ps1` → GATE PASSED (59 tests total: Core 13, Infra 11, App 13, UI 23).

## T-007 — resx en/ru sync gate

- **Branch:** `chore/resx-gate` (worktree `Q:\Projects\__My\zt-resx-gate`, based on develop @ aa73fc6)
- **Scope:** new `scripts/check-resx.ps1` (or an addition to `scripts/gate.ps1`); optional xUnit test pinning the invariant.
- **What:** The rules require every key to exist in both `.resx` cultures. Today a missing ru key is caught only by eye at runtime.
- **Outcome (in review):**
  - `scripts/check-resx.ps1` — discovers every `*.resx` base file under `src`, requires the `.ru.resx` partner, compares the key sets **case-sensitively in both directions** and verifies the module-prefix rule (known prefixes listed at the top of the script: `Menu.`, `Settings.`, `Order.`, `Status.`, `AI.`, `Chart.`, `Trading.` — extend there when a module ships its first strings, `Exchange.*` next). Designer-only entries (`$this.*`, `>>Form`) are excluded. Failures list file + key + direction, exit 1.
  - Gate wiring: `Step 'resx'` runs the script **with the same interpreter the gate was started with** (`pwsh` or Windows PowerShell 5.1 — no assumptions about pwsh being installed), after `build` and before `test`, so a diverged resx fails before the slower test step.
  - Deliberately caught and fixed two PowerShell case-insensitivity traps in the check itself: `Compare-Object`/`Sort-Object -Unique`/`-cnotcontains` default to case-blind matching, which would let `settings.Title` vs `Settings.Title` pass silently. All comparisons are now explicit `-CaseSensitive` / `-cnotcontains`.
  - No C# changes; the optional xUnit pin was skipped to avoid duplicating the file-discovery logic in C# (the gate covers it; DRY/YAGNI).
- **Acceptance (achieved):**
  - Negative runs verified: a removed ru key → `key 'Settings.Title' is missing in the ru file`, exit 1; a prefix violation (`settings.Title`) → both sync problems **and** `key 'settings.Title' has no module prefix (known: …)`; everything restored via `git checkout` afterwards.
  - Positive run: `Strings: 35 en keys, 35 ru keys`, exit 0; full check costs ~0.2 s (well under the 2 s budget).
  - Gate documents the module-prefix rule in the script header; `gate.ps1` → GATE PASSED (35/35 resx in sync + 59 tests).

## T-008 — New Order dialog

- **Branch:** `feat/order-dialog` (worktree `Q:\Projects\__My\zt-order-dialog`, based on develop @ 8f365ec)
- **Scope:** New Order window/ViewModel in `src/ZettaTrade.App`, `MenuViewModel` wiring (`NewOrderCommand` is a placeholder since the T-002 audit), returning the `Ctrl+N` gesture in `MainWindow.axaml`, resx keys `NewOrder.*` en+ru, tests in `tests/ZettaTrade.App.Tests`. Core contracts untouched — the trading-safety path (`IRiskManager` → `IConfirmationService` → `ITradingAdapter`) already exists.
- **What:** The first real trader action on the paper adapter: dialog collects symbol (typed, resolved against `GetSymbolsAsync`), side, type (Market/Limit), quantity and limit price; an `OrderRequest` is built and must pass `IRiskManager` before any confirmation; rejection shows the risk error and never opens the confirmation dialog; the confirmation dialog then gates `PlaceOrderAsync`; cancel on either step places nothing.
- **Acceptance:**
  - Runtime: a valid paper order is placed; a risk-rejected order shows the validation error and the confirmation dialog is not shown; "Cancel" on confirmation places nothing.
  - Trading safety rules kept: no order bypasses `IRiskManager`; high-risk orders go through `IConfirmationService` (already the default flow); invalid/unparsable inputs surface as errors, never defaults.
  - `Ctrl+N` gesture advertised and working again (adb removed in T-002 because the item was dead).
  - All strings via the localization system, `NewOrder.*` prefix registered in the resx gate; `gate.ps1` passes.
- **Status:** done. Merged into develop as `495d314`. Outcome:
  - The full trading-safety pipeline is wired: parse (errors, never defaults) → `OrderRequest.Validate` → `DefaultRiskManager` (new 100k USD hard cap produces the reachable rejection path; >10k requires confirmation) → `UiConfirmationService` (same `Trading.ConfirmPlaceOrderFormat` summary MCP tools use) → `PaperTradingAdapter.PlaceOrderAsync` → adapter throws surface as localized errors, the dialog stays re-runnable.
  - Runtime verified (UIA probe, screenshots + app log): Ctrl+N opens the dialog; a 150k USD limit order shows "Rejected by risk controls: …hard cap…" and no confirmation window; a 15k order shows the confirmation and Cancel places nothing (log: "user cancelled the confirmation; nothing placed"); a 0.01 BTC @ 50000 limit order fills on the paper adapter and closes the dialog ("Paper fill: Buy 0.01 BTC/USDT at 50000 (Limit)", "placed paper-…").
  - Tests: 8 ViewModel pipeline tests + 5 risk-gate tests (hard cap, threshold boundaries, market orders never escape with a zero-value estimate) — App.Tests 13 → 26; whole suite 73, GATE PASSED (resx 49/49 en+ru, format clean).
  - Avalonia 12 notes: `TextBox.PlaceholderText` (Watermark is obsolete); combo popup items are reachable under the owning window in UIA but reject `SelectionItemPattern` — probes (and later ordered-driven tests) select by click on the item rectangle; shell-injected keys must use keybd_event+WM_KEYDOWN, `SendKeys`/SendInput is not seen by Avalonia windows.

## T-009 — Positions & open orders panel

- **Branch:** `feat/positions`
- **Scope:** positions/orders panel (side panel or window) in `src/ZettaTrade.App`, `ShowPositionsCommand` wiring, resx `Positions.*` en+ru. Core contracts and adapters untouched (supervisor decision Option A).
- **What:** `GetPositionsAsync`/`GetOpenOrdersAsync` of the trading adapter rendered in a view; per-order Cancel that always passes the symbol (mandatory `cancel_order` rule) through `IConfirmationService`.
- **Acceptance (amended per Option A):**
  - Runtime on the paper adapter: placing an order in T-008's dialog shows its effects in the panel — positions reflect the fill; open orders stay empty on paper (the paper adapter fills everything instantly, so Pending never exists there) and the order appears in the "session journal" section via the new App-layer `IOrderJournal` (live adapters still feed open orders at runtime).
  - Cancel is fully wired (IConfirmationService → `CancelOrderAsync(order.Symbol, order.Id)`) and enabled for Pending/Submitted/PartiallyFilled rows; proven by unit tests, including symbol-mismatch-free argument passing. The paper adapter itself never needs cancel at runtime (F-filled instantly) and stays untouched.
  - Cancel without a symbol is impossible by construction (compiler/type safety, unit-tested).
  - Strings via resx; gate passes.
- **Status:** done. Merged into develop as `eb72401` (feature commit `32f3986`). Outcome:
  - Modal panel (Trading → Positions) with three sections: positions from `GetPositionsAsync`; open orders from `GetOpenOrdersAsync`; session-journal orders from the new App-layer `IOrderJournal` (thread-safe capacity-50 ring buffer, newest first) recorded by the T-008 dialog after each placement. Rows carry precomputed localized strings, empty states via `Has*` flags.
  - Cancel pipeline: shared `OrderConfirmationComposer` (same summary shape as the MCP tools; `Positions.ConfirmCancelFormat`) → `IConfirmationService` → `CancelOrderAsync(order.Symbol, order.Id)` — symbol-first by construction; the per-row command is enabled only for Pending/Submitted/PartiallyFilled.
  - Runtime verified (UIA probe): a 0.01 BTC @ 50000 limit order fills instantly on paper; the panel shows the position (Buy / 0.01 / 50000.00000000 / Filled), the empty open-orders state, and the journal row with a disabled Cancel; app log has "Positions panel: loaded 1 positions, 0 open orders, 1 recent orders"; Close closes the window.
  - Tests: OrderJournal (3), OrderConfirmationComposer (5), panel VM (13, incl. the cancellable-status theory), dialog journal hooks (2), menu routing (1) — App.Tests 26 → 51; whole suite 98; GATE PASSED (resx 75/75 en+ru, format clean).
  - Core contracts and `PaperTradingAdapter` untouched (Option A: paper fills everything instantly, so no runtime Pending/cancel path exists there — cancel stays unit-proven).

## T-010 — Order history + persistence (EF Core)

- **Branch:** `feat/order-history`
- **Scope:** SQLite persistence in `src/ZettaTrade.Infrastructure` (`DbContext`, migrations, platform file location via `ConfigPathResolver`), history view in `App`, resx `History.*` en+ru.
- **What:** Placed/cancelled orders persist to SQLite journal; the history view reads the DB; cold start re-loads past activity.
- **Status:** done. Canon: the feature commit is on develop as `464835a`. Outcome:
  - Infrastructure `Persistence/` module: `IOrderHistoryStore` (Infrastructure-owned, like `IShellUtils`), `SqliteOrderHistoryStore` (IAsyncInitializable: ensure data dir + `MigrateAsync`, per-operation contexts over singleton options), `OrderHistoryEntry`, `OrderHistoryDbContext` (enums/decimals/timestamps as invariant TEXT), design-time factory for `dotnet ef`, `AddOrderHistory()`, migration `20261101071749_Initial`.
  - App: `ConfigPathResolver.OrderHistoryDatabaseFile`; the dialog records placements, the panel marks cancellations (both best-effort: a store failure is logged, never fails an already-accepted order/cancel); modal 920×520 window through the interaction pattern (MenuViewModel `ShowOrderHistoryRequested` → MainWindow handler → transient `OrderHistoryViewModel`, one DB read per open); `TimeFormats.OrderTimestamp` shared with the positions panel — journal stores UTC, tables show the local wall clock; `HistoryUiDependencyInjection()` + two one-liners in CompositionRoot.
  - resx `History.*` 12 keys en+ru (87/87), `History.` prefix registered in `check-resx.ps1`.
  - **Acceptance (achieved):** restart story proven end-to-end — a 0.01 BTC @ 50000 limit order placed via the dialog on the paper adapter → graceful close → restart → the Trading > Order History window shows the row (Opened, paper, BTC/USDT, 0.01, 50000.00000000, Filled); both runs log "Order history store ready at … (migrations applied)", the second run logs "Order history window: loaded 1 orders from the journal". No secrets in the DB by construction: one `Orders` table keyed by (ExchangeId, OrderId), a 17-field white-list pinned by a `PRAGMA table_info` test.
  - Two root-caused stepping stones: the CompositionRoot init walk initializes **only factory-lambda registrations** (type-based registrations resolve lazily — the first attempt produced a 0-byte DB), so the store registers via a factory delegate; and `IDesignTimeDbContextFactory<>` lives in namespace `Microsoft.EntityFrameworkCore.Design` inside the core `Microsoft.EntityFrameworkCore.dll` (the Design package therefore needs compile assets, `PrivateAssets=all` kept). EF 10.0.14 forced `Microsoft.Extensions.*` pins 10.0.13 → 10.0.14 in four csproj (NU1605).
  - Tests 98 → 115: 8 store tests (roundtrip, upsert, newest-first, cancel marking, unrecorded cancel ignored, cross-store restart, schema white-list; `Pooling=false` lesson for temp DB deletes), dialog/panel persistence hooks + resilience, menu routing, 4 history VM tests. GATE PASSED (build, resx 87/87, format).
- **Next:** T-011 (order book widget) — cut from develop **after** the versioning chore lands (see `CHANGELOG.md`: alpha.11 is T-011's release).

## T-011 — Order book widget

- **Branch:** `feat/order-book` (worktree `zt-order-book`, cut from develop @ e7a7d56)
- **Scope:** depth rendering in `src/ZettaTrade.UI/Charting` (Skia widget, CandlestickChart pattern), `src/ZettaTrade.App` (self-fed `OrderBookViewModel` + wiring), `src/ZettaTrade.Infrastructure/Exchange` (lazy stream start on first subscriber), resx `OrderBook.*` en+ru + `check-resx.ps1` prefix. Tests across Infrastructure/App/UI test projects. Core contracts untouched.
- **What:** Depth view with bids/asks, live updates.
- **Pre-existing runtime facts discovered at start (2026-11-01 probe):**
  - `WhenActivated` never fires for VMs bound as nested-panel DataContexts: the main workspace runs with `ChartViewModel` dead (no symbols, no bars, "Нет данных"). Workspace VMs therefore use ctor subscriptions (the documented `StatusBarViewModel` precedent) and the widget subscribes on view attach; the chart's own gap is queued separately as T-017, not patched in scope here.
  - Nobody started the live subsystem: `ConnectAsync`/`SubscribeSymbolAsync` had no callers — market-data streams were unreachable (`SubscribeOrderBook` returned a never-filled subject). T-011 adds demand-driven stream start.
- **Design:**
  - `ExchangeAdapterBase`: stream-subject seams get a virtual `StartSymbolStreamsAsync(Symbol, CancellationToken)` starter (default no-op); the first subscription triggers the start, an in-flight start is cancelled with the subscription; repeated outer subscriptions re-run the idempotent start (documented). `BinanceAdapter` implements the starter: connect once (guard), then SUBSCRIBE the symbol's ticker/book/trade streams, serialized via `SemaphoreSlim` (no `lock` in async code).
  - App: `OrderBookViewModel` holds a typed `SymbolInput` (default BTC/USDT, parsed like the dialog), publishes an `Updates` stream switched on the parsed symbol; a parse failure keeps the previous symbol (logged, never silently defaulted).
  - UI: `OrderBookWidget` draws a per-frame snapshot (bids/asks halves, cumulative volume bars, header from the book's own symbol); depth beyond fitted rows is not drawn, so per-frame cost is bounded by control size, not by 1000+ levels; theme colors reused (`CandleUp`/`CandleDown`, background/grid/text), localized no-data/state strings.
- **Acceptance:**
  - Performance target measured by tests: the layout+snapshot path for 1000 levels per side sustains 60 fps (frame budget < 16 ms, measured value printed), and the VM-side ingest keeps up with 60 Hz pushes of 1000-level snapshots without backlog.
  - Real-Binance runtime probe: the default BTC/USDT book streams live depth (screenshots ~2 s apart differ, depth updates), and an idle/unreachable adapter degrades to the localized no-data placeholder instead of freezing or crashing.
  - gate.ps1 passes; version lands as alpha.11 per the changelog convention.
- **Status:** done. Canon: the feature commit `d522b92` is on develop via merge `310e49f`. Outcome:
  - Shipped: `OrderBookWidget` (Skia `ICustomDrawOperation`, CandlestickChart pattern) + pure `OrderBookPageLayout` (bids hang below the mid, asks above; cumulative bars relative to the side's own deepest visible level; per-frame cost bounded by the visible row count) + app-lifetime `OrderBookViewModel` (typed `SymbolInput`, default BTC/USDT, ctor subscriptions per the documented singleton precedent); workspace split `*,300` with the book column; resx `OrderBook.*` 5 keys en+ru (92/92) + prefix in `check-resx.ps1`; 5 `OrderBook.*` header strings drawn with the theme keys `Background/Border/TextPrimary/CandleUp/CandleDown`.
  - Demand-driven stream start: `ExchangeAdapterBase` wraps the three feeds in `SubscribeWithStreamStart` (`Observable.FromAsync(start).Select(subject).Switch()`) with the documented token contract — the token covers the in-flight start only and is reclaimed once the start completes (adapters must pool longer-lived transports on their own lifetimes); `BinanceAdapter` starts connect+on-demand SUBSCRIBE under `SemaphoreSlim(1,1)`, in-flight start cancelled with the subscription, re-subscription re-runs the idempotent start, subjects stay shared per symbol.
  - **Three transport faults the live probe uncovered (all fixed in scope, the WS path had never run before T-011):**
    1. Rx reclaims the `Observable.FromAsync` token at start completion → `WebSocketClient.ConnectAsync` had linked its session CTS to the caller's token → the receive loop died ~10 ms after connect, silently (`Connected → Disconnected`, no reconnect warning). Now the open phase honours the caller's token (in-flight connect still aborts with the subscription) and the loop runs on an adapter-owned session token until `DisconnectAsync`/disposal.
    2. SUBSCRIBE/UNSUBSCRIBE payloads lacked the required `id` field — Binance silently ignored them (connected socket, zero frames, no errors). Added a per-adapter `Interlocked.Increment` request-id counter.
    3. The adapter connected via the raw `/ws` endpoint where frames are NOT wrapped, while `HandleWebSocketMessage` expects the combined `{"stream","data"}` envelope and skipped every frame. Switched to `wss://stream.binance.com:9443/stream` (combined framing) — the endpoint literals remain the T-015 target.
  - **Acceptance (achieved):** default BTC/USDT streams real Binance depth — per-frame avg 1.9 ms / worst 4.8 ms at 1000 levels per side (60-fps budget 16 ms, printed by the test); screenshot pair 2 s apart differs; the localized no-data placeholder was observed live before the first depth frame (app stays alive, no freeze); logs show `WebSocket connected to wss://stream.binance.com:9443/stream` from the first subscriber.
  - Tests 119 → 137: 5 `ExchangeAdapterBaseSubscriptionTests` (first-subscription start, idempotent re-run + in-flight cancellation, shared-subject flow, per-symbol isolation, in-flight token liveness), 5 `OrderBookViewModelTests` (default-symbol subscribe, publish flow, Switch cancels the old stream, unparsable input keeps the stream, stream failure keeps the pipeline for later symbols), 8 `OrderBookPageLayoutTests` (60 fps budget, mid-line geometry, cumulative growth, row trimming, empty/degenerate sides, spread/header). GATE PASSED (build, resx 92/92, format); `Directory.Build.props` → `alpha.11` + CHANGELOG entry as the branch tail.
  - Known follow-ups: no e2e WS-server test for the transport (validated by the runtime probe instead); `BinanceAdapter` endpoint literals remain hardcoded (T-015); the dead `ChartViewModel`/`WhenActivated` gap and the duplicate symbol defaults stay queued as T-017.

## T-012 — UX follow-ups: chat focus, reconnect states

- **Branch:** `feat/chat-focus`
- **Scope:** `MenuViewModel`/`MainViewModel` wiring in `src/ZettaTrade.App` only.
- **What:** `OpenAiChatCommand` focuses the existing assistant side panel (with `ChatViewModel` already present) instead of a dead action; `Ctrl+Shift+A` returns; connection state in the status bar reflects adapter events rather than stubs.
- **Design (decided, 2026-11-01):**
  - Chat focus via the established menu-plumbing pattern: `MenuViewModel` gains an `OpenAiChatRequested` interaction (like `NewOrderRequested`/`ShowPositionsRequested`/…), `OpenAiChatCommand` routes through it, and the `MainWindow` code-behind handler (the window owns all menu interactions in `OnOpened`) focuses the chat input by name — the XAML input gains `x:Name="ChatInput"`. The hotkey comes back with it: a `KeyBinding Ctrl+Shift+A` plus the menu's `InputGesture` (the T-002 follow-up debt).
  - Connection chip without touching supervisor-owned Core contracts: `WebSocketClient.ConnectionStateStream` exists but is Infrastructure-internal, and `PaperTradingAdapter` wraps/hides the concrete adapter, so no clean App-visible stream exists. App-only choice: `MainViewModel` ctor-subscribes a poll — `Observable.Return(IsConnected)` + `Observable.Interval(2 s).Select(…)` (named constant), `DistinctUntilChanged`, `ObserveOnUiThread` → `StatusBar.UpdateConnectionStatus(bool)`. `MainViewModel` gains the `IMarketDataAdapter` ctor dep (CompositionRoot line). The chip clocks at most 2 s of staleness — a status indicator, not a safety signal.
  - The chip text gets localized while this VM is touched: the hardcoded English `"Connected"/"Disconnected"` literals (a rule violation) are replaced by stored-bool + derivation via the injected `ILocalizationManager` using the pre-existing `Status.Connected`/`Status.Disconnected` resx keys (present in BOTH en and ru), and `LanguageChanged` re-derives the text so a runtime language switch keeps the chip localized. `UpdateConnectionStatus(bool)` keeps its shape.
- **Acceptance:**
  - `Ctrl+Shift+A` focuses the chat panel at runtime; status chip mirrors adapter state; gate.
- **Tests:** menu routing (OpenAiChat command → interaction handled, per the ShowPositions test pattern); `StatusBarViewModel` localized derivation (fake `ILocalizationManager`, connected/language-change/disconnected transitions); the 2 s Rx glue is validated by the runtime probe, not unit tests (no scheduler injection noise).
- **Status:** done. Merged into develop as `2f53304` (fast-forward). Outcome:
  - Shipped per the design: `MenuViewModel.OpenAiChatRequested` + routed `HandleOpenAiChatAsync` (the dead `Task.CompletedTask` is gone); `MainWindow` registers `FocusChatAsync` in `OnOpened` and focuses the named `ChatInput`; `KeyBinding Gesture="Ctrl+Shift+A"` on the window + menu `InputGesture` (the T-002 follow-up debt is closed). `StatusBarViewModel` stores `_lastConnectionState` (bool?, null before the first report) and derives the chip text via the injected `ILocalizationManager` from the pre-existing `Status.Connected`/`Status.Disconnected` keys — the hardcoded English literals are gone; `LanguageChanged` re-derives so a runtime switch keeps the chip localized. `MainViewModel` ctor-subscribes the 2 s poll (`Observable.Return` + `Interval` → `DistinctUntilChanged` → `ObserveOnUiThread` → `UpdateConnectionStatus`); `CompositionRoot` gained the `MainViewModel` full-resolve with the `IMarketDataAdapter` dep. Core contracts untouched.
  - Tests 148 → 152: +1 `MenuViewModelTests` (OpenAiChat routing), +3 `StatusBarViewModelTests` (localized state per value, language-switch re-derive, empty before the first report). GATE PASSED (13 Core + 80 App + 28 Infra + 31 UI = 152).
  - Probe (`t012-live-probe.ps1`, UIA on the real exe): chip timeline t0 `Готов | Отключено | Dark | ru | 0.1.0-alpha.12` → t1 `Подключено` — the disconnected→connected transition is caught inside the 2 s poll and stays localized RU; the status-strip screenshot shows the shipped strip. Menu route: UIA Invoke on «ИИ Чат» → focus + typed `!` lands in the chat input (`keys=True values=[!]`). Hotkey route: `Ctrl+Shift+A` then `?` → `keys=True values=[!?]` — both the binding and the focus proven by the round-tripped character.
  - Probe lessons: (1) when testing `Ctrl+Shift+A` after typing into the input, blur via the window-root `SetFocus()` — a ComboBox blur enables Avalonia type-ahead and swallows the letter key, which made the first hotkey run look like an app failure (it never was); (2) .ps1 probe scripts with Cyrillic literals need UTF-8 + BOM for PS 5.1 (without it the parser dies mid-regex).

## T-013 — Order dialog fixes: market-order JSON strings + combo clipping

- **Branch:** `fix/order-dialog` (worktree `zt-dialog-fix`, cut from develop @ 1fc27a5; `scripts/new-worktree.ps1` only creates `feat/` branches, so the branch came from `git worktree add -b fix/order-dialog` directly)
- **Scope:** `src/ZettaTrade.Infrastructure/Exchange` (Binance payload parsing), `src/ZettaTrade.App/Views/NewOrderDialogWindow.axaml` (widths), `tests/ZettaTrade.Infrastructure.Tests`. VM/App pipeline untouched. User-reported.
- **What:**
  - Placing a **market** order fails with "Ордер не размещён: The requested operation requires an element of type 'Number', but the target element has type 'String'": the paper adapter resolves the fill price from the latest bar, and Binance klines (REST) return numeric kline fields as JSON strings while `GetBarsAsync` calls `JsonElement.GetDecimal()` directly (limit orders take the fill price from the order itself, which is why the T-008/T-010 probes never hit it).
  - The same crash pattern is latent in three more places of `BinanceAdapter`: WS ticker fields (`c`/`b`/`a`/`v`/`P`), depth price levels (`bids`/`asks` arrays) and trades (`p`/`q`) — all numeric values are strings on the wire; the WS handler currently swallows every one of these failures into "Error handling WebSocket message".
  - The type combo clips the RU "Рыночный" label: window is 380 px with `*,*,*` columns (≈109 px per combo).
- **Fix direction:** one tolerant number parser (JSON `String` → invariant parse, `Number` → `GetDecimal`) applied at all four sites; klines regression test with the real Binance payload shape; dialog window 380 → 440.
- **Status:** done. Merged into develop as `c5b650b`. Outcome:
  - New internal `BinanceJsonNumbers.ParseDecimal(JsonElement)` (`Exchange/BinanceJsonNumbers.cs`): `Number` → `GetDecimal()`, `String` → invariant `decimal.Parse`, any other kind → explicit throw (never a defaulted value). All four `BinanceAdapter` sites now go through it: klines OHLCV, WS ticker (`c`/`b`/`a`/`v`/`P`), depth `bids`/`asks` levels, trades (`p`/`q`) — the latent WS crashes died with the reported one.
  - Tests 115 → 119: `BinanceAdapterParsingTests` runs a real-shaped klines payload (an outer array of rows whose numbers are strings — first test attempt forgot the outer wrapper and taught the payload shape) through the public `GetBarsAsync` path, plus parser unit cases (string/number/exact decimals, `FormatException` for a non-numeric string, `InvalidOperationException` for a wrong JSON kind). Pinned via `InternalsVisibleTo` in `ZettaTrade.Infrastructure.csproj`.
  - Dialog window 380 → 440 px; type combo measured 109 → 144 px via a UIA probe.
  - GATE PASSED (119: Core 13, Infra 23, App 60, UI 23). Runtime check on the exact user scenario (RU interface, default Market, 0.01 BTC/USDT): Place → confirmation → confirm → dialog closed, log shows `Paper fill: "Buy" 0.01 BTC/USDT at <live close>` (the fill came from real Binance klines), no crash signature, order persisted to SQLite (`dialog_market_ru_fixed.png`; probe script `dialog-fix-ui-test.ps1`, PS 5.1 needs UTF-8 + BOM for RU literals; a closed Avalonia ComboBox exposes no Text UIA node — probes must measure `BoundingRectangle.Width` instead).

## T-014 — Instrument autocomplete in the order dialog (queued by user)

- **Branch:** `feat/symbol-autocomplete` (planned; not scheduled before T-011)
- **Scope:** `src/ZettaTrade.App` (dialog symbol input → editable list/AutoCompleteBox over `GetSymbolsAsync`), resx if new strings needed.
- **What:** typing "Btc/usdt" every time is clumsy — suggest completions from the adapter symbol list as the user types (case-insensitive prefix/contains), keyboard-navigable, graceful empty state when the symbols request fails offline (the field stays free-form by design).

## T-015 — Configurable exchange endpoints + registry seam (queued by user)

- **Branch:** `feat/exchange-config` (planned)
- **Scope:** `src/ZettaTrade.Infrastructure/Exchange` (`BinanceAdapter` constructor + wiring), the registration site in Infrastructure/App, new `Exchange.*` config section (the rule: new features get a config section). Core contracts untouched; public read-only endpoints mean `ISecretStore` is not involved.
- **What:** `BinanceAdapter` hardcodes `_baseUrl = "https://api.binance.com"` and `_wsUrl = "wss://stream.binance.com:9443/ws"` as private literals — testnet (`testnet.binance.vision`), regional mirrors (`api1..3.binance.com`, `api.binance.us`) or a proxy need a recompile.
- **Fix direction:**
  - Adapter constructor gains optional `baseUrl`/`wsUrl` parameters with the production defaults (existing call sites compile unchanged).
  - Registration reads `Exchange:Binance:RestBase` / `Exchange:Binance:WsUrl` from appsettings; a missing section falls back to the shipped defaults (hidden-safe, no breaking change for existing configs).
  - The registration site doubles as the adapter-registry seam T-016 selects from.
- **Acceptance:**
  - A config override points the adapter at a different host (unit test against a stub URL, no network).
  - Missing config section → production defaults at registration (test).
  - gate.

## T-016 — Exchange selector in the toolbar (queued by user)

- **Branch:** `feat/exchange-selector` (planned; **blocker: needs a 2nd registered adapter** — a selector over one item is meaningless)
- **Scope:** toolbar/status bar in `src/ZettaTrade.App`, hooking the T-015 registry seam. No Core interface changes.
- **UX design (the anti-confusion rules):**
  - **One concept, one control:** a single "active exchange" picked in exactly one toolbar place; chart, order book, dialog symbol resolution and new-order context all follow it blind. Never two places that can disagree (no split-brain: chart on Binance while the dialog places on Bybit is impossible by construction).
  - **Hide what there is no choice about:** the selector is rendered only when ≥2 adapters are registered; with one adapter the toolbar looks exactly as today.
  - **The context is always visible while the control is rare enough:** a status chip combines exchange + trading mode (`Binance · Paper`); trading mode stays a separate always-visible concern and is never folded into the same picker as the exchange — those are two different decisions.
  - **Visible at the action points:** the order dialog header names the active exchange; history rows already carry the Exchange column (T-010).
  - **Depth out of the toolbar:** endpoints/testnet configuration (T-015) lives in Settings; the picker only switches context, it does not configure.
  - **Persistence with a loud fallback:** the last choice persists; a persisted exchange that is unavailable at startup falls back to a default with an explicit user-facing warning, never silently.
- **Acceptance:**
  - One adapter: no selector, zero behavior change (pinned by test).
  - Two adapters: switching is atomic — chart/book/dialog switch together, in-flight data of the previous adapter is dropped, not mixed.
  - Unavailable persisted choice → startup warning + default selection; gate.

## T-017 — One symbol context for the workspace (queued during T-011)

- **Branch:** `feat/workspace-symbols` (started 2026-11-01, cut from develop @ 0911f13)
- **Scope:** `src/ZettaTrade.App` (workspace symbol flow: `ChartViewModel` + `OrderBookViewModel` follow one selection; the dead `WhenActivated` path for nested-panel DataContext VMs), possibly a small `MainViewModel` coordination change.
- **What:** Born from the T-011 probe: `ChartViewModel` is silently dead (its `WhenActivated` never fires for a nested DataContext, so the symbol combo stays empty and the chart draws "Нет данных"), and the T-011 book carries its own typed symbol. The workspace should have ONE active symbol driving chart and book; this task decides whether that is ctor-subscription bootstrap, view-attached activation, or an explicit workspace-level `SelectedSymbol` owner, then removes the duplicate default the two panels currently hold.
- **Design (decided):** ctor-subscription bootstrap — the workspace VMs are singletons bound as nested DataContexts where `WhenActivated` is a proven dead path (T-011 probe), so `ChartViewModel` drops `IActivatableViewModel` and mirrors the StatusBar/book pattern: ctor-built Rx pipelines, raw test-visible streams, UI-thread hops only for property assignment. Selection ownership: `ChartViewModel` (the only panel holding the symbols list) exposes a raw `IObservable<Symbol> SymbolSelections` (combo changes and the list-0 preselect both flow through one `WhenAnyValue(SelectedSymbol)`); `CompositionRoot` wires chart → `OrderBookViewModel`, which follows the selection by writing its free-typed `SymbolInput` (one owner, typed input remains the offline override; the last user action wins), so the book's and the chart's defaults stop being two independent owners. XAML stays unchanged: the existing combo keeps driving `Chart.SelectedSymbol`.
- **Acceptance:**
  - Selecting a symbol in the workspace combo drives both the chart and the order book (probe).
  - The chart shows real klines on cold start again (regression: the "Нет данных" dead state disappears without a page refresh).
  - gate.
- **Status:** done. Canon: the feature commit `de116cb` is on develop (fast-forward merge). Outcome:
  - Shipped: `ChartViewModel` rewritten without `IActivatableViewModel` — ctor-built pipelines per the singleton precedent; raw `SymbolSelections` (the combo change and the list-0 preselect flow through one `WhenAnyValue(SelectedSymbol)` chain); `BarsUpdates` = `Select(LoadBars).Switch().Replay(1).RefCount()` — a hot shared machine: exactly one load pipeline no matter how many watchers, late watchers get the latest bars; a silent `Catch<OperationCanceledException>` first (Switch-cancel is the normal outcome, no "failed" log spam), then `Catch<Exception>` logs and keeps the machine alive for later selections. Instrumented INF logging (bars requested/delivered, "Symbols loaded: {N}; preselected {Symbol}") mirrors the adapter/SettingsService precedent.
  - `OrderBookViewModel` gained the `workspaceSelections` ctor stream: a workspace selection rewrites the typed `SymbolInput` (`symbol.Normalized`) — the last user action wins, the free-typed input stays the offline override; a workspace-stream failure is logged and the typed input keeps working. `CompositionRoot` wires chart → book with one line (`ChartViewModel.SymbolSelections`).
  - Tests 137 → 145: 5 `ChartViewModelTests` (ctor bootstrap without activation, raw selections flow, bars delivered for the latest selection, fast-switch cancels the in-flight load — a 30 ms fake plus token asserts, winner token verified alive at delivery time since a completed load ends its own Rx lease; a failed load keeps the machine) + 3 book workspace tests (selection rewrites the input and switches the feed, an identical selection does not tear the feed, a selection-source failure keeps the typed input working). `Bar` carries no symbol — fake bars mark theirs in `Close` (100/200/300).
  - Probe (real Binance, UIA-driven combo): cold start — WS connect ~1.5 s; 50 symbols load, preselect ETH/BTC → "Chart bars load requested for ETH/BTC" → 168 candles delivered; the book header shows ETH/BTC too (the workspace preselect drives BOTH panels). UIA combo switch → ETH/USDT: bars requested+delivered, "Workspace selection applied: ETH/USDT", book header flips to ETH/USDT with live depth; screenshot pairs differ in both phases. The "Нет данных" dead-chart state is gone without any activation path.
  - Follow-ups from the probe: the first symbols apply can lag ~4–10 s on an idle UI thread (a posted dispatcher job on a non-interacted window) — cold-start polish candidate (post priority or inline apply); illiquid pairs (BNB/ETH) produce byte-identical "after" screenshots (sparse depth) — probes must drive liquid symbols; a probe on a stale binary misreads behavior — guard probes behind a successful build.

## T-018 — App version display in the status bar (queued by the user at the T-017 merge)

- **Branch:** `feat/status-version` (started 2026-11-01, cut from develop @ de116cb)
- **Scope:** `src/ZettaTrade.App` (status bar version chip + the version source); branch tail: the `Directory.Build.props` suffix bump + `CHANGELOG.md` per the in-file convention (next suffix alpha.12 — T-017 merged without its own bump, so this task takes alpha.12).
- **What:** The user asked for the app version somewhere in the UI. The status bar already lists tech state chips (connection status, theme name, language code) — a version chip fits there. Source of truth: the exe's `AssemblyInformationalVersionAttribute` (bakes `Directory.Build.props` `$(Version)` = `0.1.0-alpha.N`), read once by a tiny static accessor in the App project; `StatusBarViewModel` gains a documented `AppVersion` property passed through the ctor (a literal in tests). XAML: one more `Auto` column. The version string is not user-facing localization — it is tech state like the theme name, no resx key.
- **Acceptance:**
  - The status bar shows the running version (probe: UIA name search for "0.1.0-alpha.12" finds the chip).
  - Version bump alpha.11 → alpha.12 + CHANGELOG entry lands with this task.
  - gate.
- **Status:** done. Canon: the feature commit `fb38467` is on develop (fast-forward merge). Outcome:
  - Shipped: `AppVersion` accessor in the App project — the entry-assembly informational version (MSBuild bakes `Directory.Build.props` `$(Version)`) read exactly ONCE via a static get-only property initializer (compiles to a readonly field + static ctor, thread-safe once semantics; reviewed with the user), the `+<git-commit>` suffix the SDK appends in repositories trimmed by `ToDisplayVersion`, and an "unknown" fallback so the chip can never be blank. `StatusBarViewModel` gained the `AppVersion` ctor-threaded property; `CompositionRoot` passes `AppVersion.Current` (the single production call site); XAML adds the fifth `Auto` status column — the chip sits rightmost after the language chip. The version string is tech state (like the theme name), no resx key.
  - Tests 145 → 148: 3 new `StatusBarViewModelTests` (the chip flow from the ctor, the git-suffix trim, the never-blank fallback) + the 3 existing ctor calls updated with a literal.
  - Probe: UIA search over the window found exactly one version text node — `0.1.0-alpha.12`, no suffix leak; the status strip screenshot shows `Готов | Dark | ru | 0.1.0-alpha.12`. GATE PASSED (13 + 76 + 28 + 31 = 148).
  - Branch tail: `Directory.Build.props` alpha.11 → alpha.12 (the in-file comment now flags T-018 → alpha.12, next alpha.13, and mentions the status-bar chip); `CHANGELOG.md` gains the alpha.12 section documenting T-018 and noting T-017 ships inside this release (T-017 merged without its own bump — accepted drift in the strictly "one release == one merged task" reading; the bump convention itself holds: next is alpha.13).
