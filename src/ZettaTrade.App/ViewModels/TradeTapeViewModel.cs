namespace ZettaTrade.App.ViewModels;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using Livery.Contracts;
using Livery.Extensions;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// Trade tape (time and sales) panel state: a typed symbol input (free-form,
/// like the order book panel, so the tape still works offline), and the live
/// trades stream for the parsed symbol, gated by the whale-print filter
/// (T-245).
/// </summary>
/// <remarks>
/// Like <see cref="OrderBookViewModel"/>, this is an application-lifetime
/// singleton whose subscriptions are created in the constructor: WhenActivated
/// never fires for VMs bound as nested-panel DataContexts, and the tape must
/// stream from the first frame. The workspace selection (the chart's symbol
/// combo) is the owner and rewrites the typed input; the typed input stays
/// the free-form override and the last user action wins.
/// <see cref="Updates"/> stays raw in shape (testable without the UI thread):
/// the tape widget consumes the stream itself (buffering, coalescing,
/// pausing — its own frame discipline); the whale gate (T-245) filters the
/// stream here — a threshold above zero passes only the prints whose notional
/// (price × quantity) is strictly above it, zero passes everything. The
/// threshold resolves per shown symbol from the persisted state (the
/// symbol's own override, the absolute default otherwise) and every user
/// edit applies live and rides the debounced persist.
/// A new symbol cancels the previous stream (Rx <c>Switch</c>); a stream
/// failure ends the data for its symbol only (logged, never a default value)
/// and later symbol changes keep working.
/// </remarks>
public sealed class TradeTapeViewModel : ReactiveObject, IAsyncInitializable
{
    #region Constants

    /// <summary>Symbol typed at start, so the tape streams live data immediately.</summary>
    public const string DefaultSymbolInput = "BTC/USDT";

    #endregion

    #region Properties

    private readonly ILogger<TradeTapeViewModel> _logger;
    private readonly ISettingsService _settings;

    private string _symbolInput = DefaultSymbolInput;

    /// <summary>Raw symbol text; a parse failure keeps the previous tape visible.</summary>
    public string SymbolInput
    {
        get => _symbolInput;
        set => this.RaiseAndSetIfChanged(ref _symbolInput, value);
    }

    /// <summary>
    /// Live trades for the parsed symbol, switched so a new symbol cancels
    /// the previous stream, gated by the whale threshold (T-245): a
    /// threshold above zero passes only the prints whose notional
    /// (price × quantity) is strictly above it, zero passes everything.
    /// Raw — not UI-marshaled; the widget owns its own buffering and frame
    /// discipline.
    /// </summary>
    public IObservable<MarketTrade> Updates { get; }

    private decimal _whaleThreshold;

    /// <summary>
    /// The whale threshold the CURRENT symbol's filter resolves to (T-245,
    /// quote-currency units): the symbol's own override when one is stored,
    /// the absolute default otherwise; zero = the filter is off (everything
    /// passes, nothing highlights).
    /// </summary>
    public decimal WhaleThreshold
    {
        get => _whaleThreshold;
        private set => this.RaiseAndSetIfChanged(ref _whaleThreshold, value);
    }

    private string _whaleThresholdText = "0";

    /// <summary>
    /// The header field's raw text (T-245): parsed invariant per emission —
    /// an unparsable or negative spelling edits nothing (the standing value
    /// stays, never a coerced zero) — and the valid value applies live,
    /// riding the debounced persist.
    /// </summary>
    public string WhaleThresholdText
    {
        get => _whaleThresholdText;
        set => this.RaiseAndSetIfChanged(ref _whaleThresholdText, value);
    }

    #endregion

    #region Private Fields

    /// <summary>The per-symbol overrides loaded from the store (T-245); the absolute default rides beside it.</summary>
    private Dictionary<string, decimal> _whalePerSymbol = new(StringComparer.Ordinal);

    /// <summary>The absolute default the symbols without their own override keep riding.</summary>
    private decimal _whaleDefault;

