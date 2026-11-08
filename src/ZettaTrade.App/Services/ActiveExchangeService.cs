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
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.Enums;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Core.ValueObjects;
using ZettaTrade.Infrastructure.Exchange;

/// <summary>
/// The active-exchange facade: the one <see cref="IMarketDataAdapter"/> the
/// whole presentation layer resolves. Every member forwards to the exchange
/// currently picked in the toolbar selector, so a switch is context-wide by
/// construction — no consumer can accidentally stay on the previous exchange.
/// </summary>
/// <remarks>
/// Stream subscriptions live across a switch: the three stream members wrap
/// the inner subscription in a <c>Switch</c> over
/// <see cref="AdapterChanges"/>, so an exchange change atomically cancels
/// the previous exchange's stream and starts the new one. The start value of
/// the behavioral stream is the first registered adapter; the persisted
/// choice (resolved in <see cref="InitializeAsync"/> during the startup
/// walk, before any consumer is constructed) activates with no duplicate
/// persistence write.
/// </remarks>
public sealed class ActiveExchangeService : IMarketDataAdapter, IAsyncInitializable
{
    #region Constants

    /// <summary>Settings section for workspace-level UI context choices.</summary>
    private const string SettingsSection = "Workspace";

    /// <summary>Settings key holding the persisted active exchange id.</summary>
    public const string ExchangeIdSettingKey = "ExchangeId";

    #endregion Constants

    #region Properties

    private readonly IReadOnlyList<IMarketDataAdapter> _adapters;
    private readonly ISettingsService _settings;
    private readonly ILogger<ActiveExchangeService> _logger;
    private readonly BehaviorSubject<IMarketDataAdapter> _active;
    private readonly ReplaySubject<string> _unavailablePersistedChoices = new(bufferSize: 1);

    /// <summary>All adapters available to the selector, in registration order.</summary>
    public IReadOnlyList<IMarketDataAdapter> Registered => _adapters;

    /// <summary>The adapter the facade currently forwards to.</summary>
    public IMarketDataAdapter Current => _active.Value;

    /// <summary>
    /// Behavioral stream of the active adapter: emits the current adapter on
    /// subscribe (so late subscribers bootstrap without a request/response
    /// dance) and every successful switch after that.
    /// </summary>
    public IObservable<IMarketDataAdapter> AdapterChanges => _active;

    /// <summary>
    /// Emits the raw persisted exchange id whenever the startup restore could
    /// not honor it (not registered) and fell back to the default — the
    /// consumer turns this into the loud user-facing fallback warning.
    /// </summary>
    /// <remarks>
    /// The notice is buffered (the last one replays to late subscribers):
    /// the restore runs during the startup walk, before the status bar is
    /// constructed, so the warning must not be lost between the facade and
    /// its listener.
    /// </remarks>
    public IObservable<string> UnavailablePersistedChoices => _unavailablePersistedChoices;

    /// <inheritdoc/>
    public string Id => Current.Id;

    /// <inheritdoc/>
    public string Name => Current.Name;

    /// <inheritdoc/>
    public bool IsConnected => Current.IsConnected;

    /// <inheritdoc/>
    public ExchangeCapabilities Capabilities => Current.Capabilities;

    #endregion Properties

    #region Constructor

    /// <summary>
    /// Create the facade over an explicitly composed, non-empty adapter
    /// catalog (the default is the first entry until
    /// <see cref="InitializeAsync"/> applies the persisted choice).
    /// </summary>
    public ActiveExchangeService(
        IReadOnlyList<IMarketDataAdapter> adapters,
        ISettingsService settings,
        ILogger<ActiveExchangeService> logger)
    {
        if (adapters.Count == 0)
        {
            throw new ArgumentException(
                "At least one exchange adapter must be registered.",
                nameof(adapters));
        }

        _adapters = adapters;
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _active = new BehaviorSubject<IMarketDataAdapter>(adapters[0]);
    }

    #endregion Constructor

    #region Public Methods

    /// <summary>
    /// Apply the persisted choice: a registered id activates as a restore
    /// (no settings rewrite); a missing or unregistered id starts on the
    /// default, warning-backed — never a silent substitution.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var persisted = await _settings.GetAsync<string>(SettingsSection, ExchangeIdSettingKey);
        if (string.IsNullOrWhiteSpace(persisted))
        {
            _logger.LogInformation("No persisted exchange choice; starting on '{Name}'", Current.Name);
            return;
        }

        var match = _adapters.FirstOrDefault(adapter =>
            string.Equals(adapter.Id, persisted, StringComparison.OrdinalIgnoreCase));

        if (match is null)
        {
            _logger.LogWarning(
                "Persisted exchange '{PersistedExchangeId}' is not registered; staying on '{Name}'",
                persisted,
                Current.Name);
            _unavailablePersistedChoices.OnNext(persisted);
            return;
        }

