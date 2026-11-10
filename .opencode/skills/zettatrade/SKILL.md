---
description: >-
  Project conventions for ZettaTrade: workflow, clean architecture, ReactiveUI-only MVVM,
  mandatory localization, code style, performance targets, and critical trading-safety rules.
  Load before writing, reviewing, or refactoring any ZettaTrade code — ViewModels, XAML views,
  exchange adapters, chart rendering, settings, or trading and risk logic.
---

# ZettaTrade project skill

`AGENTS.md` stays binding in full while this skill is loaded — the workflow rules, shared files,
trading safety and performance targets live there and are not repeated here. This file holds the
architecture-side canon plus the on-demand references beside it.

## Architecture
- Clean Architecture: Presentation -> Domain -> Infrastructure.
- Target layout: `src/ZettaTrade.{App,Core,Infrastructure,AI,UI,Charting,Strategies,Plugins}` plus the domain-neutral `src/Livery` appearance library, `tests/*.Tests`. Not every project exists yet.
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
- The full reactive pattern (GetObservable, ToBinding, XAML and ViewModel usage): `references/localization.md`.

## Read on demand
The heavy canon lives in `references/` beside this file. Read a file when its row applies — do not load them all up front.

| Reference | Read it when |
|---|---|
| `references/project.md` | starting a task in an unfamiliar area: repo identity, domain, architecture principles, key technologies |
| `references/adapters.md` | writing or reviewing an exchange adapter (IMarketDataAdapter / ITradingAdapter, WebSocket reconnection) |
| `references/charting.md` | working on the SkiaSharp candle chart |
| `references/settings.md` | settings, configuration files, the secret store, platform config paths |
| `references/localization.md` | writing XAML or ViewModels that surface user-facing strings |
| `references/theming.md` | theme palettes, the theme JSON format, ThemeManager work |
| `references/style.md` | the full coding-conventions detail plus the typography and encoding discipline |
| `references/sync.md` | before ANY touch of `develop`: fetch hygiene, landing discipline (AGENTS rule 10 points here) |
| `references/orchestrator.md` | running multi-subagent waves: wave sizing, self-contained prompts, crash resume |
| `references/design-principles.md` | the SOLID/DRY/KISS exemplar (MANDATORY) when a structure choice is debatable |
| `references/making-changes.md` | the pre-completion change checklist — walk it before claiming a change done |
