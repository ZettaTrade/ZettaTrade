---
name: zettatrade
description: >-
  Project conventions and architecture rules for ZettaTrade, a cross-platform desktop trading terminal
  (crypto and stocks) built with .NET 10, C# 13, Avalonia UI 12 and ReactiveUI.Avalonia, with AI
  integration via MCP. Use whenever writing, reviewing, or refactoring ZettaTrade code: ViewModels,
  XAML views, exchange adapters, chart rendering, localization, theming, settings, or trading and risk
  logic. Enforces ReactiveUI (not CommunityToolkit.Mvvm), plugin-first clean architecture, SOLID/DRY/KISS
  principles, and the critical trading safety rules.
---

# ZettaTrade — AI Skill File

## Project Context
- **Project Name**: ZettaTrade
- **Type**: Cross-platform desktop trading terminal
- **Tech Stack**: .NET 10, C# 13, Avalonia UI 12, ReactiveUI.Avalonia 12.1
- **Target Platforms**: Windows, macOS, Linux
- **License**: MIT (Open Source)

## Domain
Cryptocurrency and traditional stock trading terminal with deep AI/ML integration via MCP (Model Context Protocol).

## Architecture Principles
1. **Clean Architecture** — Presentation → Domain → Infrastructure
2. **Plugin-first** — All exchange adapters, indicators, strategies are plugins
3. **Reactive** — UI and data flows use ReactiveUI exclusively (NEVER System.Reactive)
4. **MCP-native** — AI integration is first-class, not bolted on
5. **Performance** — Channel<T> for market data, SkiaSharp for rendering
6. **Security** — Encrypted secrets, no data leaves user's machine without consent

## Key Technologies
- **UI Framework**: Avalonia UI 12 (XAML-based, cross-platform)
- **MVVM Framework**: ReactiveUI.Avalonia 12.1 (NOT CommunityToolkit.Mvvm)
  - Use ReactiveObject as base class (NOT ObservableObject)
  - Use RaiseAndSetIfChanged for properties (NOT [ObservableProperty])
  - Use ReactiveCommand for commands (NOT [RelayCommand])
  - Use WhenActivated for lifecycle management
  - Use IObservable for reactive streams
  - **Avalonia 12**: Package renamed from Avalonia.ReactiveUI to ReactiveUI.Avalonia
  - **Avalonia 12**: Namespace changed from using Avalonia.ReactiveUI to using ReactiveUI.Avalonia
- **Graphics**: SkiaSharp for custom chart rendering
- **Storage**: SQLite via EF Core (local), optional TimescaleDB
- **Networking**: HttpClient + WebSocket (ClientWebSocket)
- **Serialization**: System.Text.Json
- **DI**: Microsoft.Extensions.DependencyInjection
- **Logging**: Serilog with structured logging
- **Localization**: ResourceManager + .resx files + custom LocalizedExtension
- **AI Protocol**: MCP SDK (ModelContextProtocol)
- **Local AI**: Ollama API, llama.cpp bindings (optional)
- **Testing**: xUnit + NSubstitute + FluentAssertions

## Exchange Adapters (Phase 2)
- **Architecture**: Plugin-based with IExchangeAdapter interface
- **Base Classes**:
  - ExchangeAdapterBase - common functionality for all adapters
  - WebSocketClient - WebSocket with auto-reconnection
- **Market Data**:
  - IMarketDataAdapter - read-only market data (tickers, order books, trades, historical bars)
  - Reactive streams via IObservable for real-time updates
  - ConcurrentDictionary for thread-safe subscription management
- **Trading**:
  - ITradingAdapter - extends IMarketDataAdapter with order management
  - Risk validation via IRiskManager
  - Human confirmation via IConfirmationService for high-risk operations
- **Binance Implementation**:
  - REST API for historical data and symbol info
  - WebSocket streams for real-time ticker, order book, and trades
  - Automatic reconnection with exponential backoff
  - Symbol format conversion (normalized vs exchange-specific)

## Chart Rendering (Phase 2)
- **Control**: CandlestickChart using SkiaSharp
- **Features**:
  - Real-time updates via ObservableCollection of Bar
  - Zoom and pan with mouse wheel and drag
  - Price and time axes with auto-scaling
  - Bullish/bearish candle coloring
  - Grid lines and crosshair (planned)
- **ViewModel**: ChartViewModel with symbol selection and data loading
- **Performance**: Hardware-accelerated rendering via SkiaSharp

