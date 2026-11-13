namespace ZettaTrade.Infrastructure.Persistence;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// The persistent portfolio-value history (T-209): the trading accounts'
/// snapshot rows (per-account value + allocation) written on a bounded
/// cadence and read back for the balances panel's curve. Writes are plain
/// inserts (each pass is one new row; the (ExchangeId, TimestampUtc) key
/// rejects a true duplicate), reads are newest-first and bounded.
/// </summary>
public interface IPortfolioValueStore
{
    /// <summary>Persist one snapshot row.</summary>
    Task WriteAsync(PortfolioValueSnapshotRow snapshot, CancellationToken cancellationToken = default);

    /// <summary>
    /// Read one account's most recent rows, newest first. The limit clamps
    /// into 1..<see cref="PortfolioValueStoreLimits.MaxRecentBatch"/>.
    /// </summary>
    Task<IReadOnlyList<PortfolioValueSnapshotRow>> GetRecentAsync(
        string exchangeId,
        int limit,
        CancellationToken cancellationToken = default);
}
