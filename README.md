# ZettaTrade

Cross-platform desktop trading terminal for crypto market data, with local-first AI
integration over the Model Context Protocol ([MCP](https://modelcontextprotocol.io)).
Live market data comes from **Binance** and **Bybit** public endpoints, charts and the
order book render with SkiaSharp, and local data stays on the user's machine (SQLite via
EF Core, Serilog file logs, OS-secured secrets). Every order passes risk validation, and
high-risk operations ask for explicit human confirmation. The shipped trading engine is a
**paper trading** simulator — real funds are out of scope while the app is in alpha.

| Status | License | Platforms | Stack |
| --- | --- | --- | --- |
| **Alpha** (`0.1.0-alpha.NN`) | MIT | Windows · macOS · Linux | .NET 10 · Avalonia 12 · ReactiveUI · SkiaSharp |

**ZettaTrade is educational software, not investment advice** — see the full
[Disclaimer](#disclaimer) below before using it.

## Trading safety

ZettaTrade is built so that the shortest path to real money is a long one: the only
shipped trading backend is a paper-trading simulator, and market data comes from the
venues' public endpoints. Key hygiene however is a user responsibility, and the app is
run according to the non-negotiables below. See also
[`docs/SECURITY.md`](docs/SECURITY.md).

### Alpha status

**This is alpha software.** Behavior, settings keys and file formats can change between
alpha builds without notice; the status-bar version chip shows the exact build
(`0.1.0-alpha.NN`). Test on the sandboxes first and only then rely on the app — and only
for small, affordable, experiential use.

### Use user-scoped API keys only

Never recycle keys created for other tools, and never create a key broader than the job
you are doing:

- **Market-data setups:** create a **read-only** key — no trade, no withdrawal, no
  transfer permission.
- **Trading setups:** create a **trade-only** key — **withdrawal and transfer permissions
  must stay off** — tied to a dedicated account you can afford to experiment on.
- **IP restriction:** wherever the venue offers an IP allowlist, enable it and limit the
  key to your own addresses.

Your keys are yours: nothing in the ZettaTrade project (code, CI, maintainers) should
ever need to see them. If a "ZettaTrade" surface ever asks you to paste a production key
anywhere but the app's own storage, treat it as a red flag.

### Testnet first

Both shipped adapters take their addresses from the settings file, so moving to a testnet
is a config change, not a code change. The `Exchange` section of `appsettings.json` carries
one host pair per venue (REST base URL + WebSocket URL); the shipped defaults are the
production hosts, and an override is read once at startup — restart the app to apply it.

Example `appsettings.json` pointing both adapters at the venues' sandboxes (addresses
current at the time of writing — verify against the venues' own documentation):

```jsonc
{
  "Exchange": {
    "Binance": {
      "RestBaseUrl": "https://testnet.binance.vision",
      "WsUrl": "wss://stream.testnet.binance.vision:9443/stream"
    },
    "Bybit": {
      "RestBaseUrl": "https://api-testnet.bybit.com",
      "WsUrl": "wss://stream-testnet.bybit.com/v5/public/spot"
    }
  }
}
```

Settings file locations (see [Configuration](#configuration) for the full set):
`%APPDATA%\ZettaTrade\config\appsettings.json` (Windows),
`~/Library/Application Support/ZettaTrade/config/appsettings.json` (macOS),
`~/.config/ZettaTrade/config/appsettings.json` (Linux, XDG).

### Where secrets live

Secrets — API keys, tokens for external MCP servers — live only in the per-machine
`ISecretStore`, backed by the native OS store:

- **Windows:** DPAPI (user-scoped protected data);
- **macOS:** Keychain;
- **Linux:** Secret Service (libsecret/D-Bus), with an app-created encrypted file as the
  fallback when no keyring daemon is available.

API keys never belong in the repository, on a command line (process listings are
readable), or in a plain-text settings file — those files carry non-secret settings only.
When the app launches a child process that needs a secret (an external MCP server), the
secret is passed via stdin.

### Built-in risk controls

- Every order passes `IRiskManager` validation; rejected orders do not execute.
- Operations the risk check flags as high-risk require explicit human confirmation
  (`IConfirmationService`) — including orders triggered by an AI assistant through the
  MCP trading tools.
- The shipped trading backend is the **paper trading adapter**: simulated fills against
  live market prices, in-memory balances and positions (demo starting balance `100,000
  USDT`), resting limit orders stay pending until filled or cancelled — it never touches
  real funds or exchange keys.

## Disclaimer

> [!IMPORTANT]
> **ZettaTrade is educational software, not investment advice.** Nothing in this
> repository, its documentation or the application is a recommendation to buy, sell or
> hold any asset or instrument. Trading involves substantial risk of loss; you are
> responsible for your own decisions, your own testing and your own funds. Start on
> testnets and paper trading, verify the app's behavior yourself, and never risk money
> you cannot afford to lose.

## What's shipped

- **Market data** — Binance and Bybit read-only adapters (public REST history + WebSocket
  streams), configurable endpoint host pairs, a toolbar exchange switcher, live order
  book, candlestick charting with zoom/pan, indicator plugins (SMA ships first).
- **Trading surface (paper)** — New Order dialog and a confirmation dialog for high-risk
  operations, positions panel, persisted order history (SQLite via EF Core); all orders
  go through risk validation.
- **Settings and UI** — XDG-compliant configuration paths, runtime theme switching
  (built-in palettes and custom user themes), English/Russian localization with runtime
  language switching, hotkeys, status bar with the version chip.
- **AI and MCP** — built-in MCP server `Zetta` over stdio exposing
  `get_market_data`, `get_positions`, `get_balance`, `place_order`, `cancel_order`
  (trading tools go through the same risk and confirmation flows), an MCP client for
  external servers (stdio or Streamable HTTP) configured in `mcp.config.json`, and an AI
  chat answered by a **local Ollama model** (`AI.OllamaUrl`, `AI.Model` — cloud providers
  are not wired).
- **Local-first** — everything runs on the user's machine: local config, local data, file
  logs (Serilog never writes to stdout — the MCP protocol is the only thing allowed on
  stdout), secrets only in the OS secret store.

## Configuration

All paths follow XDG on Linux; per-user, per-machine:

| What | Path |
| --- | --- |
| Settings file | `%APPDATA%\ZettaTrade\config\appsettings.json` (Windows) · `~/Library/Application Support/ZettaTrade/config/appsettings.json` (macOS) · `~/.config/ZettaTrade/config/appsettings.json` (Linux) |
| Data (database, plugins) | `%LOCALAPPDATA%\ZettaTrade\data\` (Windows) · `~/Library/Application Support/ZettaTrade/data/` (macOS) · `~/.local/share/ZettaTrade/data/` (Linux) |
| Themes | `config/themes/` inside the settings directory |
| Logs | `~/.local/state/ZettaTrade/logs/` (Linux); under the data directory on Windows and macOS |

Non-secret settings keys used by the modules referenced in this README: `Exchange` →
`Binance`/`Bybit` (each with `RestBaseUrl`, `WsUrl`), `AI` → `OllamaUrl`, `Model`. MCP
servers the app connects to as a client are configured in `mcp.config.json` next to the
settings file.

## Build and run

Requires the .NET 10 SDK:

```bash
dotnet build ZettaTrade.slnx
dotnet run --project src/ZettaTrade.App
```

## Security

Vulnerability disclosure and the repository-side secret-scanning posture are documented
in [`docs/SECURITY.md`](docs/SECURITY.md).

## Documentation site

The documentation website sources live under `docs-site/` in this repository (Vite +
React, GitHub Pages deployment). Maintainer instructions from the original site README
are kept below, unchanged in substance.

### Deploy — Option 1: GitHub Actions (recommended)

Create `.github/workflows/deploy.yml`:

```yaml
name: Deploy to GitHub Pages

on:
  push:
    branches: [main]

permissions:
  contents: read
  pages: write
  id-token: write

jobs:
  deploy:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4

      - name: Setup Node
        uses: actions/setup-node@v4
        with:
          node-version: '20'

      - name: Install dependencies
        run: npm ci

      - name: Build
        run: npm run build

      - name: Setup Pages
        uses: actions/configure-pages@v4

      - name: Upload artifact
        uses: actions/upload-pages-artifact@v3
        with:
          path: dist

      - name: Deploy to GitHub Pages
        id: deployment
        uses: actions/deploy-pages@v4
```

Then enable GitHub Pages in repository settings (Settings → Pages → Source:
"GitHub Actions") and push to `main` — the documentation will auto-deploy.

### Deploy — Option 2: manual

```bash
cd docs-site
npm install
npm run build
# dist/ holds the static site; upload it to your gh-pages branch or hosting service
```

### Development (docs site)

```bash
cd docs-site
npm run dev      # dev server
npm run build    # production build
npm run preview  # preview the production build
```

The `docs-site/src/App.github.tsx` variant is optimized for GitHub Pages: no download
buttons, no skill-file tab, all documentation tabs plus a GitHub footer link (swap it in
as `src/App.tsx` before building, if needed).

## License

MIT — see [LICENSE](LICENSE). The standard MIT warranty disclaimer applies; the
[Disclaimer](#disclaimer) block above restates its spirit in plain words for trading
contexts.