## Settings & Configuration
- **General settings**: appsettings.json + user overrides (JSON)
- **Feature settings**: Per-module config files (mcp.config.json, exchanges.config.json, etc.)
- **Secrets**: ISecretStore interface with platform-native implementations
  - Windows: DPAPI (ProtectedData)
  - macOS: Keychain Services (P/Invoke)
  - Linux: Secret Service API (libsecret/D-Bus) with encrypted file fallback
- **Config paths**: XDG-compliant per platform
  - Windows: %APPDATA%/ZettaTrade/config/
  - macOS: ~/Library/Application Support/ZettaTrade/config/
  - Linux: ~/.config/ZettaTrade/config/

## Localization (Reactive Approach)
- All internal code in English; user-facing strings via LocalizationManager
- Resource files: Strings.{culture}.resx (standard .NET .resx format)
- ResourceManager loads strings from embedded .resx resources
- Primary languages: en, ru; planned: zh-CN, ja, ko, es, de, fr
- **Runtime language switching without restart** — all UI updates automatically via reactive bindings

### Reactive Localization Pattern (MANDATORY)
- **NO INotifyPropertyChanged** for localization — use IObservable exclusively
- **NO LocalizedViewModelBase** — use direct XAML binding instead
- LocalizationManager.GetObservable(key) returns IObservable<string>
- Use Observable.Defer for lazy evaluation at subscription time
- Use ToBinding() in XAML for automatic UI updates
- Language changes propagate automatically to all {loc:Localized ...} bindings

### XAML Usage (Primary Method)
```xml
<TextBlock Text="{loc:Localized Menu.File}" />
<Button Content="{loc:Localized Order.Buy}" />
```
- LocalizedExtension returns GetObservable(Key).ToBinding()
- Updates automatically when language changes (no restart needed)
- No ViewModel properties needed for static strings
- ObserveOnUiThread() ensures updates happen on UI thread

### ViewModel Usage (ONLY for dynamic data)
```csharp
// ❌ WRONG: Don't create ViewModel properties for simple strings
public class MenuViewModel
{
    public string MenuFile { get; set; } // Unnecessary!
}

// ✅ RIGHT: Use ViewModel only for dynamic data
public class StatusBarViewModel
{
    public string ConnectionStatus { get; set; } // Dynamic - needs ViewModel
    public string CurrentTheme { get; set; } // Dynamic - needs ViewModel
}

// ❌ WRONG: Don't localize in ViewModel
Loc.GetObservable("Status.Ready")
    .Subscribe(value => StatusText = value); // Unnecessary!

// ✅ RIGHT: Localize directly in XAML
<TextBlock Text="{loc:Localized Status.Ready}" />
```

### Key Principles
- **Direct XAML binding for simple strings** — no ViewModel properties needed
- **ViewModel properties ONLY for dynamic data** — connection status, user input, etc.
- Thread-safe caching with ConcurrentDictionary
- Automatic cache invalidation on language change
- **ObserveOnUiThread()** extension method in ZettaTrade.UI.Extensions namespace
- No manual subscription management — use DisposeWith()
- GetObservable uses Observable.Defer for lazy start value
- **Language switches on-the-fly** — all {loc:Localized} bindings update automatically

## Theming
- Avalonia theme system with custom trading palettes
- Built-in: Dark (default), Light, Midnight Blue, OLED Black
- Custom themes via JSON files in themes/ directory
- Users can create/edit themes without recompiling
- Theme JSON format includes: name, displayName, baseTheme, colors, previewColors
- Color keys: Background, Surface, Border, Accent, TextPrimary, TextSecondary, Buy, Sell, CandleUp, CandleDown, ChartBackground, ChartGrid
- Chart-specific colors: CandleUp, CandleDown, Buy, Sell, Grid
- Runtime theme switching via ThemeManager service
- Sample theme created automatically on first run in themes/ directory

