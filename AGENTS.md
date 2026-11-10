# ZettaTrade agent rules

Cross-platform desktop trading terminal (crypto + stocks) with first-class AI integration via MCP.
Stack: .NET 10, C# 13, Avalonia UI 12, ReactiveUI.Avalonia 12.1, SkiaSharp, EF Core + SQLite, Serilog, xUnit + NSubstitute + FluentAssertions. MIT, open source. Targets: Windows, macOS, Linux.
These are the always-on rules: they bind in every session. The working canon — architecture, MVVM and UI, localization, coding style — lives in the `zettatrade` skill (see the canon map at the bottom).

## Workflow (read first)
1. Work only in your own git worktree on your own branch. Feature branches are based on `develop` — never on `main`: create them with `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/new-worktree.ps1 -Name <name> -Base develop` (the script's default base is `main`, so pass `-Base develop` explicitly). Never commit directly to `main` or `develop`. Once the task is done and merged, remove its worktree folder and its branch: `git worktree remove <folder>` (it refuses on uncommitted files — resolve those first), then `git branch -d <name>`; no stale task folders linger around the repo.
2. Stay inside the scope named in your task. If you need a change outside it (Core contracts, shared files), stop and ask the supervisor.
3. Before reporting done run `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/gate.ps1 -BaseBranch develop`. It must print `GATE PASSED`. Never report done otherwise.
4. Add tests for new domain logic.
5. Keep `docs/TASKS.md` truthful: whenever a task's status moves (`planned` → `in-progress` → `review` → `done`), update it in the same commit series as the work — the board row and the task's own section (the `## T-0NN …` heading with its **Status** bullet). Once a task is done and merged, move its detail section verbatim to `docs/TASKS-ARCHIVE.md`, in canonical T order; the one-line board row stays on the board. Rows and sections reference only portable facts (branches, worktree names like `zt-<name>`, base hashes); machine-local absolute paths never enter repository text (the gate lint `[MACHINE-PATH]` fails on them).
6. One alpha release per board task that changes the app: bump the `VersionSuffix` in `Directory.Build.props` (`alpha.N` → `alpha.N+1`) and add the matching `CHANGELOG.md` section in the same work. Docs-only tasks do not bump the version.
7. Finish every task with an English conventional-commit message (feat:, fix:, refactor:, docs:), 2-4 lines.
8. At most 2 review rounds per task, then escalate to the human.
9. Reason in English and answer in Russian (token economy): internal thinking, plans and scratch notes are English; user-facing replies are Russian. Everything shipped in the repo stays English (the localization canon lives in the `zettatrade` skill).
10. Shared `develop` receives landings from several agent sessions concurrently: before ANY touch of `develop`, `git fetch origin` and verify the top has not moved (`git rev-list --left-right --count origin/develop...develop`); on an unexpected shift, rebase your local commits onto `origin/develop` preserving the landed content — never stack onto a stale top. Claim task numbers (`T-0NN`) only off the synced board; keep docs commits land-friendly (unique tail anchors). Full canon: `.opencode/skills/zettatrade/references/sync.md`.

## Shared files (conflict hotspots)
- `ZettaTrade.Core/Interfaces/*`: supervisor only. Keep public interfaces backward compatible.
- `CompositionRoot.cs`: register your services in your own extension method (`Add<Module>Services`); add one line to the root.
- `*.resx`: prefix your keys by module (`Chart.*`, `Exchange.*`, `Ai.*`) and add keys for BOTH en and ru.

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

## Canon map
- This file: the always-on rules above — identity, workflow, shared files, trading safety, performance.
- `.opencode/skills/zettatrade/SKILL.md`: the working skill — architecture, MVVM and UI, localization — plus the read-on-demand index of its `references/` (project, adapters, charting, settings, theming, localization, style, sync, orchestrator, design-principles, making-changes).
- `.opencode/agents/zt-{feature,reviewer,supervisor}.md`: the role cards.
- `.opencode/skills/zettatrade-supervisor/SKILL.md`: supervisor mode, explicit user invocation only.
- `ZettaTrade_SKILL.md`: a stub index kept for old pointers; `.cursorrules` is legacy.