    /// <summary>
    /// The resolution gate: the load, the symbol changes and the store's own
    /// change echoes rewrite the text themselves — the edit chain must never
    /// read those as user edits.
    /// </summary>
    private bool _isWhaleApplying;

    /// <summary>
    /// The bootstrap gate: an edit before the persisted state lands stays
    /// session-local (the not-yet-loaded map must never persist over the
    /// stored overrides it has not seen).
    /// </summary>
    private bool _isWhaleBootstrapped;

    /// <summary>The settled edit snapshots the debounced persist drains (T-245).</summary>
    private readonly Subject<(decimal Default, Dictionary<string, decimal> Overrides)> _whalePersistQueue = new();

    #endregion

    #region Constructors

    /// <summary>
    /// Create a new trade tape ViewModel instance.
    /// </summary>
    public TradeTapeViewModel(
        IMarketDataAdapter marketData,
        IObservable<Symbol> workspaceSelections,
        ISettingsService settings,
        ILogger<TradeTapeViewModel> logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger;

        var whaleThresholds = this.WhenAnyValue(x => x.WhaleThreshold);

        Updates = this.WhenAnyValue(x => x.SymbolInput)
            .Select(ToSymbol)
            .DistinctUntilChanged()
            .Where(symbol => symbol is not null)
            .Select(symbol => GuardStreamFailures(marketData.SubscribeTrades(symbol!))
                // The whale gate (T-245): every arriving print samples the
                // threshold's latest value (WithLatestFrom — a threshold edit
                // re-emits nothing stale); zero passes everything.
                .WithLatestFrom(
                    whaleThresholds,
                    (trade, threshold) => (Trade: trade, Threshold: threshold))
                .Where(pair => pair.Threshold <= 0m ||
                               pair.Trade.Price.Value * pair.Trade.Quantity.Value > pair.Threshold)
                .Select(pair => pair.Trade))
            .Switch();

        // The workspace combo owns the symbol: a selection rewrites the typed
        // input (RaiseAndSetIfChanged dedupes the no-change repeats) and the
        // trades switch chain follows. The ViewModel lives as long as the
        // application (singleton), so this subscription mirrors it.
        workspaceSelections.Subscribe(
            symbol =>
            {
                SymbolInput = symbol.Normalized;
                _logger.LogInformation("Workspace selection applied to the tape: {Symbol}", symbol.Normalized);
            },
            ex => _logger.LogError(ex, "The tape's workspace symbol stream stopped unexpectedly"));

        // The header field's edit chain (T-245): every USER emission parses
        // invariant; a valid non-negative value applies live (the resolved
        // threshold and the persisted state follow) and rides the debounced
        // persist; an unparsable or negative spelling edits nothing. The
        // bootstrap and the symbol-change resolutions rewrite the text under
        // the applying gate — they are never read as edits.
        this.WhenAnyValue(x => x.WhaleThresholdText)
            .Skip(1)
            .Where(_ => !_isWhaleApplying)
            .Select(TryParseWhaleText)
            .Where(parsed => parsed is not null)
            .Select(parsed => parsed!.Value)
            .DistinctUntilChanged()
            .Subscribe(
                ApplyWhaleThresholdEdit,
                ex => _logger.LogError(ex, "The whale-threshold edit stream stopped unexpectedly"));

        // The persist debounce (T-245): the keystrokes settle, the store
        // takes the last edit's snapshot (every snapshot carries the whole
        // state, so edits to different symbols inside one window survive as
        // the newest one's shape).
        _whalePersistQueue
            .Throttle(TimeSpan.FromMilliseconds(TapeSettingsKeys.WhalePersistDebounceMs))
            .SelectMany(edit => Observable.FromAsync(() => PersistWhaleThresholdAsync(edit)))
            .Subscribe(
                _ => { },
                ex => _logger.LogError(ex, "Persisting the whale thresholds failed"));

        // The store's own changes (the T-053 canon): an external edit, an
        // import or a factory reset reloads the resolved state. Our own
        // writes echo back through the same stream idempotently — the
        // property setter dedupes the same value, nothing loops.
        _settings.Changes
            .Where(change => change.Section == TapeSettingsKeys.Section &&
                             (change.Key == TapeSettingsKeys.WhaleThreshold ||
                              change.Key == TapeSettingsKeys.WhaleThresholds))
            .Select(_ => Observable.FromAsync(ReadWhaleStateAsync))
            .Switch()
            .ObserveOnUiThread()
            .Subscribe(
                state => ApplyWhaleState(state),
                ex => _logger.LogError(ex, "Reloading the whale thresholds after a settings change failed"));

        // The shown symbol's threshold re-resolves on every symbol change
        // (the per-symbol override wins, the default fills the rest); the
        // subscribe-time current-value echo is skipped — the bootstrap's
        // own load resolves it first.
        this.WhenAnyValue(x => x.SymbolInput)
            .Skip(1)
            .Subscribe(
                _ => ApplyResolvedWhaleThreshold(),
                ex => _logger.LogError(ex, "The whale-threshold symbol-resolution stream stopped unexpectedly"));
    }

