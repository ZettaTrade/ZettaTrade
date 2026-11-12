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
/// The watchlist feed (T-120, row set reworked in T-196): one pricing pump
/// per panel row over the active exchange's ticker streams (through the
/// facade, so an exchange switch re-arms the per-row streams atomically), a
/// per-row price ring for the sparkline, and ONE coalesced snapshot stream
/// at the panel cadence — a wire quote storm can never storm the panel,
/// because the pulses ship the whole row set while the wire ticks stay
/// inside the service.
/// <para>
/// The row set is the user's watchlist catalog's ACTIVE list (T-196): the
/// rows ride the list's stored order, a list edit re-arms the set in place
/// (survivors keep their pricing state, moves just re-stamp the order), and
/// the workspace selection's symbol stays pinned as the trailing overlay
/// row when the list does not already carry it. Read-only all the way:
/// nothing here places, amends or cancels anything.
/// </para>
/// </summary>
/// <remarks>
/// Per-row tick swaps stay lock-free: every row's ticker handler owns one
/// immutable <see cref="WatchlistTick"/> snapshot and swaps it in with a
/// single atomic reference write; the pulse builder only reads those. The
/// row-SET transitions (list apply and selection pin) are the two
/// mutators' shared critical section — they run on different streams, so
/// they serialize through one small gate; a row's own tick state never
/// crosses it. The optional <c>pulse</c> seam injects a controllable
/// interval source for the deterministic facts (the default is the plain
/// Rx interval clock — exists per-byte for the test seam, nothing
/// speculative).
/// </remarks>
public sealed class WatchlistFeedService : IAsyncInitializable
{
    #region Constants

    /// <summary>The panel's sparkline ring capacity (the ramp of last prices).</summary>
    private const int SparklineCapacity = 32;

    /// <summary>The panel cadence: full snapshots at this rate only.</summary>
    private static readonly TimeSpan PulseInterval = TimeSpan.FromMilliseconds(500);

    #endregion Constants

    #region Private Fields

    /// <summary>The live row set, keyed by the normalized symbol text.</summary>
    private readonly ConcurrentDictionary<string, RowState> _rows = new(StringComparer.Ordinal);

    /// <summary>One ticker pump per live row: disposed when the row retires or at disposal.</summary>
    private readonly ConcurrentDictionary<string, IDisposable> _tickerSubscriptions = new(StringComparer.Ordinal);

    /// <summary>
    /// The row-set mutators' shared gate: the document apply and the
    /// selection pin ride different streams, and the set transition is a
    /// multi-step retire/join/restamp — one small critical section keeps
    /// every transition all-or-nothing against the other.
    /// </summary>
    private readonly object _rowSetGate = new();

    /// <summary>The next free row stamp: written after each list apply, read by a mid-stream overlay join.</summary>
    private long _nextRowSequence;

    /// <summary>The symbol the workspace selection currently pins (remembered, the overlay re-pins from it).</summary>
    private Symbol? _selection;

    /// <summary>The row the selection pins (may be a list row when the list carries the symbol).</summary>
    private RowState? _selectionRow;

    /// <summary>The last applied document: an identical re-publish re-arms nothing.</summary>
    private WatchlistDocument? _appliedDocument;

    /// <summary>The one shared "some row changed" signal the pulses sample.</summary>
    private readonly Subject<RxVoid> _dataTick = new();

    /// <summary>The workspace selection stream the overlay row follows.</summary>
    private readonly IDisposable? _selectionSubscription;

    /// <summary>The catalog's document stream the row set follows.</summary>
    private readonly IDisposable? _listSubscription;

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
    /// Create the feed: the row set arms over the watchlist catalog's
    /// document stream (the starter stands before the seed applies), the
    /// workspace selection pins its overlay row, and the coalesced
    /// snapshot stream composes over the pulse. The per-row subscriptions
    /// ride the active-exchange facade, so an exchange switch re-arms them
    /// without touching the row set.
    /// </summary>
    public WatchlistFeedService(
        IMarketDataAdapter marketData,
        IObservable<Symbol> workspaceSelections,
        WatchlistCatalogService catalog,
        ILogger<WatchlistFeedService> logger,
        IObservable<long>? pulse = null)
    {
        _marketData = marketData ?? throw new ArgumentNullException(nameof(marketData));
        catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _pulse = pulse ?? Observable.Interval(PulseInterval);

        Quotes = _dataTick
            .StartWith(RxVoid.Default)
            .Sample(_pulse)
            .Select(_ => BuildSnapshot())
            .Where(snapshot => snapshot.Count > 0);

        _listSubscription = catalog.Changes
            .StartWith(catalog.Current)
            .Subscribe(
                OnListDocument,
                ex => _logger.LogError(ex, "The watchlist document stream stopped unexpectedly"));

        _selectionSubscription = workspaceSelections.Subscribe(
            OnSelection,
            ex => _logger.LogError(ex, "The watchlist's workspace symbol stream stopped unexpectedly"));
    }

    #endregion Constructor

    #region Public Methods

