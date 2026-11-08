namespace ZettaTrade.App.Services;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using Livery.Contracts;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;
using RxVoid = ReactiveUI.Primitives.RxVoid;

/// <summary>
/// The watchlist feed (T-120): one pricing pump per panel row over the
/// active exchange's ticker streams (through the facade, so an exchange
/// switch re-arms the per-row streams atomically), a per-row price ring for
/// the sparkline, and ONE coalesced snapshot stream at the panel cadence —
/// a wire quote storm can never storm the panel, because the pulses ship
/// the whole row set while the wire ticks stay inside the service.
/// <para>
/// The row set is the majors starter list plus the workspace selection's
/// symbol (its row joins when it is not already there; the previous
/// selection-only row retires). Read-only all the way: nothing here places,
/// amends or cancels anything.
/// </para>
/// </summary>
/// <remarks>
/// Row state transitions stay lock-free: every row's ticker handler owns
/// one immutable <see cref="WatchlistTick"/> snapshot and swaps it into the
/// row with a single atomic reference write; the pulse builder only reads
/// those. The optional <c>pulse</c> seam injects a controllable interval
/// source for the deterministic facts (the default is the plain Rx interval
/// clock — exists per-byte for the test seam, nothing speculative).
/// </remarks>
public sealed class WatchlistFeedService : IAsyncInitializable
{
    #region Constants

    /// <summary>The panel's sparkline ring capacity (the ramp of last prices).</summary>
    private const int SparklineCapacity = 32;

    /// <summary>The panel cadence: full snapshots at this rate only.</summary>
    private static readonly TimeSpan PulseInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>The panel's starter symbols (normalized names) on the active exchange.</summary>
    private static readonly string[] StarterSymbols =
    [
        "BTC/USDT",
        "ETH/USDT",
        "SOL/USDT",
        "XRP/USDT",
        "ADA/USDT",
        "DOGE/USDT",
        "BNB/USDT",
        "LINK/USDT",
        "AVAX/USDT",
        "LTC/USDT",
    ];

    #endregion Constants

    #region Private Fields

    /// <summary>The live row set, keyed by the normalized symbol text.</summary>
    private readonly ConcurrentDictionary<string, RowState> _rows = new(StringComparer.Ordinal);

    /// <summary>One ticker pump per live row: disposed when the row retires or at disposal.</summary>
    private readonly ConcurrentDictionary<string, IDisposable> _tickerSubscriptions = new(StringComparer.Ordinal);

    /// <summary>Insertion sequence stamp for a new row; the snapshot keeps the rows by it.</summary>
    private long _nextRowSequence;

    /// <summary>The symbol the workspace selection currently pins (may equal a starter row).</summary>
    private RowState? _selectionRow;

    /// <summary>The one shared "some row changed" signal the pulses sample.</summary>
    private readonly Subject<RxVoid> _dataTick = new();

    /// <summary>The workspace selection stream the row set follows.</summary>
    private readonly IDisposable? _selectionSubscription;

    /// <summary>The pulse source: the default Rx interval clock or the test seam.</summary>
    private readonly IObservable<long> _pulse;

    /// <summary>The facade every row's ticker pump rides.</summary>
    private readonly IMarketDataAdapter _marketData;

    /// <summary>The feed's own logging lane.</summary>
    private readonly ILogger<WatchlistFeedService> _logger;

    #endregion Private Fields

    #region Properties

    /// <summary>
    /// The panel's quote feed: one coalesced snapshot per pulse — the row
    /// set with the newest state of every row (a not-yet-ticked row arrives
    /// with null fields). Nothing emits before the first pulse tick, and
    /// the first pulse always ships the row set even if nothing ticked yet,
    /// so the panel renders its rows instead of a blank surface; a pulse
    /// without fresh data re-ships nothing (the Rx sample keeps only the
    /// actually-new snapshots). The whole row set ships per snapshot, so
    /// the wire's quote storm stays inside the service.
    /// </summary>
    public IObservable<IReadOnlyList<WatchlistQuote>> Quotes { get; }

