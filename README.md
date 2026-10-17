# ZettaTrade

Cross-platform desktop trading terminal for cryptocurrency and traditional stocks with deep AI/ML integration via MCP (Model Context Protocol).

## Tech Stack

- **.NET 10** / **C# 13**
- **Avalonia UI 11** — cross-platform XAML-based UI
- **ReactiveUI** — reactive MVVM
- **SkiaSharp** — custom chart rendering
- **MCP SDK** — AI integration protocol

## Features

- 📈 Multi-exchange trading (crypto + stocks)
- 🧠 AI-powered analysis via MCP (local & cloud models)
- 📊 Advanced charting with custom indicators
- ⚡ Visual strategy builder & backtesting
- 🔐 Platform-native secret storage (DPAPI / Keychain / libsecret)
- 🌍 Localization (en, ru, and more)
- 🎨 Customizable themes

## Project Structure

```
ZettaTrade/
├── src/
│   ├── ZettaTrade.Core/              # Domain layer (no dependencies)
│   ├── ZettaTrade.Infrastructure/    # Exchange adapters, settings, secrets
│   ├── ZettaTrade.AI/                # MCP client/server, AI services
│   ├── ZettaTrade.UI/                # Shared UI components, themes, i18n
│   └── ZettaTrade.App/               # Avalonia application entry point
├── tests/
├── docs/
└── ZettaTrade.sln
```

## Building

```bash
# Prerequisites: .NET 10 SDK
dotnet restore
dotnet build

# Run
dotnet run --project src/ZettaTrade.App

# Publish for specific platform
dotnet publish src/ZettaTrade.App -c Release -r win-x64 --self-contained
dotnet publish src/ZettaTrade.App -c Release -r osx-arm64 --self-contained
dotnet publish src/ZettaTrade.App -c Release -r linux-x64 --self-contained
```

## Configuration

Settings are stored in platform-specific locations:

| Platform | Config Path |
|----------|-------------|
| Windows  | `%APPDATA%/ZettaTrade/config/` |
| macOS    | `~/Library/Application Support/ZettaTrade/config/` |
| Linux    | `~/.config/ZettaTrade/config/` (XDG) |

Secrets (API keys, tokens) are stored in platform-native secure storage:
- **Windows**: DPAPI (ProtectedData)
- **macOS**: Keychain Services
- **Linux**: Secret Service API (libsecret) with encrypted file fallback

## Contributing

This is an open-source project. See [CONTRIBUTING.md](docs/contributing.md) for guidelines.

## License

MIT License — see [LICENSE](LICENSE) for details.
