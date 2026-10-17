# ZettaTrade — AI Skill File

## Project Context
- **Project Name**: ZettaTrade
- **Type**: Cross-platform desktop trading terminal
- **Tech Stack**: .NET 10, C# 13, Avalonia UI 11.x, ReactiveUI
- **Target Platforms**: Windows, macOS, Linux
- **License**: MIT (Open Source)
- **Repository**: github.com/zettatrade/zettatrade (planned)

## Domain
Cryptocurrency and traditional stock trading terminal with deep AI/ML integration via MCP (Model Context Protocol).

## Architecture Principles
1. **Clean Architecture** — Presentation → Domain → Infrastructure
2. **Plugin-first** — All exchange adapters, indicators, strategies are plugins
3. **Reactive** — UI and data flows use ReactiveUI / System.Reactive
4. **MCP-native** — AI integration is first-class, not bolted on
5. **Performance** — Channel<T> for market data, SkiaSharp for rendering
6. **Security** — Encrypted secrets, no data leaves user's machine without consent

## Key Technologies
- **UI Framework**: Avalonia UI (XAML-based, cross-platform)
- **MVVM Framework**: ReactiveUI (NOT CommunityToolkit.Mvvm)
  - Use ReactiveObject as base class (NOT ObservableObject)
  - Use RaiseAndSetIfChanged for properties (NOT [ObservableProperty])
  - Use ReactiveCommand for commands (NOT [RelayCommand])
  - Use WhenActivated for lifecycle management
  - Use IObservable for reactive streams
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

## Localization
- All internal code in English; user-facing strings via LocalizationManager
- Resource files: Strings.{culture}.resx (standard .NET .resx format)
  - Strings.resx (English - default/fallback)
  - Strings.ru.resx (Russian)
  - Strings.zh-CN.resx (Chinese Simplified)
  - etc.
- ResourceManager loads strings from embedded .resx resources
- Primary languages: en, ru; planned: zh-CN, ja, ko, es, de, fr
- Runtime language switching without restart
- Reactive localization with IObservable and ConcurrentDictionary caching
- Custom LocalizedExtension for XAML: {loc:Localized Key}
- LocalizedViewModelBase for automatic language change handling
- Thread-safe caching with automatic invalidation on language change

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

## Project Structure
```
ZettaTrade/
├── src/
│   ├── ZettaTrade.App/              # Avalonia Application entry
│   ├── ZettaTrade.Core/             # Domain models, interfaces, events
│   ├── ZettaTrade.Infrastructure/   # Exchange adapters, storage, networking
│   ├── ZettaTrade.AI/               # MCP client/server, AI services
│   ├── ZettaTrade.UI/               # Shared UI components, themes
│   ├── ZettaTrade.Charting/         # SkiaSharp chart engine
│   ├── ZettaTrade.Strategies/       # Strategy engine + built-in strategies
│   └── ZettaTrade.Plugins/          # Plugin loader and SDK
├── tests/
│   ├── ZettaTrade.Core.Tests/
│   ├── ZettaTrade.Infrastructure.Tests/
│   └── ZettaTrade.AI.Tests/
├── docs/
├── samples/
└── ZettaTrade.sln
```

