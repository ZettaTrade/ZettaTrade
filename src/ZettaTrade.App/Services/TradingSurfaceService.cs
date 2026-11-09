namespace ZettaTrade.App.Services;

using System;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
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
/// The workspace's traded surface (T-127): the one <see cref="ITradingAdapter"/>
/// registration every consumer resolves, delegating between the paper
/// simulator and the Bybit testnet venue. The shipped default stays the
/// paper lane (bit-identical to the pre-T-127 behavior); the venue surface
/// activates ONLY when all three of the operator's deliberate choices line
/// up: the toolbar picks the Bybit exchange, the endpoint snapshot marks the
/// Bybit record testnet, and the settings toggle
/// <c>Exchange/TestnetTradingEnabled</c> is armed (default off). Every
/// order still rides the unchanged upstream pipeline
/// (<see cref="IRiskManager"/> then <see cref="IConfirmationService"/>) —
/// this service is the lane PICKER, never a second safety gate to bypass.
/// </summary>
/// <remarks>
/// The surface identity is deliberately the two delegated adapters' own ids
/// ("paper" / "bybit", the trading-mode resolver's known set); a flip
/// re-points every member forward, so an open dialog, the MCP tool lane and
/// the trading-mode chip follow the new surface transparently
/// (<see cref="SurfaceChanges"/> carries the stream). Nothing to dispose is
/// owned by the delegated adapters themselves — they are app-lifetime
/// singletons owned by the container.
/// </remarks>
public sealed class TradingSurfaceService : ITradingAdapter, IAsyncInitializable
{
    #region Constants

    /// <summary>Settings section carrying the venue-trading toggle (the exchange domain's own).</summary>
    public const string SettingsSection = "Exchange";

    /// <summary>Settings key harbour-arming the testnet venue surface (default off: paper stays the lane).</summary>
    public const string TestnetTradingSettingKey = "TestnetTradingEnabled";

    #endregion Constants

    #region Properties

    private readonly ActiveExchangeService _activeExchanges;
    private readonly PaperTradingAdapter _paper;
    private readonly BybitTradingAdapter _testnetVenue;
    private readonly ISettingsService _settings;
    private readonly ExchangeEndpointRegistry _endpoints;
    private readonly ILogger<TradingSurfaceService> _logger;

    /// <summary>The behavioral surface stream: bootstraps on the paper adapter (the shipped default lane).</summary>
    private readonly BehaviorSubject<ITradingAdapter> _surface;

    private IDisposable? _recoverySubscription;
    private bool _testnetTradingEnabled;

    /// <summary>The adapter chain the workspace currently trades through in (the behavioral stream's head).</summary>
    public ITradingAdapter Current => _surface.Value;

    /// <summary>
    /// The traded-surface stream: emits the standing surface on subscribe and
    /// every later lane flip. The trading-mode service re-resolves the chip
    /// lane from this stream; the delegation means no consumer needs it.
    /// </summary>
    public IObservable<ITradingAdapter> SurfaceChanges => _surface;

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
    /// Create the surface over the app's two adapters and the streams the
    /// lane decision rides. The standing surface starts on the paper
    /// simulator — before and after initialization, a consumer resolving
    /// <see cref="ITradingAdapter"/> never sees a half-configured lane.
    /// </summary>
    /// <param name="activeExchanges">The active-exchange facade (the exchange id the venue surface keys on).</param>
    /// <param name="paper">The paper simulator (the shipped default surface).</param>
    /// <param name="testnetVenue">The Bybit testnet venue trading adapter.</param>
    /// <param name="settings">The settings service (the toggle's read and its live stream).</param>
    /// <param name="endpoints">The endpoint snapshot (the Bybit record's testnet flag).</param>
    /// <param name="logger">Logger.</param>
    public TradingSurfaceService(
        ActiveExchangeService activeExchanges,
        PaperTradingAdapter paper,
        BybitTradingAdapter testnetVenue,
        ISettingsService settings,
        ExchangeEndpointRegistry endpoints,
        ILogger<TradingSurfaceService> logger)
    {
        _activeExchanges = activeExchanges ?? throw new ArgumentNullException(nameof(activeExchanges));
        _paper = paper ?? throw new ArgumentNullException(nameof(paper));
        _testnetVenue = testnetVenue ?? throw new ArgumentNullException(nameof(testnetVenue));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _endpoints = endpoints ?? throw new ArgumentNullException(nameof(endpoints));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _surface = new BehaviorSubject<ITradingAdapter>(_paper);
    }

    #endregion Constructor

    #region Initialization

    /// <summary>
    /// Read the toggle once at the startup walk's turn (the settings file is
    /// already loaded — the walk ordered this service after the settings
    /// service) and subscribe the two streams the lane decision rides: the
    /// toggle's live changes and the toolbar picker's adapter stream. A
    /// failure to arm either stream is logged and leaves the paper surface
    /// standing (a surface decision must never fall back to the venue lane).
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        _testnetTradingEnabled = await _settings.GetAsync<bool>(SettingsSection, TestnetTradingSettingKey);
        Recompute();

        // The merged lane-decision inputs: each source serialized on its own
        // side (the settings stream is synchronized upstream; the facade's
        // adapter stream is serialized here), the merge synchronized again
        // so the decision handler runs one-at-a-time — no lock, no race.
        var toggleChanges = _settings
            .Changes
            .Synchronize()
            .Where(change => string.Equals(change.Section, SettingsSection, StringComparison.Ordinal)
                && string.Equals(change.Key, TestnetTradingSettingKey, StringComparison.Ordinal));
        var pickerChanges = _activeExchanges.AdapterChanges.Synchronize();

