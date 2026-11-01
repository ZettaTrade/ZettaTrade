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
public sealed class SymbolListProvider : IAsyncInitializable
{
    private readonly IMarketDataAdapter _marketData;
    private readonly ILogger<SymbolListProvider> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IReadOnlyList<string>? _symbols;

    /// <summary>
    /// Create a provider with an empty cache; the composition root warms it
    /// during startup through <see cref="InitializeAsync"/>.
    /// </summary>
    public SymbolListProvider(
        IMarketDataAdapter marketData,
        ILogger<SymbolListProvider> logger)
    {
        _marketData = marketData ?? throw new ArgumentNullException(nameof(marketData));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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
    /// </summary>
    public async Task<IReadOnlyList<string>> GetSymbolsAsync(CancellationToken ct = default)
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

            var symbols = await _marketData.GetSymbolsAsync(ct);
            var names = symbols.Select(symbol => symbol.Normalized).ToArray();
            _symbols = names;
            _logger.LogInformation(
                "Symbol list cached: {SymbolCount} tradable instruments",
                names.Length);
            return names;
        }
        finally
        {
            _gate.Release();
        }
    }
}
