namespace ZettaTrade.Infrastructure.Persistence;

/// <summary>
/// The portfolio-value store's read/write bounds, named so both the store
/// and its consumers clamp through one constant.
/// </summary>
public static class PortfolioValueStoreLimits
{
    /// <summary>Growth cap in <see cref="IPortfolioValueStore.GetRecentAsync"/>; the curve is a bounded read.</summary>
    public const int MaxRecentBatch = 1000;
}