    #endregion Properties

    #region Constructor

    /// <summary>
    /// Create the feed: arm every starter row's ticker pump, follow the
    /// workspace selection, and compose the coalesced snapshot stream over
    /// the pulse. The per-row subscriptions ride the active-exchange facade,
    /// so an exchange switch re-arms them without touching the row set.
    /// </summary>
    public WatchlistFeedService(
        IMarketDataAdapter marketData,
        IObservable<Symbol> workspaceSelections,
        ILogger<WatchlistFeedService> logger,
        IObservable<long>? pulse = null)
    {
        _marketData = marketData;
        _logger = logger;
        _pulse = pulse ?? Observable.Interval(PulseInterval);

        Quotes = _dataTick
            .StartWith(RxVoid.Default)
            .Sample(_pulse)
            .Select(_ => BuildSnapshot())
            .Where(snapshot => snapshot.Count > 0);

        foreach (var text in StarterSymbols)
        {
            RegisterRow(text, isStarter: true);
        }

        _selectionSubscription = workspaceSelections.Subscribe(
            OnSelection,
            ex => _logger.LogError(ex, "The watchlist's workspace symbol stream stopped unexpectedly"));
    }

    #endregion Constructor

    #region Public Methods

    /// <summary>
    /// Nothing to await here: the pumps and the selection follow are armed
    /// in the constructor (the tape/book panel precedent), and every
    /// network-facing leg already re-arms itself through the facade. The
    /// interface keeps the feed honest about being startup-walk aware.
    /// </summary>
    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>
    /// Stop the per-row pumps and the selection follow (the app-lifetime
    /// singleton usually outlives the process, but the pose stays honest).
    /// </summary>
    public ValueTask DisposeAsync()
    {
        _selectionSubscription?.Dispose();

        foreach (var subscription in _tickerSubscriptions.Values)
        {
            subscription.Dispose();
        }

        _tickerSubscriptions.Clear();
        _dataTick.Dispose();
        return ValueTask.CompletedTask;
    }

    #endregion Public Methods

    #region Private Methods

    /// <summary>
    /// The workspace selection pins its row: the row joins the set when it
    /// is not already there, and the previous selection-only row retires (the
    /// starter rows never retire). Runs on the selection stream's own serial
    /// context, so the retire-then-pin swap is single-writer.
    /// </summary>
    private void OnSelection(Symbol selection)
    {
        RegisterRow(selection.Normalized, isStarter: false);

        var previous = _selectionRow;
        _selectionRow = _rows[selection.Normalized];

        if (previous is { } retiring &&
            !ReferenceEquals(retiring, _selectionRow) &&
            !retiring.IsStarter)
        {
            RetireRow(retiring);
        }
    }

    /// <summary>
    /// Add a row with its ticker pump; an already-present text reuses its row
    /// (a second registration is the same symbol under a different caller).
    /// A symbol the parser refuses is logged and skipped — never defaulted.
    /// </summary>
    private void RegisterRow(string normalizedText, bool isStarter)
    {
        if (_rows.ContainsKey(normalizedText))
        {
            return;
        }

        Symbol symbol;
        try
        {
            symbol = Symbol.Parse(normalizedText);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Watchlist: symbol '{SymbolText}' does not parse; the row is skipped", normalizedText);
            return;
        }

        var row = new RowState(normalizedText, symbol, Interlocked.Increment(ref _nextRowSequence))
        {
            IsStarter = isStarter,
        };
        if (_rows.TryAdd(normalizedText, row))
        {
            _tickerSubscriptions[normalizedText] = SubscribeRow(row);
            _dataTick.OnNext(RxVoid.Default);
        }
    }

