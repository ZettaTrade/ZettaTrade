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
- **NEVER create generic catch-all files** like Enums.cs, Entities.cs, Models.cs, Utils.cs
- **Split files by domain responsibility** — each file should have a clear, focused purpose
- **Use descriptive file names** that indicate the domain concept (OrderEnums.cs, not Enums.cs)
- **One entity per file** for complex entities (Order.cs, Position.cs, Trade.cs)
- **Group related small types** in one file only if they share the same domain concept (MarketData.cs for Bar, Ticker, OrderBook)

## Project Structure
```
ZettaTrade/
├── src/
│   ├── ZettaTrade.App/              # Avalonia Application entry
│   ├── ZettaTrade.Core/             # Domain models, interfaces, events
│   │   ├── Enums/
│   │   │   ├── OrderEnums.cs        # OrderSide, OrderType, TimeInForce, OrderStatus
│   │   │   ├── TimeFrame.cs         # TimeFrame enum
│   │   │   └── ExchangeEnums.cs     # AssetClass, ExchangeCapabilities
│   │   ├── Entities/
│   │   │   ├── Order.cs             # Order entity
│   │   │   ├── Position.cs          # Position entity
│   │   │   ├── Trade.cs             # User trade entity
│   │   │   ├── MarketTrade.cs       # Public market trade
│   │   │   └── MarketData.cs        # Bar, PriceLevel, OrderBook, Ticker
│   │   ├── ValueObjects/
│   │   │   ├── Symbol.cs            # Trading symbol with normalization
│   │   │   └── Money.cs             # Money, Price, Quantity
│   │   └── Interfaces/              # Core interfaces
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
// Market data adapter (read-only, no trading)
public interface IMarketDataAdapter : IAsyncDisposable
{
    string Id { get; }
    string Name { get; }
    bool IsConnected { get; }
    ExchangeCapabilities Capabilities { get; }
    Task ConnectAsync(CancellationToken ct);
    Task DisconnectAsync(CancellationToken ct);
    IAsyncEnumerable<Ticker> SubscribeTickersAsync(IEnumerable<Symbol> symbols, CancellationToken ct);
    IAsyncEnumerable<OrderBook> SubscribeOrderBookAsync(Symbol symbol, int depth, CancellationToken ct);
    IAsyncEnumerable<MarketTrade> SubscribeMarketTradesAsync(Symbol symbol, CancellationToken ct);
    Task<IReadOnlyList<Bar>> GetBarsAsync(Symbol symbol, TimeFrame tf, DateTimeOffset from, DateTimeOffset? to, int limit, CancellationToken ct);
}

// Trading adapter (extends market data with trading operations)
public interface ITradingAdapter : IMarketDataAdapter
{
    Task<Order> PlaceOrderAsync(OrderRequest request, CancellationToken ct);
    Task CancelOrderAsync(Symbol symbol, string orderId, CancellationToken ct);
    Task<IReadOnlyList<Order>> GetOpenOrdersAsync(CancellationToken ct);
    Task<IReadOnlyList<Position>> GetPositionsAsync(CancellationToken ct);
    Task<IReadOnlyDictionary<string, Money>> GetBalancesAsync(CancellationToken ct);
}

// Order request with validation
public sealed record OrderRequest
{
    public required Symbol Symbol { get; init; }
    public required OrderSide Side { get; init; }
    public required OrderType Type { get; init; }
    public required Quantity Quantity { get; init; }
    public Price? Price { get; init; } // Nullable for market orders
    public Price? StopPrice { get; init; }
    public TimeInForce TimeInForce { get; init; }
    public DateTimeOffset? Expiry { get; init; } // Required for GTD
    public string? ClientOrderId { get; init; }
    public bool ReduceOnly { get; init; }
    public void Validate(); // Validates Expiry for GTD, Price for limit orders, etc.
}

// Symbol with normalized and exchange-specific formats
public sealed record Symbol
{
    public string Normalized { get; } // "BTC/USDT" for display
    public string BaseAsset { get; }
    public string? QuoteAsset { get; }
    public AssetClass AssetClass { get; }
    public string GetExchangeFormat(string exchangeId); // "BTCUSDT" for Binance, "AAPL" for Alpaca
    public static Symbol Parse(string normalized);
    public static bool TryParseExchangeFormat(string exchangeSymbol, string exchangeId, out Symbol? symbol);
}

// Money with currency and InvariantCulture formatting
public readonly record struct Money(decimal Amount, string Currency)
{
    public static Money Zero(string currency);
    public override string ToString(); // Uses InvariantCulture
}

// Quantity with InvariantCulture formatting
public readonly record struct Quantity(decimal Value)
{
    public Money NotionalAt(Price price, string currency);
    public override string ToString(); // Uses InvariantCulture
}

// Price with operator overloads
public readonly record struct Price(decimal Value)
{
    public static Money operator *(Price price, Quantity quantity);
    public Money Multiply(Quantity quantity, string currency);
}

// Public market trade (no OrderId or Fee)
public sealed record MarketTrade
{
    public required string Id { get; init; }
    public required Symbol Symbol { get; init; }
    public required OrderSide Side { get; init; }
    public required Price Price { get; init; }
    public required Quantity Quantity { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
}

// AI/MCP integration with Streamable HTTP
public interface IMcpClient
{
    string Name { get; }
    bool IsConnected { get; }
    Task ConnectAsync(McpServerConfig config, CancellationToken ct);
    Task DisconnectAsync(CancellationToken ct);
    Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken ct);
    Task<McpToolResult> CallToolAsync(string toolName, JsonElement? parameters, CancellationToken ct);
    Task<IReadOnlyList<McpResourceInfo>> ListResourcesAsync(CancellationToken ct);
    Task<McpResourceContent> ReadResourceAsync(string uri, CancellationToken ct);
    IAsyncEnumerable<McpResourceUpdate> SubscribeResourceAsync(string uri, CancellationToken ct);
}

// MCP server host with IsRunning and Address
public interface IMcpServerHost
{
    bool IsRunning { get; }
    string? Address { get; }
    Task StartAsync(CancellationToken ct);
    Task StopAsync(CancellationToken ct);
    void RegisterTool(McpToolDefinition tool);
    void RegisterResource(McpResourceDefinition resource);
    void RegisterPrompt(McpPromptDefinition prompt);
}

// MCP tool definition with JsonElement for type-safe parameters
public sealed record McpToolDefinition
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required JsonElement InputSchema { get; init; }
    public required Func<JsonElement?, CancellationToken, Task<McpToolResult>> Handler { get; init; }
}

// MCP content with binary support (TextValue to avoid collision with CreateText method)
public sealed record McpContent
{
    public required string Type { get; init; } // "text", "image", "binary"
    public string? TextValue { get; init; }
    public byte[]? Data { get; init; }
    public string? MimeType { get; init; }
    public static McpContent CreateText(string text);
    public static McpContent CreateBinary(byte[] data, string mimeType);
}

// Settings (general configuration only)
public interface ISettingsService
{
    Task<T> GetAsync<T>(string section, string key, T defaultValue);
    Task SetAsync<T>(string section, string key, T value);
    IObservable<SettingsChange> Changes { get; }
    Task SaveAsync(CancellationToken ct);
    Task ReloadAsync(CancellationToken ct);
}

// Secrets (sensitive data - API keys, tokens, etc.)
public interface ISecretStore
{
    Task<string?> GetAsync(string key, CancellationToken ct);
    Task SetAsync(string key, string value, CancellationToken ct);
    Task RemoveAsync(string key, CancellationToken ct);
    Task<IReadOnlyList<string>> ListKeysAsync(string prefix, CancellationToken ct);
    Task<bool> IsAvailableAsync(CancellationToken ct);
}

// Localization & Theming
public interface IThemeManager
{
    string CurrentTheme { get; }
    IReadOnlyList<ThemeInfo> AvailableThemes { get; }
    Task ApplyThemeAsync(string themeName, CancellationToken ct);
    Task LoadCustomThemeAsync(string jsonPath, CancellationToken ct);
    IObservable<string> ThemeChanged { get; }
}

public sealed record ThemeInfo(
    string Name, 
    string DisplayName, 
    bool IsBuiltIn, 
    string[] PreviewColors,
    IReadOnlyDictionary<string, string>? Colors = null);

public interface ILocalizationManager
{
    string CurrentLanguage { get; }
    IReadOnlyList<LanguageInfo> AvailableLanguages { get; }
    Task SetLanguageAsync(string cultureCode, CancellationToken ct);
    string GetString(string key);
    string GetString(string key, params object[] args);
    IObservable<string> LanguageChanged { get; }
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
18. **Use async/await throughout** — avoid blocking calls (.Result, .Wait())
19. **NEVER use lock(_lockObject) in async code** — use SemaphoreSlim instead, even if there's no await inside the critical section
20. **CRITICAL: When updating scaffold files, ALWAYS update BOTH:**
    - Real files in scaffold/ directory (actual source code)
    - scaffoldData.ts (data for web interface export)
    - Never update only one — they must stay in sync!

## Trading Safety Rules (CRITICAL)
21. **NEVER execute trades without validation** — all orders must pass through IRiskManager
22. **NEVER default invalid values** — reject invalid symbol/side/type instead of substituting defaults
23. **ALWAYS require human confirmation** for high-risk operations (large orders, position changes)
24. **NEVER use stdio for MCP in GUI apps** — use HTTP with bearer token authentication
25. **NEVER write to stdout in MCP server** — breaks protocol; use proper logging to files only
26. **ALWAYS validate enum parsing** — return error for invalid values, don't silently default
27. **ALWAYS require symbol in cancel_order** — some exchanges (Binance) require it
28. **NEVER overwrite encrypted secrets on decryption failure** — preserve data integrity
29. **ALWAYS save secrets to disk immediately** — don't keep in memory only
30. **ALWAYS pass secrets via stdin** — never as command-line arguments (visible in process list)

## Performance Targets
- Order book rendering: 60fps with 1000+ levels
- WebSocket message processing: < 1ms per message
- Chart with 100k candles: smooth pan/zoom
- Cold start to interactive: < 3 seconds
- Memory footprint: < 500MB typical usage