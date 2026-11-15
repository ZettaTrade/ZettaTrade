namespace ZettaTrade.Infrastructure.Notifications;

/// <summary>
/// The trade-event vocabulary the notification sinks distinguish (T-260):
/// the three journaled order outcomes the user opts into per type. Each
/// sink carries its own per-kind opt-in flags (the routing keys are the
/// sink's own); a kind without its flag stays a quiet no-sink (the
/// unconfigured state), never a defaulted send.
/// </summary>
public enum TradeNotificationKind
{
    /// <summary>A journaled fill (the Filled row's terminal outcome).</summary>
    Fill,

    /// <summary>An accepted cancellation the journal recorded.</summary>
    Cancel,

    /// <summary>A rejected order the journal recorded.</summary>
    Reject,
}
