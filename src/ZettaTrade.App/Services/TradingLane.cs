namespace ZettaTrade.App.Services;

/// <summary>
/// The trading lane of the workspace's traded surface: the resolved state the
/// status-bar chip renders and every flip decision consults.
/// </summary>
/// <remarks>
/// The mapping is owned by <see cref="TradingModeService.ResolveLane"/>:
/// <see cref="Paper"/> covers the shipped simulator (demo/paper); a venue
/// surface classifies by its <c>T-015</c> endpoint identity — <see cref="Testnet"/>
/// when the configured record carries the testnet flag, <see cref="Live"/>
/// otherwise. An identity that cannot resolve from the registry is an error,
/// never a substituted default (trading-safety: a real-money lane must not be
/// reachable "by default").
/// </remarks>
public enum TradingLane
{
    /// <summary>Demo/paper: simulated fills against live market prices, no real funds.</summary>
    Paper,

    /// <summary>Testnet: a venue surface whose configured endpoints carry the testnet identity.</summary>
    Testnet,

    /// <summary>Live: a venue surface whose configured endpoints do not carry the testnet identity.</summary>
    Live,
}
