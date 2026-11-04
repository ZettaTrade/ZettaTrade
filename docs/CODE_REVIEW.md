# ZettaTrade — Code Review Findings

Date: 2026-11-04
Scope: full `src/` tree (App, Core, Infrastructure, UI, Charting, AI, Plugins), read-only review.
Method: four parallel review passes (Exchange adapters, Infrastructure, App, UI/AI/Charting),
followed by line-by-line manual verification of every critical and major finding; most minor
findings were also verified. Every entry below has been confirmed against the code at the
cited lines, except the one marked "suspected".

Findings: **56 total — 3 Critical, 23 Major, 30 Minor (1 suspected).**

Each finding has a stable ID (`BUG-001` … `BUG-056`) so it can be referenced directly in work
items. Status of every finding: **Open**.

Severity:
- **Critical** — data loss, protocol hang, or a broken core pipeline requiring immediate attention.
- **Major** — silent failures, wrong trading data, leaked resources, or rule violations with real exposure.
- **Minor** — edge-case defects, low-impact races, UI staleness, contract violations without immediate harm.

---

## Critical

### BUG-001 [Critical] Secrets — DPAPI store wipes all secrets after a silent load failure
- File: `src/ZettaTrade.Infrastructure/Secrets/WindowsDpapiSecretStore.cs:128-131` (witness lines 125, 147)
- `LoadStoreAsync` swallows every load failure (corrupt JSON, invalid base64 from `Convert.FromBase64String` at line 125, transient read error) and continues with an empty/partial `_cache`. The very next `SetAsync` → `SaveStoreAsync` (line 147) rewrites the whole file from that cache, permanently destroying all previously stored secrets — exactly the "never overwrite encrypted secrets when decryption fails" scenario. `InitializeAsync` never surfaces the failure.

### BUG-002 [Critical] MCP — server drops every response for numeric JSON-RPC ids
- File: `src/ZettaTrade.AI/Mcp/McpServerHost.cs:204` (witness lines 217, 230)
- `idP.GetString()` returns null for a numeric JSON-RPC id (standard MCP clients send `"id": 1, 2, …`), so the `id != null` gate at line 217 drops every response. Combined with line 230 hardcoding `id = "1"` in the initialize reply, the server hangs for any client that numbers its requests.

### BUG-003 [Critical] Exchange/Bybit — post-reconnect resubscribe is cancelled for all but the last symbol
- File: `src/ZettaTrade.Infrastructure/Exchange/BybitAdapter.cs:160-174` (witness lines 441-444, 663)
- The resubscribe pipeline is `_resubscribeStreams.Select(...).Switch()`: a new emission disposes (cancels) the previous in-flight inner subscription. `HandleConnectionStateChanged` emits all tracked symbols synchronously in one `foreach` with no await, so with ≥2 tracked symbols all resubscribe sends except the last are cancelled mid-flight; cancelling a pending `ClientWebSocket.SendAsync` aborts the whole socket, which re-triggers reconnect → reconnect churn with no recovery. The same flaw exists on the gap-recovery path (line 663): a new gap frame kills an in-flight resubscribe.

## Major

### BUG-004 [Major] Exchange/Binance — feed stays dead forever after any WebSocket reconnect
- Files: `src/ZettaTrade.Infrastructure/Exchange/BinanceAdapter.cs:461-465`, `src/ZettaTrade.Infrastructure/Exchange/WebSocketClient.cs:203-240`
- The background loop silently reconnects the socket, but nothing ever re-sends the `SUBSCRIBE` frames (`HandleConnectionStateChanged` only logs; stream starts run only on new Rx outer subscriptions). After any transient disconnect the ticker/depth/trade feeds of all subscribed symbols go silent forever while the Rx subscriptions stay alive. Aggravation: a subscriber disposing mid-start cancels the start's send token, aborting the shared socket and making the feed unrecoverable.

### BUG-005 [Major] Exchange/Paper — average entry price corrupted on partial closes
- File: `src/ZettaTrade.Infrastructure/Exchange/PaperTradingAdapter.cs:389-404`
- The net-position branch condition treats any same-sign fill as "extending", so a *reducing* fill is re-averaged with the wrong denominator: long 1 @100, sell 0.5 @150 → entry becomes (100 + 75) / 0.5 = 350 for the remaining 0.5 long (it should stay 100). Corrupts average entry and unrealized PnL on every partial close.

