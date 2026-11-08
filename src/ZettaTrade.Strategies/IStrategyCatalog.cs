namespace ZettaTrade.Strategies;

/// <summary>
/// The discovery surface of the strategy seam (T-111): the strategies the
/// plugin scan registered, fixed for the session (the scan runs once at
/// load, never again). The host consumes <see cref="All"/> in load order;
/// consumers resolving one implementation by id (a backtest replay, a
/// settings row) use <see cref="Get"/>.
/// </summary>
public interface IStrategyCatalog
{
    /// <summary>
    /// The discovered strategies in load order (the registration order the
    /// scan produced). The first registration of a duplicated id wins; the
    /// set never changes for the session lifetime.
    /// </summary>
    IReadOnlyList<IStrategy> All { get; }

    /// <summary>
    /// Resolve one strategy by its canonical id. Unknown ids resolve
    /// quietly to null - an absent plugin is an absence, not an error.
    /// </summary>
    IStrategy? Get(string strategyId);
}
