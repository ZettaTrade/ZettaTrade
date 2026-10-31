# ZettaTrade — Task Board

Statuses: `planned` → `in-progress` → `review` → `done`. One task per feature. Agents work only in their own worktree/branch, within the listed scope. Branches are created with `pwsh scripts/new-worktree.ps1 -Name <name>`.

| Task | Branch | State | Blocker |
|------|--------|-------|---------|
| T-001 Load themes from JSON files | develop | done | — |
| T-002 Wire advertised menu hotkeys | feat/hotkeys | review | — |
| T-003 Hot-reload of theme files | feat/theme-reload | review | — |
| T-004 Settings: themes folder + file validation | feat/theme-settings-ui | review | — |
| T-005 Canonical theme identity | fix/theme-identity | review | — |
| T-006 Theme preview swatches in the picker | feat/theme-preview | review | — |
| T-007 resx en/ru sync gate | chore/resx-gate | review | — |

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
