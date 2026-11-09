namespace ZettaTrade.App.Services;

using System;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using Livery.Localization;
using Microsoft.Extensions.Logging;
using ZettaTrade.Core.Interfaces;
using ZettaTrade.Infrastructure.Exchange;

/// <summary>
/// The trading-mode service: resolves and broadcasts the active
/// <see cref="TradingLane"/> of the workspace's traded surface and owns the
/// go-live confirmation gate.
/// </summary>
/// <remarks>
/// The shipped stand-up resolution parks on the paper simulator
/// (<see cref="PaperTradingAdapter.ExchangeId"/>). With the delegating
/// surface wired (T-127), a lane flip re-resolves through the same static
/// set of identities; the resolution rules below never changed. The
/// resolver demands an identity the <c>T-015</c> registry snapshot knows:
/// an unparsable one is an error, never a substituted default (a
/// real-money surface must be unreachable "by default").
/// </remarks>
public sealed class TradingModeService
{
    #region Constants

    /// <summary>Localized go-live prompt key (the flip confirmation's body).</summary>
    public const string ConfirmGoLiveKey = "Trading.ConfirmGoLive";

    #endregion Constants

    #region Properties

    private readonly IConfirmationService _confirmation;
    private readonly ILogger<TradingModeService> _logger;
    private readonly BehaviorSubject<TradingLane> _lane;

    /// <summary>
    /// The traded-surface subscription (T-127): the venue surface's lane
    /// flips re-resolve into the standing stream. Null when the delegating
    /// surface is absent (hand-construction, the pre-T-127 shape) — the
    /// optional parameter keeps every existing constructor call compiling.
    /// </summary>
    private readonly IDisposable? _surfaceSubscription;

    /// <summary>
    /// Behavioral stream of the resolved lane: emits the stood-up value on
    /// subscribe (late subscribers bootstrap without a request/response
    /// dance) and every later flip after that. Per-window chips (T-103)
    /// attach to this stream.
    /// </summary>
    public IObservable<TradingLane> Lane => _lane;

    /// <summary>The lane the stream currently stands for.</summary>
    public TradingLane CurrentLane => _lane.Value;

    #endregion Properties

    #region Constructor

    /// <summary>
    /// Create the service over the traded adapter and the endpoint registry
    /// snapshot (initialized by the startup walk before any adapter, so the
    /// configured endpoint records are already loaded). The optional surface
    /// subscription (T-127) is the venue-trading extension: a delegated
    /// surface flip re-resolves the lane live — the chip reacts without a
    /// restart. Absent (the earlier signature) the resolution stays the
    /// standing once-at-construction value.
    /// </summary>
    /// <param name="tradedAdapter">The workspace's traded surface.</param>
    /// <param name="endpoints">The T-015 endpoint registry snapshot.</param>
    /// <param name="confirmation">The human-confirmation gate.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="testnetSurfaces">
    /// The optional delegating trading surface whose lane flips re-resolve.
    /// </param>
    public TradingModeService(
        ITradingAdapter tradedAdapter,
        ExchangeEndpointRegistry endpoints,
        IConfirmationService confirmation,
        ILogger<TradingModeService> logger,
        TradingSurfaceService? testnetSurfaces = null)
    {
        _confirmation = confirmation
            ?? throw new ArgumentNullException(nameof(confirmation));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _lane = new BehaviorSubject<TradingLane>(
            ResolveLane(
                tradedAdapter ?? throw new ArgumentNullException(nameof(tradedAdapter)),
                endpoints ?? throw new ArgumentNullException(nameof(endpoints))));

        if (testnetSurfaces is not null)
        {
            _surfaceSubscription = testnetSurfaces.SurfaceChanges.Subscribe(
                surface =>
                {
                    try
                    {
                        var lane = ResolveLane(surface, endpoints);
                        if (_lane.Value != lane)
                        {
                            _logger.LogInformation("Trading lane re-resolves to {Lane} after the surface flip", lane);
                            _lane.OnNext(lane);
                        }
                    }
                    catch (InvalidOperationException ex)
                    {
                        _logger.LogError(
                            ex,
                            "The flipped surface's lane does not resolve; the chip holds the standing lane");
                    }
                },
                ex => _logger.LogError(ex, "The traded-surface stream stopped; the standing lane holds"));
        }
    }

    #endregion Constructor

    #region Public Methods

    /// <summary>
    /// Resolve one traded surface's lane from the endpoint registry
    /// snapshot: the shipped paper simulator parks on
    /// <see cref="TradingLane.Paper"/> (the wrapper trades simulated fills
    /// regardless of the market-data source it rides); a venue surface
    /// resolves by its registry record's identity and testnet flag. An
    /// identity outside both maps is an error — a lane would be a
    /// fabrication, and a fabricated lane is exactly the state the chip
    /// exists to prevent.
    /// </summary>
    /// <param name="tradedAdapter">The traded surface to classify.</param>
    /// <param name="endpoints">The endpoint registry snapshot.</param>
    /// <returns>The resolved lane.</returns>
    /// <exception cref="InvalidOperationException">
    /// The traded surface's identity does not resolve from the registry
    /// snapshot.
    /// </exception>
    public static TradingLane ResolveLane(
        ITradingAdapter tradedAdapter,
        ExchangeEndpointRegistry endpoints)
    {
        if (tradedAdapter is null)
        {
            throw new ArgumentNullException(nameof(tradedAdapter));
        }
        else if (endpoints is null)
        {
            throw new ArgumentNullException(nameof(endpoints));
        }

        var identity = tradedAdapter.Id;

        if (string.Equals(
                identity,
                PaperTradingAdapter.ExchangeId,
                StringComparison.OrdinalIgnoreCase))
        {
            return TradingLane.Paper;
        }

        if (string.Equals(
                identity,
                ExchangeEndpointRegistry.BinanceKey,
                StringComparison.OrdinalIgnoreCase))
        {
            return ResolveEndpointLane(endpoints.Binance.IsTestnet);
        }

        if (string.Equals(
                identity,
                ExchangeEndpointRegistry.BybitKey,
                StringComparison.OrdinalIgnoreCase))
        {
            return ResolveEndpointLane(endpoints.Bybit.IsTestnet);
        }

        throw new InvalidOperationException(
            $"The traded surface '{identity}' has no endpoint identity in the registry snapshot; the trading lane cannot be resolved.");
    }

    /// <summary>
    /// The flip confirmation gate: a flip INTO
    /// <see cref="TradingLane.Live"/> requires an
    /// <see cref="IConfirmationService"/> prompt on the flip itself and
    /// reports the human's answer; any other target needs no prompt.
    /// </summary>
    /// <remarks>
    /// The runtime flip call site arrives with the venue-trading surface
    /// (T-127): today the paper surface cannot flip the lane, so no caller
    /// exists — the gate is the invariant's keeper the site must ride.
    /// </remarks>
    /// <param name="target">The lane the caller intends to flip into.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True when the flip may proceed (confirmed, or not gated).</returns>
    public async Task<bool> ConfirmLaneFlipAsync(
        TradingLane target,
        CancellationToken ct = default)
    {
        if (target != TradingLane.Live)
        {
            return true;
        }

        return await _confirmation.RequestConfirmationAsync(
            new ConfirmationRequest
            {
                MessageKey = ConfirmGoLiveKey,
            },
            ct);
    }

    #endregion Public Methods

    #region Private Methods

    /// <summary>The testnet flag's lane projection (the record's identity source).</summary>
    private static TradingLane ResolveEndpointLane(bool isTestnet) =>
        isTestnet ? TradingLane.Testnet : TradingLane.Live;

    #endregion Private Methods
}
