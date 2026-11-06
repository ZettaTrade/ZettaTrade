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
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.Infrastructure.PnL;

/// <summary>
/// The PnL feed: a thin app-side wrapper around the Infrastructure
/// accounting engine. Exposes the engine's state feed for future UI
/// consumers (a headless core — no view model here), keeps the unrealized
/// side priced through the active-exchange facade's ticker subscriptions
/// (the cheapest existing market-data source), and re-syncs the ledger on
/// a slow cadence — the shipped order journal has no change notifications,
/// so a periodic idempotent diff is the one push-free way realized state
/// follows live paper fills.
/// <para>
/// Read-only bookkeeping end to end: the service never places, amends or
/// cancels anything.
/// </para>
/// </summary>
/// <remarks>
/// Ticker stream errors (adapter switches aside) leave their symbol
/// unpriced: a failed subscription is disposed and re-armed on the next
/// state change, so a flapping feed cannot loop without bound, and the
/// accounting never fabricates a price.
/// </remarks>
public sealed class PnlFeedService(
    IMarketDataAdapter marketData,
    PnlAccountingService engine,
    ILogger<PnlFeedService> logger) : IAsyncInitializable
{
    #region Constants

    /// <summary>Cadence of the ledger re-sync (the journal has no push surface).</summary>
    private static readonly TimeSpan LedgerSyncInterval = TimeSpan.FromSeconds(5);

    #endregion Constants

    #region Private Fields

    /// <summary>One ticker price pump per non-flat position symbol, through the facade.</summary>
    private readonly ConcurrentDictionary<string, IDisposable> _tickerSubscriptions = new();

    /// <summary>Cancellation of the ledger sync loop (and of in-flight price pumps) at disposal.</summary>
    private readonly CancellationTokenSource _lifetimeCts = new();

    /// <summary>The engine's state feed, mirrored into the subscription maintenance.</summary>
    private IDisposable? _stateSubscription;

    /// <summary>The sync loop task, tracked and awaited at disposal (never abandoned).</summary>
    private Task? _syncLoop;

    #endregion Private Fields

    #region Properties

    /// <summary>
    /// The engine's state feed: every accounting change (an ingested fill,
    /// a new market price) ships the complete snapshot; subscribing replays
    /// the current state — the future UI panels resolve THIS.
    /// </summary>
    public IObservable<PnlSnapshot> Snapshots => engine.StateChanges;

    #endregion Properties

    #region Public Methods

    /// <inheritdoc/>
    public Task InitializeAsync(CancellationToken ct = default)
    {
        // The engine's boot replay ran during the startup walk already (the
        // engine registration precedes this one); subscribing now replays
        // its current snapshot and drives the first pricing pass.
        _stateSubscription = engine.StateChanges.Subscribe(
            HandleStateSnapshot,
            error => logger.LogError(error, "PnL feed: the state feed failed"));

        _syncLoop = RunLedgerSyncLoopAsync(_lifetimeCts.Token);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Stop the sync loop and the ticker pumps; the engine itself is an
    /// app-lifetime singleton that outlives this wrapper.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _lifetimeCts.Cancel();

        if (_syncLoop is { } loop)
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

        _stateSubscription?.Dispose();

        foreach (var subscription in _tickerSubscriptions.Values)
        {
            subscription.Dispose();
        }

        _tickerSubscriptions.Clear();
        _lifetimeCts.Dispose();
    }

    #endregion Public Methods

    #region Private Methods

    /// <summary>
    /// The engine's snapshot just changed: re-arm ticker subscriptions for
    /// the symbols the books still hold and drop the ones they no longer
    /// do. Runs serialized (the engine emits under its own gate).
    /// </summary>
    private void HandleStateSnapshot(PnlSnapshot snapshot)
    {
        var held = snapshot.Positions
            .Where(position => !position.IsFlat)
            .Select(position => position.Symbol)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var symbol in _tickerSubscriptions.Keys)
        {
            if (held.Contains(symbol))
            {
                continue;
            }

            if (_tickerSubscriptions.TryRemove(symbol, out var dropped))
            {
                dropped.Dispose();
                logger.LogInformation("PnL feed: stopped pricing {Symbol} (position flat)", symbol);
            }
        }

        foreach (var symbol in held)
        {
            EnsureTickerSubscription(symbol);
        }
    }

    /// <summary>
    /// Start one symbol's ticker pump: every price flows through
    /// <see cref="PnlAccountingService.UpdateMarketPriceAsync"/> while the
    /// position lasts. A refused subscription (an active adapter that does
    /// not serve the symbol) logs and defers to the next state change.
    /// </summary>
    private void EnsureTickerSubscription(string symbolText)
    {
        if (_tickerSubscriptions.ContainsKey(symbolText))
        {
            return;
        }

        try
        {
            var symbol = Symbol.Parse(symbolText);
            var stream = marketData
                .SubscribeTicker(symbol)
                .Select(ticker => Observable.FromAsync(
                    () => engine.UpdateMarketPriceAsync(symbol, ticker.LastPrice, _lifetimeCts.Token)))
                .Concat();

            _tickerSubscriptions[symbolText] = stream.Subscribe(
                _ => { },
                error =>
                {
                    logger.LogWarning(
                        error,
                        "PnL feed: ticker stream for {Symbol} failed; it re-arms on the next state change",
                        symbolText);

                    if (_tickerSubscriptions.TryRemove(symbolText, out var failed))
                    {
                        failed.Dispose();
                    }
                });

            logger.LogInformation("PnL feed: pricing {Symbol} for the unrealized side", symbolText);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "PnL feed: no ticker available for {Symbol}; the unrealized side stays at its last value",
                symbolText);
        }
    }

    /// <summary>
    /// The ledger re-sync loop: one failed pass never kills the feed — the
    /// next tick retries, and the engine's own writes stay consistent by
    /// construction (books move only after a successful persist).
    /// </summary>
    private async Task RunLedgerSyncLoopAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(LedgerSyncInterval);
            while (await timer.WaitForNextTickAsync(ct))
            {
                try
                {
                    await engine.SyncWithLedgerAsync(ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "PnL feed: ledger sync failed; retrying on the next tick");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Disposal: the loop is expected to die here.
        }
    }

    #endregion Private Methods
}