        if (ReferenceEquals(match, Current))
        {
            return;
        }

        _logger.LogInformation("Restored persisted exchange '{Name}'", match.Name);
        Activate(match);
    }

    /// <summary>
    /// Switch the active exchange: the catalog must carry the id (unknown ids
    /// throw — no defaults), the choice persists first, and only then the
    /// behavioral stream flips every consumer atomically. An id already
    /// active is a no-op (no rewrite, no duplicate emission).
    /// </summary>
    public async Task SwitchToAsync(string exchangeId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(exchangeId))
        {
            throw new ArgumentException("The exchange id must not be empty.", nameof(exchangeId));
        }

        var match = _adapters.FirstOrDefault(adapter =>
            string.Equals(adapter.Id, exchangeId, StringComparison.OrdinalIgnoreCase));

        if (match is null)
        {
            throw new ArgumentException(
                $"No registered exchange adapter has id '{exchangeId}'.",
                nameof(exchangeId));
        }

        if (ReferenceEquals(match, Current))
        {
            return;
        }

        await _settings.SetAsync(SettingsSection, ExchangeIdSettingKey, match.Id);
        _logger.LogInformation("Exchange switched to '{Name}'", match.Name);
        Activate(match);
    }

    #endregion Public Methods

    #region Market Data Forwarding

    /// <inheritdoc/>
    public Task<IReadOnlyList<Symbol>> GetSymbolsAsync(CancellationToken ct = default) =>
        Current.GetSymbolsAsync(ct);

    /// <inheritdoc/>
    public Task<IReadOnlyList<Bar>> GetBarsAsync(
        Symbol symbol,
        TimeFrame timeFrame,
        DateTimeOffset from,
        DateTimeOffset? to = null,
        int limit = 500,
        CancellationToken ct = default) =>
        Current.GetBarsAsync(symbol, timeFrame, from, to, limit, ct);

    /// <inheritdoc/>
    public IObservable<Ticker> SubscribeTicker(Symbol symbol) => Attach(adapter => adapter.SubscribeTicker(symbol));

    /// <inheritdoc/>
    public IObservable<OrderBook> SubscribeOrderBook(Symbol symbol, int depth = 20) =>
        Attach(adapter => adapter.SubscribeOrderBook(symbol, ResolveBookDepth(adapter, depth)));

    /// <inheritdoc/>
    public Task ConnectAsync(CancellationToken ct = default) => Current.ConnectAsync(ct);

    /// <inheritdoc/>
    public Task DisconnectAsync(CancellationToken ct = default) => Current.DisconnectAsync(ct);

    /// <summary>
    /// Concrete adapters are owned by the app-lifetime container; disposing
    /// the facade only closes its change streams so subscribers wind down.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        _active.Dispose();
        _unavailablePersistedChoices.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public IObservable<MarketTrade> SubscribeTrades(Symbol symbol) => Attach(adapter => adapter.SubscribeTrades(symbol));

    /// <summary>
    /// Wrap a per-adapter stream start so every adapter change re-switches
    /// the running stream to the new exchange's stream (the old subscription
    /// is cancelled with it — in-flight data of the previous adapter is
    /// dropped, not mixed).
    /// </summary>
    private IObservable<TStream> Attach<TStream>(Func<IMarketDataAdapter, IObservable<TStream>> start) =>
        _active.Select(start).Switch();

    /// <summary>
    /// Resolve the requested order book depth against the adapter it will
    /// ride on: an adapter without a declared wire constraint (the demo one,
    /// serving any depth) gets the request unchanged, while a depth-locked
    /// one (Bybit serves 1 and 50, Binance a stepped range) resolves a
    /// generic display default like 20 to its nearest served depth — the
    /// raw default used to hit the adapter's explicit rejection and empty
    /// the book widget with a stream error.
    /// </summary>
    private int ResolveBookDepth(IMarketDataAdapter adapter, int requestedDepth)
    {
        var resolved = adapter is ExchangeAdapterBase concrete
            ? ExchangeAdapterBase.ResolveBookDepth(concrete.ServedBookDepths, requestedDepth)
            : requestedDepth;

        if (resolved != requestedDepth)
        {
            _logger.LogDebug(
                "Order book depth {RequestedDepth} resolved to {ResolvedDepth} on '{ExchangeId}'",
                requestedDepth,
                resolved,
                adapter.Id);
        }

        return resolved;
    }

    #endregion Market Data Forwarding

    #region Private Methods

    /// <summary>Flip the behavioral stream (the restore and user switches share this).</summary>
    private void Activate(IMarketDataAdapter target) => _active.OnNext(target);

    #endregion Private Methods
}