### BUG-006 [Major] Exchange/Paper — ~24h-stale reference price on ascending-order backends
- File: `src/ZettaTrade.Infrastructure/Exchange/PaperTradingAdapter.cs:293-306` (witness: `BinanceAdapter.cs:250-288`)
- Reference price uses `GetBarsAsync(from: now-24h, limit: 1)` and takes `bars[^1].Close`. On ascending-order backends (Binance klines are oldest-first) `limit: 1` returns the single bar at `from`, i.e. a ~24-hour-old close, so paper market fills and limit-crossing decisions use a day-old price. Bybit/Demo return the latest bar, so behavior silently differs per backend.

### BUG-007 [Major] AI/MCP tools — numeric/undefined enums accepted for side and order type
- Files: `src/ZettaTrade.AI/Mcp/Tools/TradingToolsRegistrar.cs:113, 117`; `src/ZettaTrade.Core/Entities/OrderRequest.cs:53-69`
- `Enum.TryParse<OrderSide>/<OrderType>` accepts numeric and undefined values (`"999"` parses to `(OrderSide)999`), and `OrderRequest.Validate` never checks enum defined-ness, so an invalid side/type flows on as valid toward execution.

### BUG-008 [Major] AI/MCP tools — unparsable timeInForce/timeframe silently defaulted
- File: `src/ZettaTrade.AI/Mcp/Tools/TradingToolsRegistrar.cs:128, 352`
- An unparsable `timeInForce` silently defaults to `GTC`; an unparsable `timeframe` silently defaults to `H1` (the model asks for M1 and gets H1 candles). Violates the "never substitute defaults for unparsable enums" trading-safety rule.

### BUG-009 [Major] Secrets — macOS keychain exposes the secret as a command-line argument
- File: `src/ZettaTrade.Infrastructure/Secrets/MacOSKeychainSecretStore.cs:57-61`
- The secret value is passed as a process argument (`"-w", value`), exposing it in the process list (`ps`), violating the "pass secrets via stdin, never as command-line arguments" rule. The comment on line 57 claiming `-w` doesn't support stdin is factually wrong (`security add-generic-password -w` without a value does read stdin).

### BUG-010 [Major] Secrets — DPAPI store silently claims success on failed writes
- File: `src/ZettaTrade.Infrastructure/Secrets/WindowsDpapiSecretStore.cs:134-158`
- `SaveStoreAsync` catches and swallows all write errors while `SetAsync`/`RemoveAsync` report success: a failed disk write leaves the secret memory-only (gone after restart) and a removal is silently undone (the key reappears after restart).

### BUG-011 [Major] Secrets — synchronous blocking wait inside an async contract method
- File: `src/ZettaTrade.Infrastructure/Secrets/WindowsDpapiSecretStore.cs:97-109`
- `ListKeysAsync` blocks synchronously via `_lock.Wait(ct)`: if `SetAsync` holds the semaphore awaiting file I/O, this freezes or deadlocks the calling (UI) thread.

### BUG-012 [Major] Settings — hot reload races `SetAsync` and erases just-written values
- File: `src/ZettaTrade.Infrastructure/Configuration/SettingsService.cs:186-190`
- `LoadFromFileAsync` mutates the shared `_settings` (`Clear()` + repopulate) without acquiring `_saveLock`; a debounced external reload interleaved with `SetAsync`/`SaveAsync` erases just-written values from memory and the next save persists the stale snapshot (lost writes).

### BUG-013 [Major] Settings — hot reload never publishes `SettingsChange`
- File: `src/ZettaTrade.Infrastructure/Configuration/SettingsService.cs:159-201`
- External reload replaces values but never calls `_changes.OnNext`, so all `Changes` subscribers keep operating on stale values; the promised live hot-reload behavior never propagates (consumers: `ChatViewModel.cs:113-124`, `ChartViewModel` restore paths).

### BUG-014 [Major] Indicators — plugin scan aborts on a plugin with missing dependencies
- File: `src/ZettaTrade.Infrastructure/Indicators/PluginIndicatorCatalog.cs:117-127`
- `assembly.GetTypes()` is called outside the try (which only wraps `LoadFrom`): a plugin DLL whose dependencies are missing throws `ReflectionTypeLoadException` out of the catalog scan, instead of the documented behavior of skipping the file.