        // A live toggle change re-arms the standing flag BEFORE the decision
        // re-runs — the handler reads the field, so a flip that only squirts
        // a Recompute at the stream (the earlier shape) would re-evaluate the
        // STALE arming forever. The payload reader covers both the settings
        // service's typed emission (SetAsync<T> ships the boxed T) and the
        // hot-reload path (a JsonElement); anything unparsable fails safe to
        // un-armed — a doubt never arms the venue.
        _recoverySubscription = pickerChanges
            .Select(_ => Current)
            .Merge(toggleChanges
                .Select(change => change.NewValue)
                .Do(armed => _testnetTradingEnabled = ReadArmedToggle(armed))
                .Select(_ => Current))
            .Synchronize()
            .Subscribe(
                _ => Recompute(),
                ex => _logger.LogError(ex, "The traded-surface decision stream stopped; the standing surface holds"));
    }

    /// <summary>
    /// Read the toggle's armed state off one change payload: the settings
    /// service's Set path ships the typed value, its hot-reload path ships a
    /// <c>JsonElement</c>; a removal (null) or anything else fails safe to
    /// un-armed — the venue surface requires all three gates, so a doubt
    /// leaves the paper lane standing.
    /// </summary>
    /// <param name="payload">The change's incoming value.</param>
    /// <returns>True only when the payload unambiguously arms the toggle.</returns>
    private static bool ReadArmedToggle(object? payload) => payload switch
    {
        bool armed => armed,
        JsonElement { ValueKind: JsonValueKind.True } => true,
        JsonElement { ValueKind: JsonValueKind.False } => false,
        _ => false,
    };

    /// <summary>
    /// Decide the standing surface from the armed inputs: the Bybit testnet
    /// venue requires the operator's settings toggle AND the toolbar's Bybit
    /// pick AND the endpoint snapshot's testnet flag (all three together —
    /// any one missing keeps the shipped paper lane). A change logs the flip
    /// exactly once, at the behavioral stream (the chip follows).
    /// </summary>
    private void Recompute()
    {
        var picker = _activeExchanges.Current;
        var venueWanted = _testnetTradingEnabled
            && string.Equals(
                picker.Id,
                BybitTradingAdapter.ExchangeId,
                StringComparison.OrdinalIgnoreCase)
            && _endpoints.Bybit.IsTestnet;

        var venue = venueWanted ? (ITradingAdapter)_testnetVenue : _paper;
        if (ReferenceEquals(venue, Current))
        {
            return;
        }

        _logger.LogInformation(
            "Traded surface flips to {SurfaceName} (toggle {Toggle}, picker {PickerId}, testnet flag {IsTestnet})",
            venue.Name,
            _testnetTradingEnabled,
            picker.Id,
            _endpoints.Bybit.IsTestnet);
        _surface.OnNext(venue);
    }

    #endregion Initialization

    #region Market data forwarding

    /// <inheritdoc/>
    public Task ConnectAsync(CancellationToken ct = default) => Current.ConnectAsync(ct);

    /// <inheritdoc/>
    public Task DisconnectAsync(CancellationToken ct = default) => Current.DisconnectAsync(ct);

    /// <inheritdoc/>
    public Task<IReadOnlyList<Symbol>> GetSymbolsAsync(CancellationToken ct = default) =>
        Current.GetSymbolsAsync(ct);

    /// <inheritdoc/>
    public IObservable<Ticker> SubscribeTicker(Symbol symbol) => Current.SubscribeTicker(symbol);

    /// <inheritdoc/>
    public IObservable<OrderBook> SubscribeOrderBook(Symbol symbol, int depth = 20) =>
        Current.SubscribeOrderBook(symbol, depth);

    /// <inheritdoc/>
    public IObservable<MarketTrade> SubscribeTrades(Symbol symbol) => Current.SubscribeTrades(symbol);

    /// <inheritdoc/>
    public Task<IReadOnlyList<Bar>> GetBarsAsync(
        Symbol symbol,
        TimeFrame timeFrame,
        DateTimeOffset from,
        DateTimeOffset? to = null,
        int limit = 500,
        CancellationToken ct = default) =>
        Current.GetBarsAsync(symbol, timeFrame, from, to, limit, ct);

    #endregion Market data forwarding

    #region Trading forwarding

    /// <inheritdoc/>
    public Task<Order> PlaceOrderAsync(OrderRequest request, CancellationToken ct = default) =>
        Current.PlaceOrderAsync(request, ct);

    /// <inheritdoc/>
    public Task CancelOrderAsync(Symbol symbol, string orderId, CancellationToken ct = default) =>
        Current.CancelOrderAsync(symbol, orderId, ct);

    /// <inheritdoc/>
    public Task<IReadOnlyList<Order>> GetOpenOrdersAsync(CancellationToken ct = default) =>
        Current.GetOpenOrdersAsync(ct);

    /// <inheritdoc/>
    public Task<IReadOnlyList<Position>> GetPositionsAsync(CancellationToken ct = default) =>
        Current.GetPositionsAsync(ct);

    /// <inheritdoc/>
    public Task<IReadOnlyDictionary<string, Money>> GetBalancesAsync(CancellationToken ct = default) =>
        Current.GetBalancesAsync(ct);

    #endregion Trading forwarding

    #region IAsyncDisposable

    /// <summary>Release the decision subscription; the delegated adapters stay container-owned.</summary>
    public ValueTask DisposeAsync()
    {
        _recoverySubscription?.Dispose();
        _recoverySubscription = null;
        _surface.Dispose();
        return ValueTask.CompletedTask;
    }

    #endregion IAsyncDisposable
}
