namespace ZettaTrade.App.Services;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Livery.Contracts;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.Infrastructure.Exchange;
using ZettaTrade.Infrastructure.Persistence;

/// <summary>
/// The portfolio-value snapshot service (T-209): on a bounded cadence it
/// values the paper account's balances — the quote asset rides at par (the
/// simulation's unit of account, a definitional one, never a fabricated
/// price), every other asset rides the last price its facade ticker
/// delivered, and an asset no ticker priced stays in the allocation with
/// its amount disclosed and stays OUT of the total. A pass whose facts did
/// not change since the last write is skipped: the idle account stays
/// cheap, a moved market writes. The rows land in the
/// <see cref="IPortfolioValueStore"/>, keyed per trading account — the
/// balances panel's curve and allocation read them whole.
/// <para>
/// The account is the paper account (the concrete paper adapter instance
/// the DI factory injects — paper-first; other trading accounts join
/// later). Read-only bookkeeping end to end: the service never places,
/// amends or cancels anything.
/// </para>
/// </summary>
public sealed class PortfolioValueSnapshotService(
    ITradingAdapter paperAccount,
    IMarketDataAdapter marketData,
    IPortfolioValueStore store,
    ILogger<PortfolioValueSnapshotService> logger) : IAsyncInitializable, IAsyncDisposable
{
    #region Constants

    /// <summary>The snapshot cadence: the write bound (an idle account
    /// writes one row and then skips; a moving market writes at most this
    /// often). Pinned public: the store's write bound is a test-visible
    /// contract, not an implementation detail.</summary>
    public static readonly TimeSpan SnapshotInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The paper account's quote currency: the unit of account every value
    /// rides in (the paper venue's own definitional quote).
    /// </summary>
    private static readonly string QuoteCurrency = PaperTradingAdapter.QuoteCurrency;

    #endregion Constants

    #region Private Fields

    /// <summary>The last price each priced asset's facade ticker delivered (base asset -> price).</summary>
    private readonly ConcurrentDictionary<string, decimal> _prices = new(StringComparer.Ordinal);

    /// <summary>One ticker pump per priced asset (base asset -> subscription).</summary>
    private readonly ConcurrentDictionary<string, IDisposable> _tickerSubscriptions = new(StringComparer.Ordinal);

    /// <summary>Cancellation of the snapshot loop at disposal.</summary>
    private readonly CancellationTokenSource _lifetimeCts = new();

    /// <summary>The last written row's facts, for the material-change skip.</summary>
    private (decimal TotalValue, string AllocationJson)? _lastWritten;

    /// <summary>The loop task, tracked and awaited at disposal (never abandoned).</summary>
    private Task? _snapshotLoop;

    #endregion Private Fields

    #region Public Methods

    /// <inheritdoc/>
    public Task InitializeAsync(CancellationToken ct = default)
    {
        _snapshotLoop = RunSnapshotLoopAsync(_lifetimeCts.Token);
        return Task.CompletedTask;
    }

    /// <summary>Stop the loop and the ticker pumps.</summary>
    public async ValueTask DisposeAsync()
    {
        _lifetimeCts.Cancel();

        if (_snapshotLoop is { } loop)
        {
            try
            {
                await loop;
            }
            catch (OperationCanceledException)
            {
                // The loop is expected to die through cancellation.
            }
        }

        foreach (var subscription in _tickerSubscriptions.Values)
        {
            subscription.Dispose();
        }

        _tickerSubscriptions.Clear();
        _lifetimeCts.Dispose();
    }

    /// <summary>
    /// Take one snapshot pass: value the paper account's current balances
    /// and write the row unless the facts did not change since the last
    /// write. The loop drives this on the cadence; tests drive it directly.
    /// One failed pass never kills the loop — the next tick retries.
    /// </summary>
    public async Task TakeSnapshotAsync(CancellationToken ct = default)
    {
        var balances = await paperAccount.GetBalancesAsync(ct);
        if (balances.Count == 0)
        {
            // An account holding nothing has no value to snapshot: nothing
            // is written, never a fabricated zero row.
            return;
        }

        ReconcileTickerSubscriptions(balances);

        var allocation = balances
            .OrderBy(served => served.Key, StringComparer.Ordinal)
            .Select(served => BuildAllocationEntry(served.Key, served.Value.Amount))
            .ToArray();
        var total = PortfolioValueAllocationEntry.PricedTotal(allocation);
        var allocationJson = PortfolioValueAllocationEntry.SerializeAllocation(allocation);

        // The material-change skip: an idle account (no trades, no price
        // movement) writes one row and then skips; a moved market writes.
        if (_lastWritten is { } last &&
            last.TotalValue == total &&
            string.Equals(last.AllocationJson, allocationJson, StringComparison.Ordinal))
        {
            return;
        }

        var row = new PortfolioValueSnapshotRow(
            paperAccount.Id,
            DateTimeOffset.UtcNow,
            QuoteCurrency,
            total,
            allocationJson);
        await store.WriteAsync(row, ct);

        _lastWritten = (total, allocationJson);
        logger.LogInformation(
            "Portfolio value snapshot: {Total} {Quote} across {Assets} assets ({Priced} priced)",
            total.ToString(CultureInfo.InvariantCulture),
            QuoteCurrency,
            allocation.Length,
            allocation.Count(entry => entry.Priced));
    }

    #endregion Public Methods

    #region Private Methods

    /// <summary>
    /// The snapshot cadence loop: one failed pass never kills the feed —
    /// the next tick retries (the store's own writes stay consistent by
    /// construction: a row lands whole or not at all).
    /// </summary>
    private async Task RunSnapshotLoopAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(SnapshotInterval);
            while (await timer.WaitForNextTickAsync(ct))
            {
                try
                {
                    await TakeSnapshotAsync(ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Portfolio value snapshot pass failed; retrying on the next tick");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Disposal: the loop is expected to die here.
        }
    }

    /// <summary>
    /// Keep one ticker pump per base asset the account currently holds
    /// (beyond the quote asset itself) and drop the pumps the account no
    /// longer funds. A refused subscription (the active adapter does not
    /// serve the pair) logs and defers to the next pass.
    /// </summary>
    private void ReconcileTickerSubscriptions(IReadOnlyDictionary<string, Money> balances)
    {
        var held = balances.Keys
            .Where(asset => !string.Equals(asset, QuoteCurrency, StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var asset in _tickerSubscriptions.Keys)
        {
            if (held.Contains(asset))
            {
                continue;
            }

            if (_tickerSubscriptions.TryRemove(asset, out var dropped))
            {
                dropped.Dispose();
                logger.LogInformation("Portfolio value: stopped pricing {Asset} (the account no longer holds it)", asset);
            }
        }

        foreach (var asset in held)
        {
            EnsureTickerSubscription(asset);
        }
    }

    /// <summary>Start one asset's ticker pump: every price lands in the cache.</summary>
    private void EnsureTickerSubscription(string asset)
    {
        if (_tickerSubscriptions.ContainsKey(asset))
        {
            return;
        }

        try
        {
            var symbol = Symbol.Parse($"{asset}/{QuoteCurrency}");
            var stream = marketData.SubscribeTicker(symbol);

            _tickerSubscriptions[asset] = stream.Subscribe(
                ticker => _prices[asset] = ticker.LastPrice,
                error =>
                {
                    logger.LogWarning(
                        error,
                        "Portfolio value: ticker stream for {Asset} failed; it re-arms on the next pass",
                        asset);

                    if (_tickerSubscriptions.TryRemove(asset, out var failed))
                    {
                        failed.Dispose();
                    }
                });

            logger.LogInformation("Portfolio value: pricing {Asset} for the valuation", asset);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Portfolio value: no ticker available for {Asset}; it stays unpriced (disclosed, never a fabricated price)",
                asset);
        }
    }

    /// <summary>
    /// Build one allocation entry: the quote asset rides at par (the unit
    /// of account is definitional), a priced asset rides its cached price,
    /// an unpriced one discloses its amount and stays out of the total.
    /// </summary>
    private PortfolioValueAllocationEntry BuildAllocationEntry(string asset, decimal amount)
    {
        if (string.Equals(asset, QuoteCurrency, StringComparison.Ordinal))
        {
            return new PortfolioValueAllocationEntry(asset, amount, Price: 1m, Value: amount, Priced: true);
        }

        if (_prices.TryGetValue(asset, out var price))
        {
            return new PortfolioValueAllocationEntry(asset, amount, Price: price, Value: amount * price, Priced: true);
        }

        return new PortfolioValueAllocationEntry(asset, amount, Price: null, Value: null, Priced: false);
    }

    #endregion Private Methods
}

