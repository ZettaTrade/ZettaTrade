namespace ZettaTrade.App.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using Livery.Contracts;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.Infrastructure.Exchange;

/// <summary>
/// The symbol palette's instrument source: ONE behavioral snapshot
/// aggregating every registered adapter's tradable symbols — the palette
/// is a workspace-level surface over all exchanges, unlike the autocomplete
/// which rides the active adapter only.
/// </summary>
/// <remarks>
/// Lifecycle: the warm-up fetch rides <see cref="IAsyncInitializable"/>
/// (non-blocking, the SymbolListProvider pattern), and every
/// <see cref="ActiveExchangeService.AdapterChanges"/> emission kicks a
/// re-fetch — an adapter joining or leaving re-composes the palette. A
/// refresh that races another is recognized by an epoch counter and
/// dropped before it publishes (the stale result of an earlier composition
/// never lands). A failing adapter contributes zero rows and logs — never
/// a fabricated list (the no-defaults rule) and never a broken whole: the
/// healthy adapters' rows still serve.
/// </remarks>
public sealed class SymbolPaletteSource : IAsyncInitializable
{
    private readonly ActiveExchangeService _activeExchanges;
    private readonly ILogger<SymbolPaletteSource> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly BehaviorSubject<PaletteSnapshot> _updates = new(new PaletteSnapshot([], Settled: false));

    /// <summary>The counter that invalidates a refresh racing another composition.</summary>
    private int _epoch;

    /// <summary>
    /// Create the source with an empty, unsettled behavioral subject and
    /// the live re-fetch stream; the composition root warms it during
    /// startup through <see cref="InitializeAsync"/>.
    /// </summary>
    public SymbolPaletteSource(
        ActiveExchangeService activeExchanges,
        ILogger<SymbolPaletteSource> logger)
    {
        _activeExchanges = activeExchanges ?? throw new ArgumentNullException(nameof(activeExchanges));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // A single behavioral subscription: every adapter change starts a
        // new composition. Skip(1) drops the behavioral seed — subscribing
        // is not an adapter change; the warm-up exactly rides
        // IAsyncInitializable (the double fetch would waste a whole
        // catalog sweep per start). The change bumps the epoch first (an
        // in-flight fetch of the previous composition is dropped at its
        // finish), and the kick rides the ticket-based Observable.FromAsync
        // + guarded subscription — never a bare fire-and-forget task.
        activeExchanges
            .AdapterChanges
            .Skip(1)
            .Subscribe(
                _ => KickRefresh(),
                ex => _logger.LogError(ex, "The palette adapter-change stream stopped unexpectedly"));
    }

    /// <summary>
    /// The palette snapshots: emits the current snapshot on subscribe (so
    /// a late palette view bootstraps without waiting) and every completed
    /// refresh after that.
    /// </summary>
    public IObservable<PaletteSnapshot> Snapshots => _updates;

    /// <summary>
    /// Kick off the warm-up fetch without blocking startup: a failure only
    /// means the palette opens onto the loading state until an adapter
    /// change re-composes.
    /// </summary>
    public Task InitializeAsync(CancellationToken ct = default)
    {
        // The warm-up intentionally ignores the startup token: the fetch
        // outlives initialization (it is the palette's session source) and
        // an abandoned half-fetch on shutdown is harmless.
        Observable
            .FromAsync(_ => RefreshOnceAsync(CancellationToken.None))
            .Subscribe(
                _ => { },
                ex => _logger.LogWarning(
                    ex,
                    "Palette instrument warm-up failed; the next adapter change re-fetches"));

        return Task.CompletedTask;
    }

    /// <summary>
    /// One refresh attempt: fetch every registered adapter's symbols
    /// (failures contribute zero rows), then publish the settled aggregate
    /// — unless a newer attempt superseded this one first (the epoch drop;
    /// the newer attempt's publish carries the composition).
    /// </summary>
    /// <remarks>
    /// Public on purpose: the subscription paths only kick it and guard
    /// their own errors, and the tests await it for a deterministic
    /// settle.
    /// </remarks>
    public async Task RefreshOnceAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var epochAtFetch = Volatile.Read(ref _epoch);
            var instruments = new List<PaletteInstrument>();
            var adapters = _activeExchanges.Registered;

            foreach (var adapter in adapters)
            {
                IReadOnlyList<Symbol> symbols;
                try
                {
                    symbols = await adapter.GetSymbolsAsync(ct);
                }
                catch (AdapterCredentialsMissingException ex)
                {
                    // The standing absence (T-227): the adapter reports the
                    // no-key state itself, once per session — the palette's
                    // repeated refreshes contribute zero rows silently
                    // (debug rides the test runs, never the shipped log).
                    _logger.LogDebug(
                        ex,
                        "Adapter '{AdapterId}' runs without credentials; it contributes zero palette rows",
                        adapter.Id);
                    continue;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(
                        ex,
                        "Adapter '{AdapterId}' serves no palette instruments; it contributes zero rows",
                        adapter.Id);
                    continue;
                }

                var adapterId = adapter.Id;
                var adapterName = adapter.Name;
                instruments.AddRange(symbols.Select(symbol => new PaletteInstrument(
                    symbol,
                    adapterId,
                    adapterName,
                    symbol.Normalized.ToUpperInvariant())));
            }

            if (Volatile.Read(ref _epoch) != epochAtFetch)
            {
                _logger.LogInformation(
                    "The palette fetch finished after another composition started; {Count} rows stay unpublished",
                    instruments.Count);
                return;
            }

            var snapshot = new PaletteSnapshot(instruments, Settled: true);
            _updates.OnNext(snapshot);
            _logger.LogInformation("Palette instruments refreshed: {Count} rows", instruments.Count);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// An adapter change starts a new composition: the epoch bumps so an
    /// in-flight fetch of the previous composition is recognized and its
    /// result is not published, then the kick rides Observable.FromAsync
    /// with a guarded subscription (one kick per change, no queues).
    /// </summary>
    private void KickRefresh()
    {
        Interlocked.Increment(ref _epoch);
        Observable
            .FromAsync(_ => RefreshOnceAsync(CancellationToken.None))
            .Subscribe(
                _ => { },
                ex => _logger.LogWarning(
                    ex,
                    "Palette re-fetch after an adapter change failed; the previous rows stand until the next change"));
    }
}