    /// <summary>
    /// Start one row's ticker pump: every price lands in the row's own ring
    /// (the handler owns the growth — Rx serializes each stream's
    /// notifications, so the list never sees concurrent writes) and flips
    /// the row's immutable tick snapshot with one atomic write. A stream
    /// failure leaves the row on its last served data (the honest value it
    /// already had), logged — and the facade's switch re-arms it anyway.
    /// </summary>
    private IDisposable SubscribeRow(RowState row)
    {
        var ring = new List<decimal>(SparklineCapacity);

        try
        {
            return _marketData.SubscribeTicker(row.Symbol).Subscribe(
                ticker =>
                {
                    ring.Add(ticker.LastPrice);
                    if (ring.Count > SparklineCapacity)
                    {
                        ring.RemoveAt(0);
                    }

                    row.Apply(new WatchlistTick(
                        ticker.LastPrice,
                        ticker.PriceChange24hPercent,
                        ticker.Volume24h,
                        ring.ToArray()));
                    _dataTick.OnNext(RxVoid.Default);
                },
                error => _logger.LogWarning(
                    error,
                    "Watchlist: ticker stream for {Symbol} failed; the row keeps its last served data",
                    row.SymbolText));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Watchlist: no ticker available for {Symbol}; the row stays unpriced",
                row.SymbolText);
            return Disposable.Empty;
        }
    }

    /// <summary>
    /// Retire a row: stop its pump and drop its state (the panel's diff
    /// removes the visual row on the next pulse).
    /// </summary>
    private void RetireRow(RowState retiring)
    {
        if (_tickerSubscriptions.TryRemove(retiring.SymbolText, out var pump))
        {
            pump.Dispose();
        }

        _rows.TryRemove(retiring.SymbolText, out _);
        _dataTick.OnNext(RxVoid.Default);
    }

    /// <summary>
    /// Build the complete snapshot: the whole live row set in insertion
    /// order, each row's newest atomic state (a not-yet-ticked row carries
    /// null fields and an empty ring).
    /// </summary>
    private IReadOnlyList<WatchlistQuote> BuildSnapshot()
    {
        var rows = _rows.Values.ToArray();
        Array.Sort(rows, static (left, right) => left.Sequence.CompareTo(right.Sequence));
        return rows.Select(row => row.ToQuote()).ToList();
    }

    #endregion Private Methods

    #region Row State

    /// <summary>
    /// One panel row's self-coherent state: the ticker handler swaps one
    /// immutable tick snapshot in; the pulse builder only reads it. The
    /// single atomic write keeps price, change, volume and ring mutually
    /// consistent in every snapshot.
    /// </summary>
    private sealed class RowState(string symbolText, Symbol symbol, long sequence)
    {
        private WatchlistTick? _latest;

        /// <summary>The normalized symbol text the panel shows.</summary>
        public string SymbolText { get; } = symbolText;

        /// <summary>The parsed symbol the ticker pump rides.</summary>
        public Symbol Symbol { get; } = symbol;

        /// <summary>The insertion stamp the snapshots keep rows by.</summary>
        public long Sequence { get; } = sequence;

        /// <summary>Starter rows never retire; selection-only rows do.</summary>
        public bool IsStarter { get; init; }

        /// <summary>The row's newest complete state, or null until it ticks.</summary>
        public WatchlistTick? Latest => Volatile.Read(ref _latest);

        /// <summary>Swap in the row's newest snapshot (one atomic write).</summary>
        public void Apply(WatchlistTick tick) => Volatile.Write(ref _latest, tick);

        /// <summary>The row's quote for the current snapshot.</summary>
        public WatchlistQuote ToQuote()
        {
            var latest = Latest;
            return new WatchlistQuote(
                SymbolText,
                latest?.LastPrice,
                latest?.Change24hPercent,
                latest?.Volume24h,
                latest?.Ring ?? Array.Empty<decimal>());
        }
    }

    /// <summary>
    /// One ticker arrival's full row state (immutable, swapped atomically).
    /// The ring is the producer's already-grown copy — the consumer reads it
    /// without the producer ever mutating the same array again.
    /// </summary>
    private sealed record WatchlistTick(
        decimal LastPrice,
        decimal Change24hPercent,
        decimal Volume24h,
        IReadOnlyList<decimal> Ring);

    #endregion Row State
}