### BUG-015 [Major] App — persisted 12-hour-clock setting not applied at startup in the common case
- File: `src/ZettaTrade.App/ViewModels/ChartViewModel.cs:546-568`
- `InitializeAsync` restores the persisted clock setting only at the tail of the time-frame restore path; the early returns at lines 532, 543 and 548 (no stored TF, unparsable TF, restored == default H1) skip it. When the persisted time frame is the default `H1` (the common case), the persisted `UI.ChartUse12HourClock` is never applied and the chart silently reverts to the 24-hour clock on every restart.

### BUG-016 [Major] App — symbols bootstrap race between exchange switches
- File: `src/ZettaTrade.App/ViewModels/ChartViewModel.cs:656-690` (witness lines 738-745)
- `BootstrapSymbols` has no epoch/guard: every `AdapterChanges` emission starts an independent fetch whose last-completed delivery wins `AvailableSymbols`/`SelectedSymbol`. Two rapid exchange switches deliver out of order and the old exchange's symbol list and selection clobber the new exchange's; a user symbol change made during the fetch is overwritten by the stale persisted value read at line 740.

### BUG-017 [Major] App — order placement fails silently on network exceptions
- File: `src/ZettaTrade.App/ViewModels/NewOrderDialogViewModel.cs:503-530`
- Only `InvalidOperationException` is caught, but `PlaceOrderAsync` can throw network exceptions (`HttpRequestException`, `TaskCanceledException`) via the paper adapter's reference-bar REST call. They escape into `ReactiveCommand.ThrownExceptions`, which nothing observes: the order is silently not placed, with no error message and no journal entry.

### BUG-018 [Major] App — UI-bound property updated off the UI thread from a timer
- File: `src/ZettaTrade.App/ViewModels/StatusBarViewModel.cs:281-285` (witness lines 304-307)
- The fallback-notice `Observable.Timer(...)` callback runs on a ThreadPool timer thread and sets the UI-bound `ConnectionStatus` without `ObserveOnUiThread`, violating the marshaling discipline used everywhere else. Reached whenever the persisted exchange id is unregistered at startup.

### BUG-019 [Major] App — unlogged startup crash: `async void` without try/catch
- File: `src/ZettaTrade.App/App.axaml.cs:30-36`
- `OnFrameworkInitializationCompleted` is `async void` with no try/catch: any exception in `BuildServiceProviderAsync` or `ApplyStoredUiSettingsAsync` (e.g. a corrupted SQLite store or theme failure) crashes the process before the window exists, with nothing written to the Serilog file log.

### BUG-020 [Major] MCP — registration/deadlock stall while a tool call holds the server lock
- File: `src/ZettaTrade.AI/Mcp/McpServerHost.cs:258-285` (witness lines 136-176)
- `_lock` is held across `await tool.Handler(...)` (which may await a human confirmation dialog), while `RegisterTool`/`RegisterResource`/`RegisterPrompt` and `tools/list` block synchronously on `_lock.Wait()`: a UI-thread registration stalls until the tool call completes; deadlock risk.

### BUG-021 [Major] MCP — orphaned stderr-drain task and a fillable stderr pipe
- File: `src/ZettaTrade.AI/Mcp/McpClientService.cs:198-202`
- The stderr drain runs as a fire-and-forget `_ = Task.Run(...)` bound to the connect-time token: its exceptions are unobserved (e.g. `ObjectDisposedException` after `DisconnectAsync`), and once the task dies the child's stderr pipe can fill and block the server process (zombie/protocol deadlock).

### BUG-022 [Major] MCP — zombie child process on reconnect; crash in `DisposeAsync`
- Files: `src/ZettaTrade.AI/Mcp/McpClientService.cs:37-61, 174-196, 157-167`
- Reconnecting while connected overwrites `_serverProcess`/`_httpClient` without killing the previous child (leaked zombie process). `DisposeAsync` calls `Kill` on a process that never started (when `Start()` at line 196 threw after the field was assigned at 174) → `InvalidOperationException` thrown out of disposal.

