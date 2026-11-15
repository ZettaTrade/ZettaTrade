namespace ZettaTrade.Infrastructure.Exchange;

using System.Reactive.Linq;
using ZettaTrade.Core.Entities;
using ZettaTrade.Core.ValueObjects;

/// <summary>
/// The liquidation-feed capability of a market-data adapter (T-242). An
/// optional role interface in the Infrastructure Exchange module — the
/// consumers probe <c>Current is ILiquidationFeedProvider</c> the same way
/// they probe the other optional capabilities, so an adapter without the
/// venue's forced-order wire degrades to an honest absence (an empty
/// stream), never a guess.
/// </summary>
/// <remarks>
/// <para>
/// The wire the capability rides is a DERIVATIVES wire (forced orders exist
/// on leveraged markets), but the symbol request is NOT perp-only — unlike
/// the funding and open-interest history capabilities. The venues serve the
/// forced orders under the contract's own concatenated wire name, which the
/// app's typed symbol maps onto in either form: the spot twin
/// ("BTC/USDT") and the settle-marked perpetual ("BTC/USDT:USDT") name the
/// same contract text, and a liquidation feed served for one is served for
/// the other. A spot-typed request on a derivatives-only venue is exactly
/// the tape's default path — refusing it would strand the surface.
/// </para>
/// <para>
/// Every returned event carries the wire facts as served (see
/// <see cref="LiquidationEvent"/>); the adapters normalize the side. The
/// streams are live-only: the wires replay nothing, so the events lost
/// during a reconnect gap stay lost — the tape is the venue's live forced
/// flow, never a reconstructed history.
/// </para>
/// </remarks>
public interface ILiquidationFeedProvider
{
    /// <summary>
    /// Subscribe the venue's forced-liquidation events for one symbol's
    /// contract (the wire name rides <see cref="Symbol.GetExchangeFormat"/>).
    /// Disposing the subscription releases the symbol's wire subscription;
    /// the venue's live-only flow keeps no history for a re-subscription to
    /// replay.
    /// </summary>
    /// <param name="symbol">The symbol whose contract's liquidations subscribe.</param>
    /// <param name="ct">The subscription-start's cancellation (the start only; the wire rides the adapter's own lifetime).</param>
    /// <returns>The event stream of the symbol's contract, live from subscription.</returns>
    IObservable<LiquidationEvent> SubscribeLiquidations(Symbol symbol, CancellationToken ct = default);
}