## Coding Conventions
- **ALL code, comments, XML docs, commit messages, and internal strings MUST be in English**
- User-facing strings go through the localization system
- File-scoped namespaces
- Primary constructors where appropriate
- Record types for DTOs and value objects
- Interface segregation for testability
- Async/await everywhere (no .Result or .Wait())
- CancellationToken on all async methods
- Nullable reference types enabled
- XML docs on public API
- **NEVER use CommunityToolkit.Mvvm** — use ReactiveUI exclusively
- **NEVER use [ObservableProperty], [RelayCommand], ObservableObject** — use ReactiveUI equivalents
- Use ReactiveObject as base class for all ViewModels
- Use RaiseAndSetIfChanged for property change notifications
- Use ReactiveCommand for commands
- Use WhenActivated for lifecycle management and subscriptions
- Use IObservable for reactive streams and data flows
- **NEVER create generic catch-all files** like Enums.cs, Entities.cs, Models.cs, Utils.cs
- **Split files by domain responsibility** — each file should have a clear, focused purpose
- **Use descriptive file names** that indicate the domain concept (OrderEnums.cs, not Enums.cs)
- **One entity per file** for complex entities (Order.cs, Position.cs, Trade.cs)
- **Group related small types** in one file only if they share the same domain concept (MarketData.cs for Bar, Ticker, OrderBook)
- **Avoid magic numbers** — use named constants or enums instead of bare literals (e.g., const int MaxRetries = 3; instead of if (attempts > 3))
- **Use nameof()** for property/method/type names in strings (e.g., nameof(Order.Symbol) instead of "Symbol", RaisePropertyChanged(nameof(Status)))
- **Extract repeated literals** — if a value appears in multiple places, define it as const or static readonly field in an appropriate location
- **Prefer domain-specific constants** — group related constants in static classes (e.g., OrderLimits.MaxQuantity, ExchangeNames.Binance)
- **Format method signatures** — methods with 3+ parameters should be formatted on multiple lines for readability (one parameter per line, indented)
- **NEVER use fire-and-forget** — avoid underscore-equals async method pattern. Either await the task or use proper error handling with Subscribe(onError: ...) for reactive streams
- **Avoid synchronous methods returning Task** — if a method is truly synchronous, return the value directly instead of wrapping in Task.FromResult. Only return Task when the method actually performs async I/O or when required by an interface contract
- **NEVER use .GetAwaiter().GetResult() in constructors** — this blocks the thread and can cause deadlocks in UI applications. Use IAsyncInitializable pattern instead: implement InitializeAsync() method and call it from async context (e.g., App.OnFrameworkInitializationCompleted)
- **Use RxVoid from ReactiveUI** — for commands without return value, use ReactiveCommand<TParam, RxVoid> (NOT System.Reactive.Unit). Methods should return Task (not Task<Unit>). Note: IObservable<T> is OK (it's from System.Reactive but used by ReactiveUI), but NEVER use System.Reactive.Unit — always use ReactiveUI.RxVoid instead
- **NO HTML entities in C# code** — never use &lt;, &gt;, &amp;, etc. in C# source files. Use simple // comments for examples instead of XML documentation with HTML escapes

## Design Principles (MANDATORY)

### SOLID Principles

**S - Single Responsibility Principle (SRP)**
- Each class should have ONE reason to change
- ViewModels should coordinate, not contain business logic
- Split large ViewModels into focused sub-ViewModels (MenuViewModel, StatusBarViewModel, ChatViewModel, SettingsViewModel)
- Services should do one thing well

**O - Open/Closed Principle (OCP)**
- Open for extension, closed for modification
- Use interfaces and dependency injection for extensibility
- Prefer composition over inheritance
- Use strategy pattern for varying behaviors

**L - Liskov Substitution Principle (LSP)**
- Derived classes must be substitutable for their base classes
- Don't override methods to throw exceptions or change behavior
- Use interfaces for contracts, not inheritance hierarchies

**I - Interface Segregation Principle (ISP)**
- Many small interfaces > one large interface
- Clients should not depend on methods they don't use
- Split IExchangeAdapter into IMarketDataAdapter and ITradingAdapter
- Create role-specific interfaces (IReadable, IWritable, INotifiable)

**D - Dependency Inversion Principle (DIP)**
- Depend on abstractions, not concretions
- Inject dependencies through constructors
- Use interfaces for all external dependencies
- Never use new keyword for services — use DI container

### DRY (Don't Repeat Yourself)
- **NEVER duplicate code** — extract to methods, classes, or helpers
- Use extension methods for repeated operations
- Centralize configuration in appsettings.json
- Delegate to managers (ThemeManager, LocalizationManager) instead of duplicating data

### KISS (Keep It Simple, Stupid)
- **Avoid over-engineering** — solve the actual problem, not hypothetical ones
- Prefer simple solutions over clever ones
- Readable code > clever code
- Don't add features "just in case"
- Delete code that is no longer needed

### YAGNI (You Aren't Gonna Need It)
- **Don't implement until you need it**
- No speculative features or abstractions
- Remove TODO placeholders if not implementing soon
- Start with minimal viable implementation, iterate later

### Composition Over Inheritance
- **Prefer composition** — build complex objects from simple parts
- Use dependency injection to compose services
- Inheritance only for "is-a" relationships, not "has-a"
- Favor interfaces over abstract classes

### Law of Demeter (Principle of Least Knowledge)
- Objects should only talk to their immediate friends
- Don't chain property access: a.B.C.D.Method()
- Pass only what is needed, not entire objects
- Use facade pattern to simplify complex APIs

### Tell, Don't Ask
- **Tell objects what to do**, don't ask for their state
- Bad: if (order.Status == Pending) order.Cancel()
- Good: order.TryCancel()
- Encapsulate behavior in domain objects
- Avoid anemic domain models

### Principle of Least Astonishment
- Code should do what its name suggests
- Method names should describe behavior, not implementation
- Avoid side effects in getters
- Follow established conventions and patterns

### Separation of Concerns (SoC)
- **Separate UI, business logic, and data access**
- ViewModels = UI coordination only
- Services = business logic
- Repositories = data access
- Never mix concerns in one class

### Single Level of Abstraction (SLAP)
- Each method should operate at ONE level of abstraction
- High-level methods orchestrate, low-level methods implement
- Don't mix SaveOrder() with ValidateEmailFormat()
- Extract helper methods for different abstraction levels

### ViewModel Architecture Example
- **MainViewModel** - Coordinator only, delegates to sub-ViewModels
- **MenuViewModel** - Menu localization and commands
- **StatusBarViewModel** - Status bar (connection, theme, language)
- **ChatViewModel** - AI chat functionality
- **SettingsViewModel** - Settings management (theme, language)
- Each ViewModel has single responsibility and is independently testable

## When Making Changes
1. Always maintain backward compatibility in public interfaces
2. Add tests for new domain logic
3. Follow reactive patterns (Observable, not events)
4. Consider plugin boundary — new adapters should be separate assemblies
5. AI features should work with local models first, cloud as enhancement
6. Never hardcode exchange-specific logic in core
7. **All code, comments, and internal strings MUST be in English**
8. User-facing strings must use localization system (never hardcoded)
9. **For localization: use {loc:Localized Key} in XAML for static strings, ViewModel properties ONLY for dynamic data**
10. **NEVER create ViewModel properties for localized strings** — use direct XAML binding instead
11. Sensitive data (API keys, tokens) MUST go through ISecretStore
12. Settings changes should be reactive (IObservable<SettingsChange>)
13. New features should have corresponding config sections
14. Platform-specific code must be isolated in Infrastructure layer
15. **Use ReactiveUI exclusively** — never mix with CommunityToolkit.Mvvm
16. All ViewModels must inherit from ReactiveObject
17. Use WhenActivated for subscription lifecycle management
18. Always use ObserveOnUiThread() extension for UI updates (not ObserveOn(RxApp.MainThreadScheduler))
19. Dispose subscriptions properly with DisposeWith(disposables)
20. **Use async/await throughout** — avoid blocking calls (.Result, .Wait())
21. **NEVER use lock(_lockObject) in async code** — use SemaphoreSlim instead, even if there's no await inside the critical section
22. **CRITICAL: When updating scaffold files, ALWAYS update BOTH:**
    - Real files in scaffold/ directory (actual source code)
    - scaffoldData.ts (data for web interface export)
    - Never update only one — they must stay in sync!
23. **ALWAYS provide a commit message in English after making changes** — brief summary (2-4 lines) describing what was changed and why, following conventional commit format (feat:, fix:, refactor:, docs:, etc.)
24. **Use #region directives** to organize large files into collapsible sections (e.g., #region Properties, #region Commands, #region Private Methods, #region Event Handlers). After #endregion, always specify the region name (e.g., #endregion Properties, #endregion Commands)

## Trading Safety Rules (CRITICAL)
25. **NEVER execute trades without validation** — all orders must pass through IRiskManager
26. **NEVER default invalid values** — reject invalid symbol/side/type instead of substituting defaults
27. **ALWAYS require human confirmation** for high-risk operations (large orders, position changes)
28. **NEVER write to stdout except MCP protocol messages** — Serilog must log to files only, not console
29. **NEVER write to stdout in MCP server** — breaks protocol; use proper logging to files only
30. **ALWAYS validate enum parsing** — return error for invalid values, don't silently default
31. **ALWAYS require symbol in cancel_order** — some exchanges (Binance) require it
32. **NEVER overwrite encrypted secrets on decryption failure** — preserve data integrity
33. **ALWAYS save secrets to disk immediately** — don't keep in memory only
34. **ALWAYS pass secrets via stdin** — never as command-line arguments (visible in process list)

## Performance Targets
- Order book rendering: 60fps with 1000+ levels
- WebSocket message processing: < 1ms per message
- Chart with 100k candles: smooth pan/zoom
- Cold start to interactive: < 3 seconds
- Memory footprint: < 500MB typical usage