### BUG-023 [Major] MCP — stdio replies not matched to request ids; stream desynchronization
- File: `src/ZettaTrade.AI/Mcp/McpClientService.cs:96-123`
- The stdio reply is never matched to the request id (a random Guid at line 96) and any interleaved server line (notification/log) desynchronizes the stream: `GetProperty("result")` throws out of `CallToolAsync`, and a cancelled `ReadLineAsync(ct)` leaves a half-consumed line corrupting all subsequent reads.

### BUG-024 [Major] MCP — HTTP auth uses a hardcoded placeholder token instead of the secret store
- File: `src/ZettaTrade.AI/Mcp/McpClientService.cs:218-222`
- `AuthSecretRef` is resolved to a hardcoded `"placeholder-token"` Bearer header instead of the secret store: HTTP-transport authentication never works, and secrets bypass `ISecretStore`.

### BUG-025 [Major] UI — async subscription helper runs tasks concurrently against its contract
- File: `src/ZettaTrade.UI/Extensions/ObservableAsyncExtensions.cs:29-38`
- The documented contract says notifications run "serialized in emission order", but the implementation `.Select(...).Merge()` runs the per-notification tasks concurrently: dependent steps (e.g. serialized page fetches) interleave.

### BUG-026 [Major] Risk — market/stop orders bypass the hard order-value cap
- File: `src/ZettaTrade.App/Services/DefaultRiskManager.cs:36-46`
- Orders without an explicit price (market orders and stop orders whose value cannot be estimated) return `Approved(requiresConfirmation: true)` without ever being checked against `MaxOrderValueHardCapUsd`, bypassing the cap that the class's own documentation (lines 18-22) declares "must never be bypassable by any caller". The only barrier for an order of any size is the confirmation dialog.

## Minor

