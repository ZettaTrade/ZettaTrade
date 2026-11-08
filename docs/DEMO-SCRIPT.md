# ZettaTrade demo video: the storyboard and the shot list (T-128)

The tracked deliverable of T-128: a shot-by-shot storyboard for the demo video (the board windows it at 90-180 s, this script budgets 150 s), the narration draft, the recording instructions and the honesty ledger. The recording itself is a human task; this script is written so one session work window can produce the video from it. The final video file and its hosted link are referenced from the product landing once T-126 lands - the landing is queued, so no README or docs links happen at this step.

Two standing rules the script inherits:

- Every feature named on camera exists on the current develop tree (develop@0823719). No vaporware: the beats the board asked for that are not shipped yet stay explicitly marked placeholders - never filmed, never implied (the sibling T-126 canon).
- In-repo text is English (the house localization canon): the narration draft here is English, and the Russian narration is translated from it by the human at recording time as a recording artifact - the ru draft does not enter the repository.

## The standing security rule (the one absolute)

No real keys on screen ever. The video films demo data and the paper lane only: no account pages, no API-key dialogs, no secret-store contents in frame. Secrets live in the app's secret store and never on camera; market data needs no keys at all. The only future exception is the T-127 insert over an exchange TESTNET account - testnet credentials only, the on-screen badge must read Testnet.

## The duration budget

- Board window: 90-180 s. Budgeted footage total: 150 s.
- Per-shot seconds: S1 8 | S2 14 | S3 12 | S4 22 | S5 12 | S6 26 | S7 14 | S8 16 | S9 10 | S10 16 = 150 s.
- Hard cap 180 s. Overrun is fixed by dropping whole beats, in the drop order S9, then S5, then S3 - never by speeding the narration.
- The T-127 testnet insert (+8 s) is NOT in the budget until T-127 lands.

## The shot list

| Shot | Seconds | On screen (the UI state) | Action | Narration (English draft) |
|------|---------|--------------------------|--------|---------------------------|
| S1 | 8 | The shell at rest in the Dark theme: candles, the order book ladder, the trade tape, the watchlist; the status bar carries the exchange name, the Paper chip, the adapter health dots and the version. | Let the data stream; the cursor idles. | "Trading terminals are usually closed-source web apps or chart toys. ZettaTrade is a desktop terminal that runs locally - open source, MIT licensed." |
| S2 | 14 | The full workspace, left to right: candles with the VWAP and volume-profile overlays, the OHLC crosshair readout, the book ladder with the cumulative depth chart, the trade tape, the watchlist sparklines; the draggable splitters. | Pan across the workspace; hover one candle for the OHLC readout; drag a splitter a little. | "One screen carries the whole workspace: chart overlays, the order book with its cumulative depth, the tape and a streaming watchlist. The splitters are draggable and the layout persists between sessions." |
| S3 | 12 | The Ctrl+K symbol palette overlay over the workspace. | Press Ctrl+K, type a symbol, Enter to apply; the chart, book, tape and watchlist follow it. | "Ctrl+K opens the symbol palette across every registered adapter, favorites and recents included. The whole workspace follows the applied symbol." |
| S4 | 22 | The AI assistant column: the market-context consent toggle at the top, the messages list, the input (Enter sends). | Toggle the market-context consent ON, ask one question about the on-screen state, Enter; wait for the answer to land. | "The assistant answers with on-screen awareness: one consent toggle feeds it a token-bounded snapshot of the live workspace. It runs on a local model first - Ollama; the cloud stays optional." |
| S5 | 12 | The same workspace; an MCP client configured in the settings window drives the terminal's MCP surface. | Place one paper order through the MCP surface; open order history and positions to show the fill. | "The terminal is drivable by tools: an MCP client places a paper order and the fill lands in the same journal the panels and the PnL engine read - exactly like a trade placed from the UI." |
| S6 | 26 | The ticket flow over the paper lane; the status bar chip reads Paper throughout. | Click a book level (the ticket opens prefilled), size the order with the position sizer, check the fees, submit; the confirmation dialog shows the summary; confirm. The working-order marker, then the entry line with the PnL tag appear on the candles; order history gains the row. | "Placing an order is a click-through path: a book level prefills the ticket, fees and the pre-submit summary are visible, and the risk checks run on the way out. This is the paper lane: the simulated fill lands in history, positions and PnL, and the chart draws my own footprint." |
| S7 | 14 | The balances window (per adapter and aggregated), the order history window, the statistics dashboard: win rate, profit factor, equity curve. | Walk the Trading menu top to bottom; land on the statistics curves. | "The accounting is local: balances, the order journal and a statistics dashboard with win rate, profit factor and the equity curve, all computed from the ledger on disk." |
| S8 | 16 | The alerts window, then the toast stack top-right and the notification history. | Create a price alert at a tight threshold; let the Demo walk cross it; the toast lands; click it to open the notification history. | "Price alerts watch the stream and fire into the notification center: a toast lands, the history keeps every notification, and do-not-disturb is one toggle away." |
| S9 | 10 | The settings window over the workspace. | Flip the theme Dark -> Light and the language en -> ru, close; the whole shell re-inks live. | "Themes are JSON files with hot reload and the language flips live - the whole shell re-inks without a restart." |
| S10 | 16 | Back in Dark; the disclaimer dialog from Help -> About / Disclaimer; then the workspace at rest under the closing narration. | Show the disclaimer briefly; the closing card names the repository and the pending Release page. | "ZettaTrade is alpha: local first, no telemetry, and this video used demo data and a paper account only - no real keys were ever on screen. The source is open today; the download CTA (pending the release pipeline landing) points at the Releases page once it exists." |