    #endregion

    #region Initialization

    /// <inheritdoc/>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        // The persisted state lands before the first frame (the startup walk
        // owns the pass): the absolute default, the per-symbol overrides and
        // the shown symbol's resolved threshold — the store's raw absence is
        // the honest off.
        await ReadAndApplyWhaleStateAsync().ConfigureAwait(false);
        _isWhaleBootstrapped = true;
    }

    #endregion

    #region Private Methods

    /// <summary>
    /// Parse typed input into a normalized symbol; unexpected input yields
    /// null, which switches to no data (the widget keeps what it already has).
    /// </summary>
    private static Symbol? ToSymbol(string input)
    {
        var raw = input.Trim();
        if (raw.Length == 0)
        {
            return null;
        }

        var parts = raw.Split('/');
        if (parts.Length != 2 || parts.Any(part => part.Length == 0))
        {
            return null;
        }

        try
        {
            return Symbol.Parse(raw);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// A stream failure ends the current symbol's data (an error is logged)
    /// but must not terminate the switch pipeline for later symbols.
    /// </summary>
    private IObservable<MarketTrade> GuardStreamFailures(IObservable<MarketTrade> source) =>
        source.Catch<MarketTrade, Exception>(ex =>
        {
            _logger.LogError(ex, "The trade stream ended with an error");
            return Observable.Empty<MarketTrade>();
        });

    /// <summary>
    /// The whale threshold's header spelling (T-245): the invariant parse
    /// only (the editor fields' own convention); a negative or unparsable
    /// value is no threshold — null edits nothing, nothing coerces to zero.
    /// </summary>
    private static decimal? TryParseWhaleText(string text) =>
        decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0m
            ? parsed
            : null;

    /// <summary>
    /// Apply one USER threshold edit (T-245): the value lands on the CURRENT
    /// symbol's entry — and when the symbol rode the default (no override of
    /// its own), the edit also becomes the default the other symbols keep
    /// riding. An unparsable spelling keys the edit on nothing: the value
    /// applies to the live filter, the store misses out (logged), the next
    /// valid edit persists. A pre-bootstrap edit stays session-local: the
    /// not-yet-loaded map must never persist over the stored overrides it
    /// has not seen.
    /// </summary>
    private void ApplyWhaleThresholdEdit(decimal value)
    {
        var symbol = ToSymbol(SymbolInput);
        if (symbol is null)
        {
            _logger.LogDebug(
                "The whale-threshold edit applies to no parsable symbol ('{Symbol}'); it stays session-local",
                SymbolInput);
            WhaleThreshold = value;
            return;
        }

        if (!_isWhaleBootstrapped)
        {
            _logger.LogDebug("The whale-threshold edit landed before the persisted state loaded; it stays session-local");
            WhaleThreshold = value;
            return;
        }

        var hadOverride = _whalePerSymbol.ContainsKey(symbol.Normalized);
        var overrides = new Dictionary<string, decimal>(_whalePerSymbol, StringComparer.Ordinal)
        {
            [symbol.Normalized] = value,
        };
        _whalePerSymbol = overrides;
        if (!hadOverride)
        {
            _whaleDefault = value;
        }

        WhaleThreshold = value;
        _whalePersistQueue.OnNext((_whaleDefault, _whalePerSymbol));
    }

    /// <summary>Resolve the shown symbol's threshold and mirror it onto the resolved property and the header text (T-245).</summary>
    private void ApplyResolvedWhaleThreshold()
    {
        var symbol = ToSymbol(SymbolInput);
        if (symbol is null)
        {
            // An unparsable spelling resolves nothing: the last resolved
            // state stays (the tape itself shows no data for it anyway).
            return;
        }

        var resolved = _whalePerSymbol.TryGetValue(symbol.Normalized, out var overridden)
            ? overridden
            : _whaleDefault;
        if (resolved < 0m)
        {
            resolved = 0m;
        }

        _isWhaleApplying = true;
        try
        {
            WhaleThreshold = resolved;
            WhaleThresholdText = resolved.ToString(CultureInfo.InvariantCulture);
        }
        finally
        {
            _isWhaleApplying = false;
        }
    }

    /// <summary>Read the persisted whale state (the T-053 canon: the store is the source of truth, read fresh — never a cached copy).</summary>
    private async Task<(decimal Default, Dictionary<string, decimal> Overrides)> ReadWhaleStateAsync()
    {
        var defaultThreshold = await _settings
            .GetAsync<decimal>(TapeSettingsKeys.Section, TapeSettingsKeys.WhaleThreshold, 0m)
            .ConfigureAwait(false);
        var overrides = await _settings
            .GetAsync<Dictionary<string, decimal>>(TapeSettingsKeys.Section, TapeSettingsKeys.WhaleThresholds, new Dictionary<string, decimal>())
            .ConfigureAwait(false);
        return (defaultThreshold, overrides ?? new Dictionary<string, decimal>());
    }

    /// <summary>Load the persisted state and apply it (the initialization pass and the change echoes share the one reload lane).</summary>
    private async Task ReadAndApplyWhaleStateAsync()
    {
        var state = await ReadWhaleStateAsync().ConfigureAwait(false);
        ApplyWhaleState(state);
    }

    /// <summary>Land one read of the persisted whale state: the sanitized state, then the shown symbol's resolution.</summary>
    private void ApplyWhaleState((decimal Default, Dictionary<string, decimal> Overrides) state)
    {
        _whaleDefault = state.Default < 0m ? 0m : state.Default;
        _whalePerSymbol = SanitizeOverrides(state.Overrides);
        ApplyResolvedWhaleThreshold();
    }

    /// <summary>
    /// Sanitize one stored override map (T-245): a negative value is no
    /// threshold (zero is the honest off, negatives name nothing) and an
    /// empty key names no symbol — such entries drop at load, never coerce.
    /// </summary>
    private static Dictionary<string, decimal> SanitizeOverrides(Dictionary<string, decimal> overrides)
    {
        var clean = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var pair in overrides)
        {
            if (pair.Key.Length > 0 && pair.Value >= 0m)
            {
                clean[pair.Key] = pair.Value;
            }
        }

        return clean;
    }

    /// <summary>Persist one settled edit snapshot: the absolute default and the override map land as two keys (the codec-free JSON shapes the settings service serializes).</summary>
    private async Task PersistWhaleThresholdAsync((decimal Default, Dictionary<string, decimal> Overrides) edit)
    {
        try
        {
            await _settings
                .SetAsync(TapeSettingsKeys.Section, TapeSettingsKeys.WhaleThreshold, edit.Default)
                .ConfigureAwait(false);
            await _settings
                .SetAsync(TapeSettingsKeys.Section, TapeSettingsKeys.WhaleThresholds, edit.Overrides)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The live filter carries on (the state is in memory); the write
            // fault is loud but transient — log at error, never swallow
            // silently.
            _logger.LogError(ex, "Persisting the whale thresholds failed");
        }
    }

    #endregion
}
