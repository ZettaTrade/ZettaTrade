namespace ZettaTrade.Infrastructure.Fees;

/// <summary>
/// The fee-schedule lookup the fill stamping and the order ticket's estimate
/// line read (T-080). A role interface over the registry, so consumers never
/// depend on the settings-reading engine behind it: the lookup answers an
/// adapter identifier with that adapter's schedule — or nothing when the
/// adapter carries none, which is the honest unknown every fee surface
/// renders as a dash (never a zero that would hide the cost).
/// </summary>
public interface IFeeScheduleProvider
{
    /// <summary>
    /// The schedule configured for the given adapter, or nothing when the
    /// adapter carries no schedule. Synchronous by contract: it reads the
    /// in-memory snapshot the reactive settings already keep fresh.
    /// </summary>
    /// <param name="exchangeId">The exchange identifier the fees would charge on.</param>
    FeeSchedule? GetSchedule(string exchangeId);
}
