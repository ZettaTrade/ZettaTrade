namespace ZettaTrade.App.Services;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Livery.Contracts;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.Infrastructure.Exchange;

/// <summary>
/// Application-lifetime cache for the tradable symbol lists — one fetch per
/// exchange per session (T-228): a fresh <c>exchangeInfo</c> REST call per
/// dialog open made the autocomplete suggestions show up seconds late; this
/// provider pays the network cost once per exchange — warmed up at startup
/// via <see cref="ZettaTrade.Core.Interfaces.IAsyncInitializable"/> for every
/// registered adapter — and every later consumer answers from the cache.
/// The active exchange's list rides <see cref="GetSymbolsAsync"/> (the
/// autocomplete, the chart bootstrap); the palette's warm-up and the
/// heatmap/screener universes ride the same per-exchange path
/// (<see cref="GetAdapterSymbolsAsync"/>), so the venue is hit exactly once.
/// </summary>
/// <remarks>
/// Each exchange's list is session-stable by design (the T-225 lesson: the
/// provider exists so nothing re-pays the catalog sweep); a failed fetch is
/// never cached — the next ask retries. Concurrent asks for one exchange
/// share the in-flight fetch through the entry's semaphore (the warm-up, the
/// palette's warm-up and the first dialog open all land on one adapter hit),
/// and an exchange switch answers from the target exchange's own entry —
/// warmed at startup, so a switch re-composes without touching the wire.
/// <see cref="GetSymbolsAsync"/> takes no default token value on purpose:
/// every consumer passes an explicit one (the dialog's command execution
/// token, the chart's subscription token) and the only
/// <see cref="CancellationToken.None"/> in this class is the deliberate
/// app-lifetime warm-up.
/// </remarks>
public sealed class SymbolListProvider : IAsyncInitializable
{
    private readonly ActiveExchangeService _activeExchanges;
    private readonly ILogger<SymbolListProvider> _logger;

    /// <summary>One cache entry per exchange id: the list plus its fetch gate.</summary>
    private readonly ConcurrentDictionary<string, CacheEntry> _caches = new(StringComparer.Ordinal);

    /// <summary>
    /// Create the provider with empty per-exchange caches; the composition
    /// root warms every registered exchange during startup through
    /// <see cref="InitializeAsync"/>.
    /// </summary>
    public SymbolListProvider(
        ActiveExchangeService activeExchanges,
        ILogger<SymbolListProvider> logger)
    {
        _activeExchanges = activeExchanges ?? throw new ArgumentNullException(nameof(activeExchanges));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Warm every registered exchange's list without blocking startup: a
    /// failure only means the first ask for that exchange pays the network
    /// cost (the entry stays empty and re-fetches there).
    /// </summary>
    public Task InitializeAsync(CancellationToken ct = default)
    {
        // The warm-up intentionally ignores the startup token: the fetches
        // outlive initialization (they are the caches of the whole session)
        // and an abandoned half-fetch on shutdown is harmless. Every
        // registered adapter warms in parallel — one fetch per exchange,
        // which is the whole point of the session cache (T-228).
        Observable
            .FromAsync(_ => WarmUpAsync(CancellationToken.None))
            .Subscribe(
                _ => { },
                ex => _logger.LogWarning(
                    ex,
                    "Symbol list warm-up failed; the first ask per exchange re-fetches"));

        return Task.CompletedTask;
    }

    /// <summary>
    /// Return the active exchange's cached symbol names (the autocomplete
    /// and chart-bootstrap shape), fetching it once when empty (double
    /// checked under the entry's semaphore so concurrent opens share one
    /// fetch). A failed fetch is never cached: the next ask retries.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetSymbolsAsync(CancellationToken ct)
    {
        var symbols = await GetAdapterSymbolsAsync(_activeExchanges.Current, ct).ConfigureAwait(false);
        return symbols.Select(symbol => symbol.Normalized).ToArray();
    }

    /// <summary>
    /// Return one exchange's cached symbol list (the palette warm-up and
    /// the heatmap/screener universe shape), fetching it once when empty
    /// under the entry's semaphore. A failed fetch throws to the asker and
    /// stays uncached; a concurrent ask for the same exchange joins the
    /// in-flight fetch instead of duplicating it.
    /// </summary>
    public async Task<IReadOnlyList<Symbol>> GetAdapterSymbolsAsync(
        IMarketDataAdapter adapter,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        var entry = _caches.GetOrAdd(adapter.Id, _ => new CacheEntry());
        if (entry.Symbols is { } fastPath)
        {
            return fastPath;
        }

        await entry.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (entry.Symbols is { } doubleChecked)
            {
                return doubleChecked;
            }

            var symbols = await adapter.GetSymbolsAsync(ct).ConfigureAwait(false);
            entry.Symbols = symbols;
            _logger.LogInformation(
                "Instrument list cached for {ExchangeId}: {SymbolCount} tradable symbols",
                adapter.Id,
                symbols.Count);
            return symbols;
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    /// <summary>
    /// Warm every registered adapter once, in parallel; each adapter's
    /// failure is its own (a no-key refusal rides the T-227 quiet-absence
    /// debug lane, a real fault a warning) and never poisons the others.
    /// </summary>
    private async Task WarmUpAsync(CancellationToken ct)
    {
        var adapters = _activeExchanges.Registered;
        var warmups = adapters.Select(adapter => WarmOneAsync(adapter, ct));
        await Task.WhenAll(warmups).ConfigureAwait(false);
    }

    private async Task WarmOneAsync(IMarketDataAdapter adapter, CancellationToken ct)
    {
        try
        {
            await GetAdapterSymbolsAsync(adapter, ct).ConfigureAwait(false);
        }
        catch (AdapterCredentialsMissingException ex)
        {
            // The standing absence (T-227): the adapter reports the no-key
            // state itself — the warm-up's failure is quiet and the entry
            // stays empty for the next ask.
            _logger.LogDebug(
                ex,
                "The {ExchangeId} warm-up runs without credentials; the first ask re-fetches",
                adapter.Id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "The {ExchangeId} instrument warm-up failed; the first ask re-fetches",
                adapter.Id);
        }
    }

    /// <summary>One exchange's list plus the gate serializing its fetches.</summary>
    private sealed class CacheEntry
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public IReadOnlyList<Symbol>? Symbols { get; set; }
    }
}
