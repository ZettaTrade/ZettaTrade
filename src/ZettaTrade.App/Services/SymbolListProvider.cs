namespace ZettaTrade.App.Services;

using System;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// Application-lifetime cache for the tradable symbol list. A fresh
/// <c>exchangeInfo</c> REST call per dialog open made the autocomplete
/// suggestions show up seconds late; this provider pays the network cost
/// once — warmed up at startup via
/// <see cref="ZettaTrade.Core.Interfaces.IAsyncInitializable"/> — and every
/// later dialog open answers instantly from the cache.
/// </summary>
/// <remarks>
/// The cache is scoped to the active exchange: every
/// <see cref="ActiveExchangeService.AdapterChanges"/> emission flushes it,
/// so the next fetch targets the exchange the selector picked. A fetch that
/// races a switch is recognized by an invariant epoch counter and is not
/// cached (it still returns its data — the next caller re-fetches fresh).
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
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Caches are scoped to one exchange; the counter invalidates in-flight fetches on a switch.</summary>
    private int _epoch;

    private IReadOnlyList<string>? _symbols;

    /// <summary>
    /// Create a provider with an empty cache and a live flush stream; the
    /// composition root warms it during startup through
    /// <see cref="InitializeAsync"/>.
    /// </summary>
    public SymbolListProvider(
        ActiveExchangeService activeExchanges,
        ILogger<SymbolListProvider> logger)
    {
        _activeExchanges = activeExchanges ?? throw new ArgumentNullException(nameof(activeExchanges));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // A single behavioral subscription: every adapter change (and the
        // first emission at construction) drops the cached list, so the next
        // fetch targets the exchange the selector picked.
        activeExchanges
            .AdapterChanges
            .Subscribe(
                _ => OnAdapterChanged(),
                ex => _logger.LogError(ex, "The symbol cache flush stream stopped unexpectedly"));
    }

    /// <summary>
    /// Kick off the warm-up fetch without blocking startup: a failure only
    /// means the first dialog open pays the network cost (the cache stays
    /// empty and <see cref="GetSymbolsAsync"/> re-fetches there).
    /// </summary>
    public Task InitializeAsync(CancellationToken ct = default)
    {
        // The warm-up intentionally ignores the startup token: the fetch
        // outlives initialization (it is the cache of the whole session) and
        // an abandoned half-fetch on shutdown is harmless.
        Observable
            .FromAsync(_ => GetSymbolsAsync(CancellationToken.None))
            .Subscribe(
                _ => { },
                ex => _logger.LogWarning(
                    ex,
                    "Symbol list warm-up failed; the first dialog open re-fetches"));

        return Task.CompletedTask;
    }

    /// <summary>
    /// Return the cached symbol list, fetching it once when empty (double
    /// checked under a semaphore so concurrent dialog opens share one
    /// fetch). A failed fetch is never cached: the next dialog open retries.
    /// A fetch that finishes after an exchange switch returns its (possibly
    /// stale) list to the caller but is not cached — the switcher's next
    /// fetch already targets the new exchange.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetSymbolsAsync(CancellationToken ct)
    {
        if (_symbols is { } fastPath)
        {
            return fastPath;
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (_symbols is { } doubleChecked)
            {
                return doubleChecked;
            }

            var epochAtFetch = Volatile.Read(ref _epoch);
            var symbols = await _activeExchanges.GetSymbolsAsync(ct);
            var names = symbols.Select(symbol => symbol.Normalized).ToArray();

            if (Volatile.Read(ref _epoch) != epochAtFetch)
            {
                _logger.LogWarning(
                    "The symbol fetch finished after an active-exchange switch; the list belongs to the previous exchange and is not cached");
            }
            else
            {
                _symbols = names;
                _logger.LogInformation(
                    "Symbol list cached: {SymbolCount} tradable instruments",
                    names.Length);
            }

            return names;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Drop the cached list on an adapter change: the epoch counter bumps so
    /// an in-flight fetch of the previous exchange is recognized and its
    /// result is not cached.
    /// </summary>
    private void OnAdapterChanged()
    {
        Interlocked.Increment(ref _epoch);
        _symbols = null;
        _logger.LogInformation("The active exchange changed; the cached symbol list is dropped");
    }
}
