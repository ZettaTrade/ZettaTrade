---
description: >-
  Project conventions for ZettaTrade: workflow, clean architecture, ReactiveUI-only MVVM,
  mandatory localization, code style, performance targets, and critical trading-safety rules.
  Load before writing, reviewing, or refactoring any ZettaTrade code — ViewModels, XAML views,
  exchange adapters, chart rendering, settings, or trading and risk logic.
---

# ZettaTrade agent rules

Cross-platform desktop trading terminal (crypto + stocks) with first-class AI integration via MCP.
Stack: .NET 10, C# 13, Avalonia UI 12, ReactiveUI.Avalonia 12.1, SkiaSharp, EF Core + SQLite, Serilog, xUnit + NSubstitute + FluentAssertions. MIT, open source. Targets: Windows, macOS, Linux.

## Workflow (read first)
1. Work only in your own git worktree on your own branch. Feature branches are based on `develop` — never on `main`: create them with `pwsh scripts/new-worktree.ps1 -Name <name> -Base develop` (the script's default base is `main`, so pass `-Base develop` explicitly). Never commit directly to `main` or `develop`. Once the task is done and merged, remove its worktree folder and its branch: `git worktree remove <folder>` (it refuses on uncommitted files — resolve those first), then `git branch -d <name>`; no stale task folders linger around the repo.
2. Stay inside the scope named in your task. If you need a change outside it (Core contracts, shared files), stop and ask the supervisor.
3. Before reporting done run `pwsh scripts/gate.ps1 -BaseBranch develop`. It must print `GATE PASSED`. Never report done otherwise.
4. Add tests for new domain logic.
5. Keep `docs/TASKS.md` truthful: whenever a task's status moves (`planned` → `in-progress` → `review` → `done`), update it in the same commit series as the work — the board row and the task's own section (the `## T-0NN …` heading with its **Status** bullet). Once a task is done and merged, move its detail section verbatim to `docs/TASKS-ARCHIVE.md`, in canonical T order; the one-line board row stays on the board.
6. One alpha release per board task that changes the app: bump the `VersionSuffix` in `Directory.Build.props` (`alpha.N` → `alpha.N+1`) and add the matching `CHANGELOG.md` section in the same work. Docs-only tasks do not bump the version.
7. Finish every task with an English conventional-commit message (feat:, fix:, refactor:, docs:), 2-4 lines.
8. At most 2 review rounds per task, then escalate to the human.
9. Reason in English and answer in Russian (token economy): internal thinking, plans and scratch notes are English; user-facing replies are Russian. Everything shipped in the repo stays English (see Localization).
10. Shared `develop` receives landings from several agent sessions concurrently: before ANY touch of `develop`, `git fetch origin` and verify the top has not moved (`git rev-list --left-right --count origin/develop...develop`); on an unexpected shift, rebase your local commits onto `origin/develop` preserving the landed content — never stack onto a stale top. Claim task numbers (`T-0NN`) only off the synced board; keep docs commits land-friendly (unique tail anchors). Full canon: `ZettaTrade_SKILL.md` -> Multi-Agent Repository Sync.

## Orchestrator: multi-agent waves (orchestrator/supervisor sessions)
- One board task per subagent session, no idle capacity: re-assign a finished or parked session the next suitable board task immediately — the rulebook is already in its context.
- Size each wave by the conflict surface (`CompositionRoot.cs` lines, resx tails, board rows/sections, `Directory.Build.props` + `CHANGELOG.md` heads); pre-assign every task its own `alpha.N` in board canonical order.
- Self-contained subagent prompts: own worktree via `scripts/new-worktree.ps1 -Name <name> -Base develop`, resx en+ru duty, own board flip, gate bar (`GATE PASSED`; `-AllowProtected` only for the protected files the task legitimately owns).
- On a subagent crash: inspect its worktree first (`git log` + `git status`), resume the SAME sessionID with a short continuation prompt; a fresh session only when the resume is impossible.
- Verify before merging: re-run the gate and review the subagent's diff in its worktree, land through the sync rules, then remove the worktree and the branch.
- Full canon: `ZettaTrade_SKILL.md` -> Multi-Agent Subagent Utilization.

## Shared files (conflict hotspots)
- `ZettaTrade.Core/Interfaces/*`: supervisor only. Keep public interfaces backward compatible.
- `CompositionRoot.cs`: register your services in your own extension method (`Add<Module>Services`); add one line to the root.
- `*.resx`: prefix your keys by module (`Chart.*`, `Exchange.*`, `Ai.*`) and add keys for BOTH en and ru.

## Architecture
- Clean Architecture: Presentation -> Domain -> Infrastructure.
- Target layout: `src/ZettaTrade.{App,Core,Infrastructure,AI,UI,Charting,Strategies,Plugins}`, `tests/*.Tests`. Not every project exists yet.
- Plugin-first: exchange adapters, indicators and strategies are separate assemblies. Never hardcode exchange-specific logic in core.
- Platform-specific code lives only in Infrastructure.
- Market data flows through `Channel<T>`; charts are rendered with SkiaSharp.
- AI features work with local models first (Ollama, llama.cpp), cloud is an enhancement. MCP via the ModelContextProtocol SDK.
- Exchange adapters: `IMarketDataAdapter` (read-only) and `ITradingAdapter : IMarketDataAdapter` (orders). Small interfaces, constructor injection, no `new` for services.

## MVVM and UI
- ReactiveUI only. NEVER CommunityToolkit.Mvvm, `[ObservableProperty]`, `[RelayCommand]`, `ObservableObject`.
- ViewModels inherit `ReactiveObject`; properties use `RaiseAndSetIfChanged`; commands are `ReactiveCommand`; lifecycle via `WhenActivated` + `DisposeWith(disposables)`.
- Use `ReactiveUI.RxVoid`, never `System.Reactive.Unit`.
- Use `ObserveOnUiThread()` (Livery.Extensions), not `ObserveOn(RxApp.MainThreadScheduler)`.
- ViewModels coordinate only; business logic lives in services. Split large ViewModels by responsibility.
- Avalonia 12: package is `ReactiveUI.Avalonia`, namespace `ReactiveUI.Avalonia` (not `Avalonia.ReactiveUI`).

## Localization (mandatory)
- All code, comments, XML docs, commit messages and internal strings are English.
- User-facing strings only via the localization system. Static strings: `{loc:Localized Key}` directly in XAML.
- NEVER create ViewModel properties for static localized strings. ViewModel properties only for dynamic data.
- No `INotifyPropertyChanged` for localization, no `LocalizedViewModelBase`.
- Language and theme switch at runtime without restart.

## Code style
- File-scoped namespaces, primary constructors where appropriate, records for DTOs/value objects, nullable enabled.
- XML docs on public API. `CancellationToken` on all async methods.
- Async everywhere. No `.Result`, `.Wait()`, `.GetAwaiter().GetResult()`. Never block in constructors; use an `InitializeAsync()` (IAsyncInitializable) pattern.
- NEVER `lock` in async code: use `SemaphoreSlim`, `ConcurrentDictionary`, `Channel`.
- No fire-and-forget (`_ = SomethingAsync()`): await it or handle errors (`Subscribe(onError: ...)`).
- Do not wrap synchronous results in `Task.FromResult` unless an interface requires it.
- No catch-all files (`Enums.cs`, `Models.cs`, `Utils.cs`). One entity per file; small related types may share a file named after the domain concept (`MarketData.cs`).
- No magic numbers (named constants or enums). Use `nameof()`. Methods with 3+ parameters: one parameter per line.
- Use `#region X` / `#endregion X` in large files.
- No HTML entities (`&lt;` etc.) in C# sources.
- SOLID, DRY, KISS, YAGNI. No speculative abstractions, no TODO placeholders, delete dead code.
- Settings changes are reactive (`IObservable<SettingsChange>`); new features get a config section.

## Trading safety (CRITICAL, never relax)
- Every order passes through `IRiskManager`. Never execute without validation.
- Never substitute defaults for invalid symbol/side/type or unparsable enums: return an error.
- High-risk operations (large orders, position changes) require human confirmation via `IConfirmationService`.
- `cancel_order` always requires the symbol.
- Secrets only through `ISecretStore`; save immediately; pass via stdin, never as command-line arguments; never overwrite encrypted secrets when decryption fails.
- Nothing may write to stdout except MCP protocol messages. Serilog logs to files only.
- Never use real exchange keys. Testnet and mocks only.

## Performance targets
Order book 60 fps with 1000+ levels; WebSocket message < 1 ms; 100k-candle chart pans smoothly; cold start < 3 s; typical memory < 500 MB.