## Key Interfaces
```csharp
// Exchange connectivity
public interface IExchangeAdapter
{
    string Name { get; }
    Task ConnectAsync(CancellationToken ct);
    IAsyncEnumerable<Ticker> SubscribeTickers(IEnumerable<string> symbols, CancellationToken ct);
    IAsyncEnumerable<OrderBook> SubscribeOrderBook(string symbol, CancellationToken ct);
    Task<OrderResult> PlaceOrderAsync(OrderRequest order, CancellationToken ct);
    Task<Position[]> GetPositionsAsync(CancellationToken ct);
}

// AI/MCP integration
public interface IMcpClient
{
    Task<McpConnection> ConnectAsync(McpServerConfig config, CancellationToken ct);
    Task<ToolResult> CallToolAsync(string toolName, object parameters, CancellationToken ct);
    IAsyncEnumerable<ResourceUpdate> SubscribeResource(string uri, CancellationToken ct);
}

public interface ITradingAIService
{
    Task<AnalysisResult> AnalyzeMarketAsync(string symbol, TimeFrame tf, CancellationToken ct);
    Task<StrategySuggestion> SuggestStrategyAsync(MarketContext context, CancellationToken ct);
    IAsyncEnumerable<AIInsight> StreamInsightsAsync(AnalysisRequest request, CancellationToken ct);
}

// Strategy engine
public interface IStrategy
{
    string Name { get; }
    StrategyParameters Parameters { get; }
    Signal OnBar(Bar bar, IIndicatorContext context);
}

// Settings & Secrets
public interface ISettingsService
{
    Task<T> GetAsync<T>(string section, string key, T defaultValue);
    Task SetAsync<T>(string section, string key, T value);
    Task<string?> GetSecretAsync(string key);
    Task SetSecretAsync(string key, string value);
    Task RemoveSecretAsync(string key);
    IObservable<SettingsChange> Changes { get; }
    Task SaveAsync(CancellationToken ct = default);
}

public interface ISecretStore
{
    Task<string?> GetAsync(string key, CancellationToken ct = default);
    Task SetAsync(string key, string value, CancellationToken ct = default);
    Task RemoveAsync(string key, CancellationToken ct = default);
    Task<IReadOnlyList<string>> ListKeysAsync(string prefix, CancellationToken ct = default);
    Task<bool> IsAvailableAsync(CancellationToken ct = default);
}

// Localization & Theming
public interface IThemeManager
{
    string CurrentTheme { get; }
    IReadOnlyList<ThemeInfo> AvailableThemes { get; }
    Task ApplyThemeAsync(string themeName, CancellationToken ct = default);
    Task LoadCustomThemeAsync(string jsonPath, CancellationToken ct = default);
}
```

## MCP Server (presents as "Zetta")
- The MCP server identifies itself as "Zetta" for friendly AI interaction
- AI models see the app as a personal trading assistant named "Zetta"
- Server info: {"name": "Zetta", "version": "0.1.0"}

## MCP Server Tools (exposed by Zetta)
- `place_order` — Place a trading order
- `cancel_order` — Cancel pending order
- `get_positions` — Current open positions
- `get_balance` — Account balance info
- `get_market_data` — OHLCV data for symbol
- `get_order_book` — Current order book snapshot
- `get_indicators` — Calculated indicator values
- `set_alert` — Create price/indicator alert

## MCP Resources (exposed by Zetta)
- `zettatrade://positions` — Live positions stream
- `zettatrade://orders` — Active orders
- `zettatrade://pnl` — P&L tracking
- `zettatrade://market/{symbol}` — Market data feed

## When Making Changes
1. Always maintain backward compatibility in public interfaces
2. Add tests for new domain logic
3. Follow reactive patterns (Observable, not events)
4. Consider plugin boundary — new adapters should be separate assemblies
5. AI features should work with local models first, cloud as enhancement
6. Never hardcode exchange-specific logic in core
7. **All code, comments, and internal strings MUST be in English**
8. User-facing strings must use localization system (never hardcoded)
9. Sensitive data (API keys, tokens) MUST go through ISecretStore
10. Settings changes should be reactive (IObservable<SettingsChange>)
11. New features should have corresponding config sections
12. Platform-specific code must be isolated in Infrastructure layer
13. **Use ReactiveUI exclusively** — never mix with CommunityToolkit.Mvvm
14. All ViewModels must inherit from ReactiveObject
15. Use WhenActivated for subscription lifecycle management
16. Always use ObserveOn(RxApp.MainThreadScheduler) for UI updates
17. Dispose subscriptions properly with DisposeWith(disposables)

## Performance Targets
- Order book rendering: 60fps with 1000+ levels
- WebSocket message processing: < 1ms per message
- Chart with 100k candles: smooth pan/zoom
- Cold start to interactive: < 3 seconds
- Memory footprint: < 500MB typical usage