The moments the board named but the current tree does not have:

- S6 insert, blocked on T-127 (the testnet trading loop is queued): when T-127 lands, film the same walk over a Bybit TESTNET account (+8 s; the chip and every surfaced order state read Testnet) and extend the narration with one sentence about the exchange testnet. The placeholder is never filmed before that landing; the total re-cuts to 158 s.
- S10 CTA, T-125 and T-126 queued: the "download CTA (pending the release pipeline landing)" is the standing wording until the delivery pipeline ships; then the narration points at the repository's Releases page (github.com/ZettaTrade/ZettaTrade/releases) and the landing (T-126) carries the video link.
- Optional S3 variant: switch the market-data picker to Bybit (real public feeds, read-only - shipped as T-023) for one pan over real market data. Only if the network stays stable on camera; the narration must keep market data and the traded lane apart - the chip reflects the TRADED surface's lane, not the data source.

## The recording instructions (the operator checklist)

- Screen: deliver at 1920x1080 (capture higher if the tool allows), 100% UI scale, 30 fps or better; keep the window maximized for the whole video.
- Box budget: cold start under the project's 3 s target, the book stays fluid (the 60 fps / 1000+ levels target), typical memory under 500 MB. If the box stutters, trim the fast beats (S2 first) instead of filming lag. The perf HUD is not shipped (T-108 queued): no meters on screen and none faked.
- Theme and language: start and end on the baked Dark theme; the only on-camera flips are S9's. Keep the UI text in English for every shot except the S9 flip.
- Data and lane: the Demo adapter for every beat (offline, deterministic seeded walk) on the paper lane; the chip must read Paper in every trading shot (Testnet is reserved for the T-127 insert when it exists; Live never).
- AI backend: S4 and S5 need a working local backend (Ollama + a pulled model). If the box cannot run one, drop S4 and S5 rather than film a broken or cloud-backed chat.
- Alerts: the Demo walk is seeded, so tune a trigger the stream crosses inside the take - test the threshold before rolling.
- Quiet set: dismiss or block every other app's notifications; the app's own do-not-disturb stays OFF for S8 (it would hide the toast).
- Narration: read this file's English draft as-is, or translate it to Russian at recording time; the ru narration is a recording artifact and is not tracked in the repository.
- Take discipline: one beat per take, cut on the still frame. The first-run disclaimer (T-119) is already acknowledged on the recording install; S10 re-shows it from Help -> About / Disclaimer deliberately.

## The honesty ledger (beats kept, placeholder, dropped)

- Kept as live beats (all shipped on develop@0823719): shell chrome and the status bar (T-065/T-066/T-098/T-123), charting (T-026/T-028/T-033/T-035/T-036/T-037/T-089/T-092/T-093), book and depth (T-011/T-072/T-088), tape (T-087), watchlist (T-120), palette and favorites (T-095/T-039), AI chat with the live context (T-027/T-032/T-114), MCP-driven paper fills (T-040/T-068/T-133), the paper-lane order walk (T-008/T-025/T-051/T-057/T-078/T-079/T-080/T-122), portfolio and statistics (T-010/T-081/T-082/T-083), alerts and toasts (T-096/T-097), themes and the live language switch (T-001 through T-006), the disclaimer (T-119).
- Kept as explicitly marked placeholders (doors noted, never filmed): the testnet limit-order lifecycle insert under S6 (T-127, queued) and the download CTA pointing at the Releases page in S10 (T-125, queued; the landing reference is T-126's, also queued).
- Dropped: the Russian narration as in-repo text (the localization canon outranks the board's "en and ru" detail - the ru draft is the human recording artifact); the perf HUD on camera (T-108, queued); multi-window workspaces (T-103, queued). The board text hedged the alerts beat as possibly unshipped - on the current tree both T-096 and T-097 are done, so the alert beat is live, not a placeholder.

## What stays out of this task's scope

- The recording is a human task; this script is the tracked deliverable of T-128 and is executable inside one session work window.
- The final video file, its hosting and the landing reference belong to T-126. The README, docs/SECURITY.md and docs/USER-MANUAL.md are owned by other workers and are not touched here.
- No version artifacts ride this docs-only task: no alpha bump, no CHANGELOG section.
