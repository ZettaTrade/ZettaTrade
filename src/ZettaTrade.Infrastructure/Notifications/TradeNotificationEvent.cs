namespace ZettaTrade.Infrastructure.Notifications;

using ZettaTrade.Core.Enums;

/// <summary>
/// One mapped trade event (T-260): the journaled outcome's structured
/// facts, translated once by the App bridge from the notification
/// center's raw event, so every sink renders the same truth. The fields
/// are the journal row's own — a sink composes its user-facing shape from
/// them and never fabricates a fact the event does not carry.
/// </summary>
/// <param name="Kind">Which journaled outcome the event announces.</param>
/// <param name="Symbol">The normalized symbol behind the outcome; empty when the journal could not recover it.</param>
/// <param name="Side">The fill's side (Buy/Sell); unused (a plain Buy placeholder) for the cancels and the rejections.</param>
/// <param name="Quantity">The fill's traded quantity; unused (zero) for the cancels and the rejections.</param>
/// <param name="Price">The journaled row's own price for a fill; null when the surface carries no price fact (a fill without one is a surface-contract fault the composing sink degrades loudly — never a fabricated zero).</param>
/// <param name="Exchange">The journal row's exchange id behind the outcome ("paper", "bybit", …).</param>
/// <param name="Reason">The rejection reason for a rejection; empty for the rest.</param>
/// <param name="Lane">The trading lane the outcome arrived under (the message's paper/live tag; the sink-domain parallel of the App's own lane enum).</param>
public sealed record TradeNotificationEvent(
    TradeNotificationKind Kind,
    string Symbol,
    OrderSide Side,
    decimal Quantity,
    decimal? Price,
    string Exchange,
    string Reason,
    TradeNotificationLane Lane);