### BUG-027 [Minor] Exchange/Bybit — trade-frame parser throws on non-object entries
- File: `src/ZettaTrade.Infrastructure/Exchange/BybitAdapter.cs:693-701`
- `TryGetProperty("S")` is called before the `ValueKind != Object` check: a non-object array entry throws `InvalidOperationException` and aborts all remaining trades of the frame instead of being counted as "dropped" (contradicting the method's own doc).

### BUG-028 [Minor] Exchange/Binance — ungated subscribe/unsubscribe send
- File: `src/ZettaTrade.Infrastructure/Exchange/BinanceAdapter.cs:293-314` (witness line 175)
- `SubscribeSymbolAsync`/`UnsubscribeSymbolAsync` send without the `_streamStartGate` used by `StartSymbolStreamsAsync`: a concurrent ungated unsubscribe + gated subscribe produce two simultaneous `ClientWebSocket.SendAsync` calls on one socket → `InvalidOperationException` ("there is already one outstanding send").

### BUG-029 [Minor] Exchange — WebSocketClient state races on overlapping connects
- File: `src/ZettaTrade.Infrastructure/Exchange/WebSocketClient.cs:82-88` (witness line 192)
- `ConnectAsync` mutates the shared `_cts` (`Cancel`/`Dispose`/replace) with no synchronization: overlapping calls corrupt the session token (leaked CTS, or the old loop's `Task.Delay` dies as `ObjectDisposedException`). `_reconnectAttempts` is only reset on successful open (line 192), so a failed previous session shrinks the next session's attempt budget.

### BUG-030 [Minor] Exchange — DisconnectAsync causes a spurious "background loop crashed"
- File: `src/ZettaTrade.Infrastructure/Exchange/WebSocketClient.cs:148` (witness lines 284-292, 235-239)
- `DisconnectAsync` disposes `_webSocket` while the receive loop may be blocked in `ReceiveAsync`; the resulting `ObjectDisposedException` matches none of the receive loop's inner catches and escapes as a bogus "background loop crashed" error.

### BUG-031 [Minor] Demo — racy trade sequence counter
- File: `src/ZettaTrade.Infrastructure/Demo/DemoMarketDataAdapter.cs:207-223`
- `++_tradeSequence` runs on concurrent per-symbol timer threads without synchronization → lost increments, duplicate `demo-…-{sequence}` trade ids, and parity flips of side/price.

### BUG-032 [Minor] Exchange — order book depth parameter silently ignored
- File: `src/ZettaTrade.Infrastructure/Exchange/ExchangeAdapterBase.cs:85-89`
- `SubscribeOrderBook(symbol, depth)` silently ignores `depth`: Binance always streams its pinned 20 levels, Bybit 50; a caller requesting depth 5 or 100 gets the fixed depth with no error.

### BUG-033 [Minor] Exchange/Paper — phantom Filled order on balance rejection
- File: `src/ZettaTrade.Infrastructure/Exchange/PaperTradingAdapter.cs:184-186` (witness lines 362-365)
- `FillAndApply` records the Filled order into `_orders` before the balance validation inside `ApplyFillToPortfolio`: an order rejected for insufficient funds leaves a phantom Filled order in the store while the caller receives an exception.

### BUG-034 [Minor] Exchange/Paper — new position inherits the old position's open time
- File: `src/ZettaTrade.Infrastructure/Exchange/PaperTradingAdapter.cs:409-416`
- The crossing-zero branch opens a fresh opposite position at the fill price but never resets `OpenedAt`, so the new position carries the closed position's timestamp.

### BUG-035 [Minor] Exchange/Paper — cancel cannot be aborted by caller's cancellation
- File: `src/ZettaTrade.Infrastructure/Exchange/PaperTradingAdapter.cs:200-210`
- `CancelOrderCoreAsync` waits on `_tradingLock.WaitAsync()` without the caller's `ct`: a wedged lock holder blocks cancellation indefinitely.

### BUG-036 [Minor] Secrets — ListKeysAsync always empty on macOS and Linux stores
- Files: `src/ZettaTrade.Infrastructure/Secrets/MacOSKeychainSecretStore.cs:87-90`, `src/ZettaTrade.Infrastructure/Secrets/LinuxSecretServiceStore.cs:103-106`
- `ISecretStore.ListKeysAsync` ("list all keys matching a prefix") silently reports no stored secrets on both platforms.

### BUG-037 [Minor] Secrets — child processes not killed on timeout
- Files: `src/ZettaTrade.Infrastructure/Secrets/MacOSKeychainSecretStore.cs:121-145`, `src/ZettaTrade.Infrastructure/Secrets/LinuxSecretServiceStore.cs:120-152`
- On timeout/cancellation (`WaitForExitAsync(cts.Token)` throws) the `security`/`secret-tool` child process is never killed (`using Process` disposes the handle, not the process), leaving an orphan that may hold an open keychain prompt/lock.

### BUG-038 [Minor] Secrets — non-atomic destroy-then-create on macOS keychain update
- File: `src/ZettaTrade.Infrastructure/Secrets/MacOSKeychainSecretStore.cs:48-55`
- `SetAsync` deletes the existing item before adding the new one: if the add fails or the process dies in between, the old secret is lost. The empty `catch { }` at line 55 swallows real keychain errors, not just "not found".

### BUG-039 [Minor] Secrets — DPAPI store initialization runs without the lock
- File: `src/ZettaTrade.Infrastructure/Secrets/WindowsDpapiSecretStore.cs:37-40` (witness 114-132)
- `InitializeAsync` → `LoadStoreAsync` reassigns `_cache` without holding `_lock`: a concurrent early `SetAsync` can be clobbered and then erased from disk by the next save.

### BUG-040 [Minor] Secrets — decryption failure indistinguishable from "not found"
- File: `src/ZettaTrade.Infrastructure/Secrets/WindowsDpapiSecretStore.cs:50-59`
- `GetAsync` returns null on any decryption failure, conflating "corrupt/unreadable secret" with "missing key" (the interface only promises null for missing keys), hiding corruption from the caller.

### BUG-041 [Minor] Secrets — truncated encrypted file falls back to overwrite
- File: `src/ZettaTrade.Infrastructure/Secrets/EncryptedFileSecretStore.cs:176-191` (witness 254-256)
- On a too-short/truncated file the loader silently "starts fresh" and `_initialized` becomes true, so the next `SetAsync` overwrites the undecryptable file instead of failing (contradicts the "never overwrite on decryption failure" rule, though the content is unrecoverable either way).

### BUG-042 [Minor] Persistence — unconditional cancel rewrites terminal order history
- File: `src/ZettaTrade.Infrastructure/Persistence/SqliteOrderHistoryStore.cs:93-110`
- `MarkCancelledAsync` unconditionally sets `Status = Cancelled`: a late/duplicate cancel for an order already `Filled`/`Rejected`/`Expired` rewrites the journal history instead of being ignored.

### BUG-043 [Minor] Persistence — check-then-act upsert fails under concurrency
- File: `src/ZettaTrade.Infrastructure/Persistence/SqliteOrderHistoryStore.cs:65-79`
- The upsert is `FindAsync` then `Add` without a real DB upsert: two concurrent `RecordPlacedAsync` calls for the same order both see null and the second `SaveChangesAsync` fails with a UNIQUE-constraint error instead of updating.

### BUG-044 [Minor] Config — empty XDG environment variables break path resolution
- File: `src/ZettaTrade.Infrastructure/Configuration/ConfigPathResolver.cs:71-73, 82-84, 91-93`
- `XDG_CONFIG_HOME`/`XDG_DATA_HOME`/`XDG_STATE_HOME` set to an empty string fail the `??` fallback (only null falls back), producing CWD-relative paths; the XDG spec requires the default when the variable is empty.

### BUG-045 [Minor] Settings — mixed-type change payload; self-triggering reload window
- File: `src/ZettaTrade.Infrastructure/Configuration/SettingsService.cs:97-101, 222`
- `SettingsChange.OldValue` is a boxed `JsonElement?` while `NewValue` is the typed `T`, so subscribers comparing old/new always mis-handle one side. The `_isSaving` guard resets in `finally` before the `File.Move` event is delivered, so every save can self-trigger a reload and widen the lost-write race (BUG-012).

### BUG-046 [Minor] Themes — hot-reload misses files and silently dies
- File: `src/ZettaTrade.UI/Themes/ThemeFileWatcher.cs:54-55` (witness lines 92-96)
- One global `Throttle` collapses a burst spanning different theme files into a single event: only the last-changed file reloads; others changed in the same 150 ms window are never reloaded. The FSW `Error` handler only logs: after an internal-buffer overflow the watcher stops raising events and hot-reload silently dies.

### BUG-047 [Minor] Themes — thread-unsafe live list exposed as `AvailableThemes`
- File: `src/ZettaTrade.UI/Themes/ThemeManager.cs:125` (witness lines 300-308)
- `AvailableThemes` returns the live `List<ThemeInfo>` that the background hot-reload pipeline mutates under `_lock`: an unsynchronized consumer (e.g. `StatusBarViewModel.cs:315-318`) enumerating it throws `InvalidOperationException`.

### BUG-048 [Minor] Charting — pan pacing mismatch with painted bar width
- File: `src/ZettaTrade.UI/Charting/CandlestickChart.cs:1195` (witness lines 614-634)
- Pan pacing computes the bar pitch from `Bounds.Width` while the draw pass uses `plotRect.Width` (bounds minus the measured price gutter): dragging "one bar" moves the window by a different distance than the painted bar width.

### BUG-049 [Minor] Charting — undisposed `SKPath` per redraw
- File: `src/ZettaTrade.UI/Charting/CandlestickChart.cs:844`
- The `SKPath` returned by `path.Detach()` is never disposed (`DrawPath` copies; the wrapper waits for its finalizer): native memory churn on every redraw for every indicator line.

### BUG-050 [Minor] Charting — order book widget never reacts to theme/language changes
- File: `src/ZettaTrade.UI/Charting/OrderBookWidget.cs:90-120`
- Unlike `CandlestickChart`, the widget never subscribes to `ThemeChanged`/`LanguageChanged`, so a theme or language switch leaves the drawn book (colors, column headers) stale until the next `Book` snapshot arrives — indefinitely while disconnected.

### BUG-051 [Minor] Localization — culture race in cached string lookup
- File: `src/ZettaTrade.UI/Localization/LocalizationManager.cs:113-114`
- `GetString` reads the cache key from `_currentCulture` before `GetOrAdd`; a concurrent language switch clears the cache so a racing caller receives a string in the wrong culture (self-heals on the next switch).

### BUG-052 [Minor, suspected] Localization — strong subscription lifetime of `{loc:Localized}` bindings
- Files: `src/ZettaTrade.UI/Localization/LocalizedExtension.cs:33-38`, `src/ZettaTrade.UI/Localization/LocalizationManager.cs:132-137`
- `GetObservable(...)` keeps an app-lifetime chain (`Defer` → value → `LanguageChanged` re-emission) and `ToBinding()` subscribes it for the binding's lifetime; detached/abandoned controls may stay rooted by the singleton subject chain. Verify Avalonia binding weak-reference semantics before treating this as a confirmed leak.

### BUG-053 [Minor] App — AI-panel visibility cache race under rapid toggles
- File: `src/ZettaTrade.App/ViewModels/ChatViewModel.cs:98-108`
- The `_persistedShowAiPanel` cache is updated in completion order of concurrent `SelectMany(FromAsync)` writes: two rapid toggles can complete out of order, the cache goes stale, and a subsequent toggle to the same value is filtered out and never persisted (restart shows the old visibility).

### BUG-054 [Minor] App — positions panel swallows non-validation failures
- File: `src/ZettaTrade.App/ViewModels/PositionsPanelViewModel.cs:255-259` (witness lines 284-288)
- Cancel and reload catch only `InvalidOperationException`: other failures escape into unobserved `ThrownExceptions` (cancel) or the window's subscription (reload), so the panel shows stale/empty lists with no error line.

### BUG-055 [Minor] App — inconsistent timestamp rendering between positions panel and history
- Files: `src/ZettaTrade.App/ViewModels/PositionsPanelViewModel.cs:298, 321`; `src/ZettaTrade.App/ViewModels/OrderHistoryViewModel.cs:137`
- The positions panel formats stored UTC timestamps as-is (`ToString(...)`), while the history window calls `.ToLocalTime()`: the same order displays timestamps differing by the local UTC offset between the two windows.

### BUG-056 [Minor] MCP — malformed requests get no error reply; stop path can sit on a blocked read
- Files: `src/ZettaTrade.AI/Mcp/McpServerHost.cs:202-215` (witness line 184)
- Malformed requests (missing `method`/`params.name`, bad JSON) and unknown methods with an id produce no JSON-RPC error response (the `-32601` path only covers unknown tools/resources), so the client waits forever. `StopAsync` cancels and immediately disposes `_cts` while `RunLoopAsync` may still be blocked in `Console.In.ReadLineAsync`, which cannot be interrupted for the console reader.

---

## Verified clean (checked, no bug found)

Areas reviewed and explicitly cleared, so effort is not duplicated when fixing the above:

- `EncryptedFileSecretStore`: AES-256-GCM with auth tag, fresh random 12-byte nonce per save, PBKDF2-SHA256 (600k iterations, random salt), temp+rename atomic save, Unix 0600 file mode; wrong-password/parse failures throw and never touch the file.
- `LinuxSecretServiceStore`: secret value correctly written to stdin; stdout/stderr drained before `WaitForExitAsync` (no pipe deadlock).
- `BinanceJsonNumbers`: InvariantCulture throughout; wrong JSON kinds throw instead of defaulting.
- `BybitOrderBookAssembler`: sides correct, delta gated on `u == last+1`, qty-0 deletes, `Cap` removes the worst-priced level.
- `BybitAdapter` REST plumbing: non-zero `retCode` throws; catalog pagination bounded and terminated.
- `WebSocketClient` background loop: cannot fault unobserved; fragment reassembly correct.
- `PaperTradingAdapter` fills/cancels serialized under `_tradingLock`; no `async void`/`.Result`/`lock`/stdout writes anywhere in the Exchange scope.
- Risk pipeline order preserved: dialog `Validate()` → `IRiskManager` → confirmation → placement (except BUG-026 above).
- `UiConfirmationService`: fail-closed on timeout/exception; composed from the exact request/order; `cancel_order` always passes `order.Symbol` (`PositionsPanelViewModel.cs:229`).
- `ChartViewModel` older-bars machinery: rebase guard, epoch check-and-set, UI-thread continuations; `BarSeriesMerge.PrependOlder` prefix logic.
- `SmaIndicator`: warm-up head, sliding window, output length, decimal math — correct.
- `ChartPriceScale`/`ChartBarLayout`/`OrderBookPageLayout`/`ChartPanWindow`: degenerate-value guards and slot math consistent.
- Theme parsing: malformed JSON recorded as a load error without crash; UI-thread publication of load errors.
- Localization fallback: absent key → `"[key]"`; format failures fall back to the template.
- MCP stdout hygiene: the only stdout writes in scope are the MCP protocol responses themselves.
- DI lifetimes in App: transient dialog/panel VMs, app-lifetime VMs singleton; no captive dependencies found.
