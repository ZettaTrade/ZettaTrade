namespace ZettaTrade.Infrastructure.Notifications;

using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// One trade-notification sink (T-260): a registered consumer of the
/// mapped journaled trade outcomes. The App bridge feeds every registered
/// sink the same structured event; each sink composes its own user-facing
/// shape (the Telegram sink composes its message text, the webhook batch
/// will render its own JSON) and routes it on its own opt-in flags.
/// <para>
/// The contract is BEST-EFFORT end to end: a sink never faults — its
/// <see cref="HandleAsync"/> completes for every input, a failure
/// degrading into the sink's own logged warning lane exactly once, no
/// retries — never blocks a caller beyond its own bounded work, and never
/// touches the trading flow. A sink that breaks the never-faults contract
/// is still contained: the bridge's dispatch degrades the aggregate fault
/// and keeps serving the stream.
/// </para>
/// </summary>
public interface INotificationSink
{
    /// <summary>
    /// Handle one mapped trade event, best-effort: routing (the sink's own
    /// opt-in flags), composition and delivery all live inside this call,
    /// and the returned task never faults. Cancellation is cooperative on
    /// top of the sink's own bounded work — a cancelled send is the same
    /// degraded non-delivery as any other failure.
    /// </summary>
    /// <param name="tradeEvent">The mapped trade event (the bridge's own vocabulary).</param>
    /// <param name="cancellationToken">Cooperative cancellation of the sink's delivery.</param>
    /// <returns>A task completing when the sink has finished (or degraded) the event.</returns>
    Task HandleAsync(TradeNotificationEvent tradeEvent, CancellationToken cancellationToken = default);
}
