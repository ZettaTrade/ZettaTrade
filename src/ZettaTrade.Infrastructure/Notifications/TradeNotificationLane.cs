namespace ZettaTrade.Infrastructure.Notifications;

/// <summary>
/// The trading lane of a mapped trade event (T-260): the sink-domain
/// parallel of the App's own lane enum — the sink vocabulary stays
/// sink-owned, the mapping is explicit at the bridge, and the two enums
/// can evolve independently without a shared-file move.
/// </summary>
public enum TradeNotificationLane
{
    /// <summary>Demo/paper: simulated fills against live market prices, no real funds.</summary>
    Paper,

    /// <summary>Testnet: a venue surface whose configured endpoints carry the testnet identity.</summary>
    Testnet,

    /// <summary>Live: a venue surface whose configured endpoints do not carry the testnet identity.</summary>
    Live,
}