    /// <summary>
    /// Nothing to await here: the pumps, the document follow and the
    /// selection follow are armed in the constructor (the tape/book panel
    /// precedent), and every network-facing leg already re-arms itself
    /// through the facade. The interface keeps the feed honest about being
    /// startup-walk aware.
    /// </summary>
    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>
    /// Stop the per-row pumps, the document follow and the selection follow
    /// (the app-lifetime singleton usually outlives the process, but the
    /// pose stays honest).
    /// </summary>
    public ValueTask DisposeAsync()
    {
        _listSubscription?.Dispose();
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
    /// Apply one catalog document: the active list is the row set. Rows the
    /// list dropped retire (the standing selection's row is exempt — it
    /// re-pins as the trailing overlay below), missing rows join, and the
    /// survivors re-stamp to the list's stored order. The selection's row
    /// closes the set: a list row when the list carries the symbol, the
    /// trailing overlay row otherwise. An identical re-publish (the seed
    /// echoing the already-armed starter) re-arms nothing.
    /// </summary>
    private void OnListDocument(WatchlistDocument document)
    {
        if (ReferenceEquals(_appliedDocument, document))
        {
            return;
        }

        _appliedDocument = document;

        var active = document.Lists.FirstOrDefault(list =>
                string.Equals(list.Name, document.ActiveList, StringComparison.OrdinalIgnoreCase))
            ?? document.Lists.FirstOrDefault();
        var wanted = active?.Symbols ?? (IReadOnlyList<string>)Array.Empty<string>();
        var listed = new HashSet<string>(wanted, StringComparer.Ordinal);

        lock (_rowSetGate)
        {
            // Pass 1: retire what the new list dropped. The standing
            // selection's row survives every list edit — it re-pins below.
            var selectionText = _selection?.Normalized;
            foreach (var row in _rows.Values)
            {
                if (!listed.Contains(row.SymbolText) &&
                    !string.Equals(row.SymbolText, selectionText, StringComparison.Ordinal))
                {
                    RetireRow(row);
                }
            }

            // Pass 2: join the missing rows and stamp every survivor into
            // the list's stored order — the snapshot order IS the list
            // order. A text the parser refuses is logged and skipped, never
            // defaulted (its stored slot stays empty).
            var sequence = 0L;
            foreach (var text in wanted)
            {
                var row = _rows.TryGetValue(text, out var found)
                    ? found
                    : RegisterRow(text, isOverlay: false, sequence);
                if (row is null)
                {
                    sequence++;
                    continue;
                }

                row.IsOverlay = false;
                row.SetSequence(sequence++);
            }

            // Pass 3: the selection's trailing slot — a list row stays where
            // the list put it, an outside symbol re-pins as the overlay row
            // after them.
            if (_selection is { } standing)
            {
                var row = _rows.TryGetValue(standing.Normalized, out var found)
                    ? found
                    : RegisterRow(standing.Normalized, isOverlay: true, sequence);
                if (row is { } pinned)
                {
                    if (!listed.Contains(pinned.SymbolText))
                    {
                        pinned.IsOverlay = true;
                        pinned.SetSequence(sequence++);
                    }

                    _selectionRow = pinned;
                }
            }

            Volatile.Write(ref _nextRowSequence, sequence);
            _dataTick.OnNext(RxVoid.Default);
        }
    }

    /// <summary>
    /// The workspace selection pins its row: the row joins as the trailing
    /// overlay when it is not already in the set, and the previous
    /// selection-only row retires — a list row never retires from a
    /// selection move. Runs under the row-set gate, so a list edit racing
    /// the move converges on one writer's answer.
    /// </summary>
    private void OnSelection(Symbol selection)
    {
        lock (_rowSetGate)
        {
            _selection = selection;
            var previous = _selectionRow;
            var row = _rows.TryGetValue(selection.Normalized, out var found)
                ? found
                : RegisterRow(selection.Normalized, isOverlay: true, Volatile.Read(ref _nextRowSequence));
            _selectionRow = row;

            if (previous is { } retiring &&
                !ReferenceEquals(retiring, row) &&
                retiring.IsOverlay)
            {
                RetireRow(retiring);
            }
        }
    }

    /// <summary>
    /// Add a row with its ticker pump at an explicit order stamp; an
    /// already-present text reuses its row. A symbol the parser refuses is
    /// logged and skipped — never defaulted.
    /// </summary>
    private RowState? RegisterRow(string normalizedText, bool isOverlay, long sequence)
    {
        if (_rows.TryGetValue(normalizedText, out var present))
        {
            return present;
        }

        Symbol symbol;
        try
        {
            symbol = Symbol.Parse(normalizedText);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Watchlist: symbol '{SymbolText}' does not parse; the row is skipped", normalizedText);
            return null;
        }

        var row = new RowState(normalizedText, symbol, isOverlay);
        row.SetSequence(sequence);
        if (_rows.TryAdd(normalizedText, row))
        {
            _tickerSubscriptions[normalizedText] = SubscribeRow(row);
            _dataTick.OnNext(RxVoid.Default);
            return row;
        }

        // A concurrent registration won the slot: its row is the answer.
        return _rows.TryGetValue(normalizedText, out var winner) ? winner : null;
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
    /// Build the complete snapshot: the whole live row set in stamp order,
    /// each row's newest atomic state (a not-yet-ticked row carries null
    /// fields and an empty ring).
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
    /// consistent in every snapshot. The order stamp is mutable — a list
    /// edit re-stamps the survivors without touching their tick state.
    /// </summary>
    private sealed class RowState(string symbolText, Symbol symbol, bool isOverlay)
    {
        private WatchlistTick? _latest;

        /// <summary>The order stamp the snapshots keep rows by (volatile both ways: the mutators and the pulse builder race).</summary>
        private long _sequence;

        /// <summary>The normalized symbol text the panel shows.</summary>
        public string SymbolText { get; } = symbolText;

        /// <summary>The parsed symbol the ticker pump rides.</summary>
        public Symbol Symbol { get; } = symbol;

        /// <summary>The row's order stamp for the snapshot sort.</summary>
        public long Sequence => Volatile.Read(ref _sequence);

        /// <summary>Restamp the row's order (one volatile write; the tick state is untouched).</summary>
        public void SetSequence(long value) => Volatile.Write(ref _sequence, value);

        /// <summary>Whether the row is the selection's transient overlay (list rows never are).</summary>
        public bool IsOverlay { get; internal set; } = isOverlay;

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
