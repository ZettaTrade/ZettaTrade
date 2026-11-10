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
3. **Reactive** — UI and data flows use ReactiveUI exclusively (RxVoid, never System.Reactive.Unit)
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
