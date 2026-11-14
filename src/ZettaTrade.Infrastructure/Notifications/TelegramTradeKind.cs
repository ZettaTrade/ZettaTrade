namespace ZettaTrade.Infrastructure.Notifications;

/// <summary>
/// The trade-event vocabulary the Telegram notification sink distinguishes
/// (T-041): the three journaled order outcomes the user opts into
/// per type. The opt-in flags are separate settings keys, one per kind —
/// a kind without its flag stays a quiet no-sink (the unconfigured state),
/// never a defaulted send.
/// </summary>
public enum TelegramTradeKind
{
    /// <summary>A journaled fill (the Filled row's terminal outcome).</summary>
    Fill,

    /// <summary>An accepted cancellation the journal recorded.</summary>
    Cancel,

    /// <summary>A rejected order the journal recorded.</summary>
    Reject,
}
